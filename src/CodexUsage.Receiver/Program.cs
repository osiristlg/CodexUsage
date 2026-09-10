using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodexUsage.Core;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;

var dataDirectory = Environment.GetEnvironmentVariable("CODEX_USAGE_RECEIVER_DATA");
if (string.IsNullOrWhiteSpace(dataDirectory))
    dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Usage Receiver");
dataDirectory = Path.GetFullPath(dataDirectory);
Directory.CreateDirectory(dataDirectory);
var settingsPath = Path.Combine(dataDirectory, "receiver-settings.json");
var databasePath = Path.Combine(dataDirectory, "usage.db");

if (args.Contains("--init", StringComparer.OrdinalIgnoreCase))
{
    if (!File.Exists(settingsPath)) ReceiverSettingsStore.Save(settingsPath, ReceiverSettings.Default);
    Console.WriteLine(settingsPath);
    return;
}

if (args.Length >= 3 && string.Equals(args[0], "--add-client", StringComparison.OrdinalIgnoreCase))
{
    var settings = ReceiverSettingsStore.Load(settingsPath);
    var clientId = args[1].Trim();
    var machineName = args[2].Trim();
    if (clientId.Length is < 1 or > 80 || machineName.Length is < 1 or > 100)
        throw new ArgumentException("Client ID or machine name is invalid.");
    Console.Write("Shared passphrase: ");
    var passphrase = ReadSecret();
    Console.WriteLine();
    var salt = AggregateProtocol.NewSalt();
    var key = AggregateProtocol.DeriveKey(passphrase, salt);
    var clients = settings.Clients.Where(c => !string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase)).ToList();
    clients.Add(new ClientAccess(clientId, machineName, Convert.ToBase64String(salt), Convert.ToBase64String(key), true));
    ReceiverSettingsStore.Save(settingsPath, settings with { Clients = clients });
    CryptographicOperations.ZeroMemory(key);
    Console.WriteLine($"Client '{clientId}' is ready. Enter the same passphrase in the client.");
    return;
}

var receiverSettings = ReceiverSettingsStore.Load(settingsPath);
await UsageDatabase.InitializeAsync(databasePath);

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.PropertyNameCaseInsensitive = false);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = receiverSettings.MaxPayloadBytes;
    options.Listen(IPAddress.Parse(receiverSettings.BindAddress), receiverSettings.Port);
});
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "Codex Usage Receiver",
    protocolVersion = AggregateProtocol.Version,
    clients = receiverSettings.Clients.Count(c => c.Enabled)
}));

app.MapGet("/api/v1/salt/{clientId}", (string clientId, HttpContext context) =>
{
    if (!NetworkPolicy.IsAllowed(context.Connection.RemoteIpAddress, receiverSettings.AllowedSubnets)) return Results.NotFound();
    var client = receiverSettings.Clients.FirstOrDefault(c => c.Enabled &&
        string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
    return client is null ? Results.NotFound() : Results.Ok(new { version = AggregateProtocol.Version, salt = client.Salt });
});

app.MapPost("/api/v1/exchange", async (EncryptedEnvelope envelope, HttpContext context) =>
{
    if (!NetworkPolicy.IsAllowed(context.Connection.RemoteIpAddress, receiverSettings.AllowedSubnets)) return Results.NotFound();
    var client = receiverSettings.Clients.FirstOrDefault(c => c.Enabled &&
        string.Equals(c.ClientId, envelope.ClientId, StringComparison.OrdinalIgnoreCase));
    if (client is null) return Results.NotFound();
    if (Math.Abs((DateTime.UtcNow - envelope.CreatedAtUtc.ToUniversalTime()).TotalMinutes) > 15)
        return Results.Unauthorized();

    byte[] key;
    try { key = Convert.FromBase64String(client.Key); }
    catch (FormatException) { return Results.StatusCode(StatusCodes.Status500InternalServerError); }
    try
    {
        SyncPayload payload;
        try { payload = AggregateProtocol.Decrypt<SyncPayload>(envelope, key); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        { return Results.Unauthorized(); }

        var validation = PayloadValidation.Validate(payload, receiverSettings.MaxRowsPerRequest);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        var result = await UsageDatabase.ReplaceAndSummarizeAsync(databasePath, client.ClientId, payload, envelope.RequestId);
        var namedMachines = new Dictionary<string, TokenCounts>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in result.Machines)
        {
            var name = receiverSettings.Clients.FirstOrDefault(c =>
                string.Equals(c.ClientId, pair.Key, StringComparison.OrdinalIgnoreCase))?.MachineName ?? pair.Key;
            if (namedMachines.ContainsKey(name)) name = $"{name} ({pair.Key[..Math.Min(8, pair.Key.Length)]})";
            namedMachines[name] = pair.Value;
        }
        var reply = new ExchangeReply(true, result.Duplicate ? "Already accepted" : "Accepted", DateTime.UtcNow,
            result.Combined, namedMachines);
        return Results.Ok(AggregateProtocol.Encrypt(client.ClientId, Guid.NewGuid().ToString(), DateTime.UtcNow, reply, key));
    }
    finally { CryptographicOperations.ZeroMemory(key); }
});

Console.WriteLine($"Codex Usage Receiver listening on http://{receiverSettings.BindAddress}:{receiverSettings.Port}");
Console.WriteLine($"Settings: {settingsPath}");
await app.RunAsync();

static string ReadSecret()
{
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
    return value.ToString();
}

internal sealed record ClientAccess(string ClientId, string MachineName, string Salt, string Key, bool Enabled);
internal sealed record ReceiverSettings(
    string BindAddress,
    int Port,
    IReadOnlyList<string> AllowedSubnets,
    IReadOnlyList<ClientAccess> Clients,
    int MaxPayloadBytes = 16 * 1024 * 1024,
    int MaxRowsPerRequest = 100_000)
{
    public static ReceiverSettings Default => new("127.0.0.1", 4747, ["127.0.0.0/8", "::1/128"], []);
}

internal static class ReceiverSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ReceiverSettings Load(string path)
    {
        if (!File.Exists(path)) Save(path, ReceiverSettings.Default);
        return JsonSerializer.Deserialize<ReceiverSettings>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Receiver settings are invalid.");
    }

    public static void Save(string path, ReceiverSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, path, true);
    }
}

internal static class NetworkPolicy
{
    public static bool IsAllowed(IPAddress? address, IReadOnlyList<string> ranges)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return ranges.Any(range => Contains(range, address));
    }

    private static bool Contains(string cidr, IPAddress address)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
            return false;
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length || prefix < 0 || prefix > networkBytes.Length * 8) return false;
        var wholeBytes = prefix / 8;
        var remainingBits = prefix % 8;
        for (var i = 0; i < wholeBytes; i++) if (networkBytes[i] != addressBytes[i]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (networkBytes[wholeBytes] & mask) == (addressBytes[wholeBytes] & mask);
    }
}

internal static class PayloadValidation
{
    public static string? Validate(SyncPayload payload, int maxRows)
    {
        if (payload.Kind is not ("incremental" or "full" or "query")) return "Unknown synchronization kind.";
        if (string.IsNullOrWhiteSpace(payload.MachineName) || payload.MachineName.Length > 100) return "Invalid machine name.";
        if (payload.RangeStartUtc.Kind != DateTimeKind.Utc || payload.RangeEndUtc.Kind != DateTimeKind.Utc ||
            payload.CombinedStartUtc.Kind != DateTimeKind.Utc || payload.CombinedEndUtc.Kind != DateTimeKind.Utc)
            return "All timestamps must be UTC.";
        if (payload.RangeEndUtc <= payload.RangeStartUtc || payload.RangeEndUtc - payload.RangeStartUtc > TimeSpan.FromDays(31))
            return "Invalid synchronization range.";
        if (payload.CombinedEndUtc <= payload.CombinedStartUtc || payload.CombinedEndUtc - payload.CombinedStartUtc > TimeSpan.FromDays(2))
            return "Invalid combined-total range.";
        if (payload.Rows.Count > maxRows) return "Too many aggregate rows.";
        if (payload.Kind == "query" && payload.Rows.Count != 0) return "Query requests cannot contain aggregate rows.";
        foreach (var row in payload.Rows)
        {
            if (row.BucketStartUtc.Kind != DateTimeKind.Utc || row.BucketStartUtc.Minute != 0 || row.BucketStartUtc.Second != 0 ||
                row.BucketStartUtc < payload.RangeStartUtc || row.BucketStartUtc >= payload.RangeEndUtc)
                return "An aggregate bucket is outside the declared range or not hour-aligned.";
            if (row.Model.Length is < 1 or > 100 || row.Project.Length is < 1 or > 200) return "Invalid aggregate label.";
            if (row.Tokens.Input < 0 || row.Tokens.CachedInput < 0 || row.Tokens.Output < 0 || row.Tokens.Reasoning < 0 || row.Tokens.Responses < 0)
                return "Negative aggregate value.";
        }
        return null;
    }
}

internal sealed record DatabaseResult(bool Duplicate, TokenCounts Combined, IReadOnlyDictionary<string, TokenCounts> Machines);

internal static class UsageDatabase
{
    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();

    public static async Task InitializeAsync(string path)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS usage (
              client_id TEXT NOT NULL,
              bucket_utc TEXT NOT NULL,
              project TEXT NOT NULL,
              model TEXT NOT NULL,
              input_tokens INTEGER NOT NULL,
              cached_tokens INTEGER NOT NULL,
              output_tokens INTEGER NOT NULL,
              reasoning_tokens INTEGER NOT NULL,
              responses INTEGER NOT NULL,
              PRIMARY KEY (client_id, bucket_utc, project, model)
            );
            CREATE TABLE IF NOT EXISTS requests (
              client_id TEXT NOT NULL,
              request_id TEXT NOT NULL,
              received_utc TEXT NOT NULL,
              PRIMARY KEY (client_id, request_id)
            );
            CREATE INDEX IF NOT EXISTS ix_usage_bucket ON usage(bucket_utc);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<DatabaseResult> ReplaceAndSummarizeAsync(string path, string clientId, SyncPayload payload, string requestId)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var duplicateCommand = connection.CreateCommand();
        duplicateCommand.Transaction = (SqliteTransaction)transaction;
        duplicateCommand.CommandText = "SELECT 1 FROM requests WHERE client_id=$client AND request_id=$request LIMIT 1";
        duplicateCommand.Parameters.AddWithValue("$client", clientId);
        duplicateCommand.Parameters.AddWithValue("$request", requestId);
        var duplicate = await duplicateCommand.ExecuteScalarAsync() is not null;
        if (!duplicate)
        {
            if (payload.Kind != "query")
            {
                var delete = connection.CreateCommand();
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM usage WHERE client_id=$client AND bucket_utc >= $start AND bucket_utc < $end";
                delete.Parameters.AddWithValue("$client", clientId);
                delete.Parameters.AddWithValue("$start", payload.RangeStartUtc.ToString("O"));
                delete.Parameters.AddWithValue("$end", payload.RangeEndUtc.ToString("O"));
                await delete.ExecuteNonQueryAsync();

                foreach (var row in payload.Rows)
                {
                    var insert = connection.CreateCommand();
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText = """
                        INSERT INTO usage(client_id,bucket_utc,project,model,input_tokens,cached_tokens,output_tokens,reasoning_tokens,responses)
                        VALUES($client,$bucket,$project,$model,$input,$cached,$output,$reasoning,$responses)
                        """;
                    insert.Parameters.AddWithValue("$client", clientId);
                    insert.Parameters.AddWithValue("$bucket", row.BucketStartUtc.ToString("O"));
                    insert.Parameters.AddWithValue("$project", row.Project);
                    insert.Parameters.AddWithValue("$model", row.Model);
                    insert.Parameters.AddWithValue("$input", row.Tokens.Input);
                    insert.Parameters.AddWithValue("$cached", row.Tokens.CachedInput);
                    insert.Parameters.AddWithValue("$output", row.Tokens.Output);
                    insert.Parameters.AddWithValue("$reasoning", row.Tokens.Reasoning);
                    insert.Parameters.AddWithValue("$responses", row.Tokens.Responses);
                    await insert.ExecuteNonQueryAsync();
                }
            }
            var remember = connection.CreateCommand();
            remember.Transaction = (SqliteTransaction)transaction;
            remember.CommandText = "INSERT INTO requests(client_id,request_id,received_utc) VALUES($client,$request,$received)";
            remember.Parameters.AddWithValue("$client", clientId);
            remember.Parameters.AddWithValue("$request", requestId);
            remember.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O"));
            await remember.ExecuteNonQueryAsync();

            var prune = connection.CreateCommand();
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = "DELETE FROM requests WHERE received_utc < $cutoff";
            prune.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-7).ToString("O"));
            await prune.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();

        var machines = new Dictionary<string, TokenCounts>(StringComparer.OrdinalIgnoreCase);
        var totals = connection.CreateCommand();
        totals.CommandText = """
            SELECT client_id, SUM(input_tokens), SUM(cached_tokens), SUM(output_tokens), SUM(reasoning_tokens), SUM(responses)
            FROM usage WHERE bucket_utc >= $start AND bucket_utc < $end GROUP BY client_id
            """;
        totals.Parameters.AddWithValue("$start", payload.CombinedStartUtc.ToString("O"));
        totals.Parameters.AddWithValue("$end", payload.CombinedEndUtc.ToString("O"));
        await using var reader = await totals.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            machines[reader.GetString(0)] = new TokenCounts(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        var combined = new TokenCounts(machines.Values.Sum(v => v.Input), machines.Values.Sum(v => v.CachedInput),
            machines.Values.Sum(v => v.Output), machines.Values.Sum(v => v.Reasoning), machines.Values.Sum(v => v.Responses));
        return new DatabaseResult(duplicate, combined, machines);
    }
}
