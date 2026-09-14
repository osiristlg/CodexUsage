using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsage.Core;

namespace CodexUsageDashboard;

internal sealed record NetworkSyncState(
    DateTime? LastSuccessUtc = null,
    DateTime? LastFullSyncDate = null,
    string LastStatus = "Not connected",
    TokenCounts? Combined = null,
    IReadOnlyDictionary<string, TokenCounts>? Machines = null,
    IReadOnlyList<AggregateRow>? Rows = null,
    IReadOnlyDictionary<string, IReadOnlyList<AggregateRow>>? MachineRows = null);

internal static class NetworkReporter
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Usage", "network-state.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexUsage.NetworkKey.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static NetworkSyncState LoadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<NetworkSyncState>(File.ReadAllText(StatePath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static async Task<DashboardSettings> ConfigurePassphraseAsync(
        DashboardSettings settings, string passphrase, CancellationToken token)
    {
        using var client = CreateClient(settings.ReceiverUrl);
        var saltReply = await client.GetFromJsonAsync<SaltReply>($"api/v1/salt/{Uri.EscapeDataString(settings.ClientId)}", JsonOptions, token)
            ?? throw new InvalidDataException("Receiver returned no pairing information.");
        if (saltReply.Version != AggregateProtocol.Version) throw new InvalidDataException("Receiver protocol version is incompatible.");
        var salt = Convert.FromBase64String(saltReply.Salt);
        var key = AggregateProtocol.DeriveKey(passphrase, salt);
        try
        {
            var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            return settings with { NetworkSalt = saltReply.Salt, ProtectedNetworkKey = Convert.ToBase64String(protectedKey) };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static async Task<NetworkSyncState> TestAsync(DashboardSettings settings, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var payload = WithDashboardRange(new SyncPayload("query", settings.MachineName, hour, hour.AddHours(1),
            DateTime.Today.ToUniversalTime(), DateTime.Today.AddDays(1).ToUniversalTime(), []));
        return await ExchangeAsync(settings, payload, false, token);
    }

    public static async Task<NetworkSyncState> SyncAsync(DashboardSettings settings, bool forceFull, CancellationToken token)
    {
        var previous = LoadState();
        var now = DateTime.Now;
        var full = forceFull || (now.Hour >= 2 && previous.LastFullSyncDate?.Date != now.Date);
        DateTime startUtc;
        DateTime endUtc;
        if (full)
        {
            startUtc = DateTime.Today.AddDays(-29).ToUniversalTime();
            endUtc = DateTime.Today.AddDays(1).ToUniversalTime();
        }
        else
        {
            var currentHour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
            startUtc = currentHour.AddHours(-1);
            endUtc = currentHour.AddHours(1);
        }

        var rows = await LogScanner.ScanAggregatesAsync(startUtc, endUtc, token, settings.SessionsFolder);
        var key = UnprotectKey(settings);
        try
        {
            rows = ApplyProjectPrivacy(rows, settings.NetworkProjectMode, key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        var payload = WithDashboardRange(new SyncPayload(full ? "full" : "incremental", settings.MachineName, startUtc, endUtc,
            DateTime.Today.ToUniversalTime(), DateTime.Today.AddDays(1).ToUniversalTime(), rows));
        return await ExchangeAsync(settings, payload, full, token);
    }

    private static async Task<NetworkSyncState> ExchangeAsync(
        DashboardSettings settings, SyncPayload payload, bool full, CancellationToken token)
    {
        var previous = LoadState();
        byte[] key;
        try { key = UnprotectKey(settings); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            var failed = previous with { LastStatus = "Encryption key unavailable" };
            SaveState(failed);
            return failed;
        }

        try
        {
            using var client = CreateClient(settings.ReceiverUrl);
            var envelope = AggregateProtocol.Encrypt(settings.ClientId, Guid.NewGuid().ToString(), DateTime.UtcNow, payload, key);
            using var response = await client.PostAsJsonAsync("api/v1/exchange", envelope, JsonOptions, token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Receiver rejected the request ({(int)response.StatusCode}).");
            var encryptedReply = await response.Content.ReadFromJsonAsync<EncryptedEnvelope>(JsonOptions, token)
                ?? throw new InvalidDataException("Receiver returned an empty response.");
            var reply = AggregateProtocol.Decrypt<ExchangeReply>(encryptedReply, key);
            if (!reply.Accepted) throw new InvalidDataException(reply.Message);
            var state = new NetworkSyncState(
                DateTime.UtcNow,
                full ? DateTime.Today : previous.LastFullSyncDate,
                payload.Kind == "query" ? "Encrypted connection verified" :
                    full ? "Full reconciliation complete" : "Incremental sync complete",
                reply.Combined,
                reply.Machines,
                reply.Rows,
                reply.MachineRows);
            SaveState(state);
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or
                                      JsonException or InvalidDataException or FormatException)
        {
            var failed = previous with { LastStatus = ex.Message };
            SaveState(failed);
            return failed;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static IReadOnlyList<AggregateRow> ApplyProjectPrivacy(
        IReadOnlyList<AggregateRow> rows, string mode, ReadOnlySpan<byte> key)
    {
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
            return rows.GroupBy(row => new { row.BucketStartUtc, row.Model, row.Effort }).Select(group => new AggregateRow(
                group.Key.BucketStartUtc, group.Key.Model, "All projects", Sum(group.Select(row => row.Tokens)))
                { Effort = group.Key.Effort }).ToArray();

        using var hmac = new HMACSHA256(key.ToArray());
        return rows.Select(row =>
        {
            var projectId = "Project " + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(row.Project)))[..8];
            return row with
            {
                Project = string.Equals(mode, "names", StringComparison.OrdinalIgnoreCase) ? row.Project : projectId,
                ProjectId = projectId
            };
        }).ToArray();
    }

    private static TokenCounts Sum(IEnumerable<TokenCounts> values)
    {
        var list = values.ToArray();
        return new TokenCounts(list.Sum(v => v.Input), list.Sum(v => v.CachedInput), list.Sum(v => v.Output),
            list.Sum(v => v.Reasoning), list.Sum(v => v.Responses));
    }

    private static byte[] UnprotectKey(DashboardSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ProtectedNetworkKey)) throw new CryptographicException("Passphrase is not configured.");
        return ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedNetworkKey), Entropy, DataProtectionScope.CurrentUser);
    }

    private static HttpClient CreateClient(string receiverUrl)
    {
        var normalized = receiverUrl.Trim();
        if (!normalized.EndsWith('/')) normalized += "/";
        return new HttpClient { BaseAddress = new Uri(normalized, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(20) };
    }

    private static SyncPayload WithDashboardRange(SyncPayload payload) => payload with
    {
        QueryStartUtc = DateTime.Today.AddDays(-29).ToUniversalTime(),
        QueryEndUtc = DateTime.Today.AddDays(1).ToUniversalTime()
    };

    private static void SaveState(NetworkSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, StatePath, true);
    }

    private sealed record SaltReply(int Version, string Salt);
}
