using System.Text.Json;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexUsage.Core;

namespace CodexUsageDashboard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = LogScanner.ScanTodayAsync(CancellationToken.None).GetAwaiter().GetResult();
            var history = LogScanner.ScanHistoryAsync(30, CancellationToken.None).GetAwaiter().GetResult();
            var report = JsonSerializer.Serialize(new
            {
                snapshot.Day,
                snapshot.RefreshedAt,
                snapshot.Total,
                snapshot.Input,
                snapshot.Cached,
                snapshot.Output,
                snapshot.Reasoning,
                snapshot.FilesScanned,
                Responses = snapshot.Points.Count,
                Models = snapshot.Points.GroupBy(p => p.Model).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)),
                Projects = snapshot.Points.GroupBy(p => p.Project).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)),
                HistoryDays = history.Days.Count,
                HistoryTotal = history.Days.Sum(d => d.Tokens),
                HistoryFirstDay = history.Days.FirstOrDefault(),
                HistoryLastDay = history.Days.LastOrDefault(),
                CachedHistoryVersion = HistoryStore.Load()?.FormatVersion
            }, new JsonSerializerOptions { WriteIndented = true });
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.WriteLine(report);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => MessageBox.Show(
            $"The dashboard recovered from an error:\n\n{eventArgs.Exception.Message}",
            "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new DashboardForm());
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}

internal sealed record HourlyUsage(int Hour, long Tokens, Dictionary<string, long> Models, Dictionary<string, long> Projects,
    Dictionary<string, long>? Efforts = null);
internal sealed record DailyUsage(DateTime Date, long Tokens, Dictionary<string, long>? Projects = null,
    IReadOnlyList<HourlyUsage>? Hours = null, Dictionary<string, long>? Efforts = null);
internal sealed record HistoryCache(DateTime BuiltAt, IReadOnlyList<DailyUsage> Days, int FormatVersion = 7);

internal static class LogScanner
{
    public static string DefaultSessionsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public static Task<UsageSnapshot> ScanTodayAsync(CancellationToken token, string? sessionsPath = null) =>
        Task.Run(() => ScanToday(token, ResolveSessionsPath(sessionsPath)), token);

    public static Task<HistoryCache> ScanHistoryAsync(int days, CancellationToken token, string? sessionsPath = null) => Task.Run(() =>
    {
        var sourcePath = ResolveSessionsPath(sessionsPath);
        var end = DateTime.Today;
        var start = end.AddDays(-(days - 1));
        var files = Directory.Exists(sourcePath)
            ? Directory.EnumerateFiles(sourcePath, "*.jsonl", SearchOption.AllDirectories).ToArray()
            : [];
        var points = new List<UsagePoint>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            ScanFile(file, start, end, points, token);
        }
        var byDay = points.GroupBy(p => p.Time.Date).ToDictionary(g => g.Key, g => g.ToArray());
        var result = Enumerable.Range(0, days).Select(i => start.AddDays(i))
            .Select(date =>
            {
                var dayPoints = byDay.GetValueOrDefault(date) ?? [];
                var hours = Enumerable.Range(0, 24).Select(hour =>
                {
                    var hourPoints = dayPoints.Where(p => p.Time.Hour == hour).ToArray();
                    return new HourlyUsage(hour, hourPoints.Sum(p => p.Total),
                        hourPoints.GroupBy(p => p.Model).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)),
                        hourPoints.GroupBy(p => p.Project).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)),
                        hourPoints.GroupBy(p => p.Effort).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)));
                }).ToArray();
                return new DailyUsage(date, dayPoints.Sum(p => p.Total), dayPoints
                    .GroupBy(p => p.Project).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)), hours,
                    dayPoints.GroupBy(p => p.Effort).ToDictionary(g => g.Key, g => g.Sum(p => p.Total)));
            }).ToArray();
        return new HistoryCache(DateTime.Now, result);
    }, token);

    public static Task<IReadOnlyList<AggregateRow>> ScanAggregatesAsync(
        DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken token, string? sessionsPath = null) => Task.Run(() =>
    {
        if (rangeStartUtc.Kind != DateTimeKind.Utc || rangeEndUtc.Kind != DateTimeKind.Utc || rangeEndUtc <= rangeStartUtc)
            throw new ArgumentException("Aggregate scan range must be a valid UTC interval.");
        var sourcePath = ResolveSessionsPath(sessionsPath);
        var files = Directory.Exists(sourcePath)
            ? Directory.EnumerateFiles(sourcePath, "*.jsonl", SearchOption.AllDirectories).ToArray()
            : [];
        var points = new List<UsagePoint>();
        var localStart = rangeStartUtc.ToLocalTime().Date;
        var localEnd = rangeEndUtc.AddTicks(-1).ToLocalTime().Date;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            ScanFile(file, localStart, localEnd, points, token);
        }
        return (IReadOnlyList<AggregateRow>)points
            .Where(point => point.Time.ToUniversalTime() >= rangeStartUtc && point.Time.ToUniversalTime() < rangeEndUtc)
            .GroupBy(point => new
            {
                Bucket = new DateTime(point.Time.ToUniversalTime().Year, point.Time.ToUniversalTime().Month,
                    point.Time.ToUniversalTime().Day, point.Time.ToUniversalTime().Hour, 0, 0, DateTimeKind.Utc),
                point.Model,
                point.Project,
                point.Effort
            })
            .Select(group => new AggregateRow(group.Key.Bucket, group.Key.Model, group.Key.Project,
                new TokenCounts(group.Sum(p => p.Input), group.Sum(p => p.Cached), group.Sum(p => p.Output),
                    group.Sum(p => p.Reasoning), group.LongCount())) { Effort = group.Key.Effort })
            .OrderBy(row => row.BucketStartUtc).ThenBy(row => row.Project).ThenBy(row => row.Model)
            .ToArray();
    }, token);

    private static UsageSnapshot ScanToday(CancellationToken token, string sessionsPath)
    {
        var today = DateTime.Today;
        var candidateDays = new[] { today.AddDays(-1), today, today.AddDays(1) };
        var dateFolderFiles = candidateDays
            .Select(d => Path.Combine(sessionsPath, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd")))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly));
        var recentlyActiveFiles = Directory.Exists(sessionsPath)
            ? Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories)
                .Where(file => File.GetLastWriteTime(file) >= today.AddDays(-1))
            : [];
        var files = dateFolderFiles.Concat(recentlyActiveFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var points = new List<UsagePoint>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            ScanFile(file, today, today, points, token);
        }
        return new UsageSnapshot(today, DateTime.Now, points, files.Length);
    }

    private static string ResolveSessionsPath(string? sessionsPath) =>
        string.IsNullOrWhiteSpace(sessionsPath) ? DefaultSessionsPath : Path.GetFullPath(sessionsPath);

    private static void ScanFile(string file, DateTime firstDay, DateTime lastDay, List<UsagePoint> points, CancellationToken token)
    {
        var modelByTurn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var effortByTurn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string fallbackModel = "Unknown model";
        string currentModel = fallbackModel;
        string currentEffort = "Unknown";
        string currentProject = "Projectless";
        var countPoints = new List<UsagePoint>();
        var recordPoints = new List<UsagePoint>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (!line.Contains("\"turn_context\"", StringComparison.Ordinal) &&
                    !line.Contains("\"token_count\"", StringComparison.Ordinal) &&
                    !line.Contains("\"token_usage_record\"", StringComparison.Ordinal) &&
                    !line.Contains("\"session_meta\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();
                    var payload = root.GetProperty("payload");

                    if (type == "session_meta")
                    {
                        if (payload.TryGetProperty("cwd", out var cwd))
                            currentProject = FriendlyProject(cwd.GetString());
                        if (payload.TryGetProperty("base_instructions", out var bi) &&
                            bi.TryGetProperty("provenance", out var prov) &&
                            prov.TryGetProperty("model", out var sm))
                            currentModel = fallbackModel = FriendlyModel(sm.GetString());
                        continue;
                    }
                    if (type == "turn_context")
                    {
                        var contextTurnId = payload.TryGetProperty("turn_id", out var tid) ? tid.GetString() ?? "" : "";
                        if (payload.TryGetProperty("model", out var model))
                        {
                            currentModel = FriendlyModel(model.GetString());
                            if (contextTurnId.Length > 0) modelByTurn[contextTurnId] = currentModel;
                        }
                        currentEffort = FriendlyEffort(ReadEffort(payload));
                        if (contextTurnId.Length > 0) effortByTurn[contextTurnId] = currentEffort;
                        continue;
                    }

                    var utc = DateTime.Parse(root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var local = utc.ToLocalTime();
                    if (local.Date < firstDay.Date || local.Date > lastDay.Date) continue;

                    if (type == "event_msg" && payload.TryGetProperty("type", out var eventType) &&
                        eventType.GetString() == "token_count" && payload.TryGetProperty("info", out var info) &&
                        info.ValueKind == JsonValueKind.Object &&
                        info.TryGetProperty("last_token_usage", out var lastUsage) &&
                        lastUsage.ValueKind == JsonValueKind.Object)
                    {
                        countPoints.Add(ToPoint(local, currentModel, currentProject, currentEffort, lastUsage));
                        continue;
                    }
                    if (type != "token_usage_record") continue;

                    var turnId = payload.TryGetProperty("turn_id", out var tr) ? tr.GetString() ?? "" : "";
                    var modelName = modelByTurn.GetValueOrDefault(turnId, fallbackModel);
                    var effortName = effortByTurn.GetValueOrDefault(turnId, "Unknown");
                    var usage = payload.GetProperty("usage");
                    recordPoints.Add(ToPoint(local, modelName, currentProject, effortName, usage));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
                { /* Skip incomplete or legacy-shaped records without interrupting a refresh. */ }
            }
        }
        catch (IOException) { /* Skip a file briefly unavailable during a write. */ }
        points.AddRange(countPoints.Count > 0 ? countPoints : recordPoints);
    }

    private static UsagePoint ToPoint(DateTime time, string model, string project, string effort, JsonElement usage) => new(
        time,
        model,
        project,
        GetLong(usage, "input_tokens"),
        GetLong(usage, "cached_input_tokens"),
        GetLong(usage, "output_tokens"),
        GetLong(usage, "reasoning_output_tokens"),
        Effort: effort);

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static string FriendlyModel(string? model) => string.IsNullOrWhiteSpace(model)
        ? "Unknown model"
        : model.Replace("gpt-", "GPT ", StringComparison.OrdinalIgnoreCase)
               .Replace("codex", "Codex", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" or "light" or "minimal" => "Light",
        "medium" => "Medium",
        "high" => "High",
        null or "" => "Unknown",
        var value => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '))
    };

    private static string? ReadEffort(JsonElement payload)
    {
        if (payload.TryGetProperty("reasoning_effort", out var reasoningEffort)) return reasoningEffort.GetString();
        if (payload.TryGetProperty("effort", out var effort)) return effort.GetString();
        if (payload.TryGetProperty("collaboration_mode", out var collaborationMode) &&
            collaborationMode.ValueKind == JsonValueKind.Object &&
            collaborationMode.TryGetProperty("settings", out var settings) &&
            settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("reasoning_effort", out var nestedEffort)) return nestedEffort.GetString();
        return null;
    }

    private static string FriendlyProject(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Projectless";
        var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }
}

internal static class HistoryStore
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Usage", "history.json");

    public static HistoryCache? Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<HistoryCache>(json);
            using var doc = JsonDocument.Parse(json);
            return cache is not null && !doc.RootElement.TryGetProperty("FormatVersion", out _)
                ? cache with { FormatVersion = 0 }
                : cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(HistoryCache cache)
    {
        var directory = Path.GetDirectoryName(CachePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
    }

    public static HistoryCache MergeToday(HistoryCache cache, UsageSnapshot snapshot)
    {
        var projects = snapshot.Points.GroupBy(point => point.Project)
            .ToDictionary(group => group.Key, group => group.Sum(point => point.Total));
        var hours = Enumerable.Range(0, 24).Select(hour =>
        {
            var hourPoints = snapshot.Points.Where(point => point.Time.Hour == hour).ToArray();
            return new HourlyUsage(hour, hourPoints.Sum(point => point.Total),
                hourPoints.GroupBy(point => point.Model).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)),
                hourPoints.GroupBy(point => point.Project).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)),
                hourPoints.GroupBy(point => point.Effort).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)));
        }).ToArray();
        var snapshotDay = snapshot.Day.Date;
        var efforts = snapshot.Points.GroupBy(point => point.Effort)
            .ToDictionary(group => group.Key, group => group.Sum(point => point.Total));
        var today = new DailyUsage(snapshotDay, snapshot.Total, projects, hours, efforts);
        var days = cache.Days.Select(day => day.Date.Date == snapshotDay ? today : day).ToList();
        if (!days.Any(day => day.Date.Date == snapshotDay))
        {
            days.Add(today);
            days = days.OrderBy(day => day.Date).TakeLast(30).ToList();
        }
        return cache with { Days = days };
    }

    public static bool ShouldAutoBuild(HistoryCache? cache) =>
        cache is null || cache.FormatVersion < 7 ||
        (DateTime.Now.Hour >= 2 && cache.BuiltAt.Date < DateTime.Today);

    public static (UsageSnapshot Snapshot, HistoryCache History) FromAggregates(IReadOnlyList<AggregateRow> rows)
    {
        var points = rows.Select(row => new UsagePoint(row.BucketStartUtc.ToLocalTime(), row.Model, row.Project,
            row.Tokens.Input, row.Tokens.CachedInput, row.Tokens.Output, row.Tokens.Reasoning, row.Tokens.Responses,
            row.Effort ?? "Unknown")).ToArray();
        var start = DateTime.Today.AddDays(-29);
        var byDay = points.GroupBy(point => point.Time.Date).ToDictionary(group => group.Key, group => group.ToArray());
        var days = Enumerable.Range(0, 30).Select(offset => start.AddDays(offset)).Select(date =>
        {
            var dayPoints = byDay.GetValueOrDefault(date) ?? [];
            var hours = Enumerable.Range(0, 24).Select(hour =>
            {
                var hourPoints = dayPoints.Where(point => point.Time.Hour == hour).ToArray();
                return new HourlyUsage(hour, hourPoints.Sum(point => point.Total),
                    hourPoints.GroupBy(point => point.Model).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)),
                    hourPoints.GroupBy(point => point.Project).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)),
                    hourPoints.GroupBy(point => point.Effort).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)));
            }).ToArray();
            return new DailyUsage(date, dayPoints.Sum(point => point.Total),
                dayPoints.GroupBy(point => point.Project).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)), hours,
                dayPoints.GroupBy(point => point.Effort).ToDictionary(group => group.Key, group => group.Sum(point => point.Total)));
        }).ToArray();
        var todayPoints = byDay.GetValueOrDefault(DateTime.Today) ?? [];
        return (new UsageSnapshot(DateTime.Today, DateTime.Now, todayPoints, 0), new HistoryCache(DateTime.Now, days));
    }
}

internal sealed record ThemePalette(
    string Name, Color Background, Color Panel, Color Text, Color Muted,
    Color Primary, Color Secondary, Color Tertiary, Color[] Series);

internal static class ThemeCatalog
{
    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new("Night City", Color.FromArgb(9, 5, 24), Color.FromArgb(21, 16, 45), Color.FromArgb(244, 246, 255), Color.FromArgb(143, 151, 177),
            Color.FromArgb(0, 218, 255), Color.FromArgb(255, 48, 190), Color.FromArgb(255, 174, 42),
            [Color.FromArgb(134, 99, 255), Color.FromArgb(29, 211, 176), Color.FromArgb(255, 168, 76), Color.FromArgb(80, 156, 255), Color.FromArgb(244, 101, 153), Color.FromArgb(180, 188, 212)]),
        new("Neon Sunset", Color.FromArgb(25, 9, 35), Color.FromArgb(40, 16, 47), Color.FromArgb(255, 242, 223), Color.FromArgb(185, 143, 157),
            Color.FromArgb(255, 182, 39), Color.FromArgb(255, 77, 109), Color.FromArgb(198, 78, 255),
            [Color.FromArgb(255, 182, 39), Color.FromArgb(255, 77, 109), Color.FromArgb(255, 119, 48), Color.FromArgb(198, 78, 255), Color.FromArgb(255, 217, 120), Color.FromArgb(222, 129, 186)]),
        new("Toxic Rain", Color.FromArgb(6, 21, 15), Color.FromArgb(11, 33, 25), Color.FromArgb(234, 255, 216), Color.FromArgb(126, 165, 139),
            Color.FromArgb(182, 255, 46), Color.FromArgb(0, 255, 200), Color.FromArgb(255, 214, 62),
            [Color.FromArgb(182, 255, 46), Color.FromArgb(0, 255, 200), Color.FromArgb(87, 219, 69), Color.FromArgb(255, 214, 62), Color.FromArgb(48, 210, 255), Color.FromArgb(173, 205, 126)]),
        new("Ion Storm", Color.FromArgb(7, 14, 40), Color.FromArgb(16, 26, 62), Color.FromArgb(236, 243, 255), Color.FromArgb(139, 153, 190),
            Color.FromArgb(72, 229, 255), Color.FromArgb(157, 124, 255), Color.FromArgb(89, 124, 255),
            [Color.FromArgb(72, 229, 255), Color.FromArgb(157, 124, 255), Color.FromArgb(89, 124, 255), Color.FromArgb(92, 180, 255), Color.FromArgb(206, 117, 255), Color.FromArgb(171, 196, 233)]),
        new("Redline District", Color.FromArgb(22, 7, 7), Color.FromArgb(40, 16, 14), Color.FromArgb(255, 240, 223), Color.FromArgb(181, 137, 124),
            Color.FromArgb(255, 59, 48), Color.FromArgb(255, 159, 28), Color.FromArgb(0, 205, 255),
            [Color.FromArgb(255, 59, 48), Color.FromArgb(255, 159, 28), Color.FromArgb(255, 91, 75), Color.FromArgb(255, 205, 54), Color.FromArgb(0, 205, 255), Color.FromArgb(218, 153, 126)])
    ];

    public static ThemePalette Current { get; private set; } = All[0];
    public static void Select(string? name) => Current = All.FirstOrDefault(t =>
        string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

internal sealed record DashboardSettings(
    int RefreshMinutes = 5,
    string Theme = "Night City",
    bool SnapshotEnabled = false,
    int SnapshotMinutes = 15,
    string SnapshotFolder = "",
    bool HideProjectNames = true,
    string SessionsFolder = "",
    bool NetworkEnabled = false,
    string ReceiverUrl = "http://127.0.0.1:4747",
    string MachineName = "",
    string ClientId = "",
    string NetworkSalt = "",
    string ProtectedNetworkKey = "",
    string NetworkProjectMode = "anonymous",
    string NetworkView = "All machines");

internal static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Usage", "settings.json");

    public static DashboardSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new DashboardSettings();
            return JsonSerializer.Deserialize<DashboardSettings>(File.ReadAllText(SettingsPath)) ?? new DashboardSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new DashboardSettings(); }
    }

    public static void Save(DashboardSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }
}

internal sealed record ReceiverClientEntry(string ClientId, string MachineName, string Salt, string Key, bool Enabled);
internal sealed record ReceiverAdminSettings(
    string BindAddress,
    int Port,
    IReadOnlyList<string> AllowedSubnets,
    IReadOnlyList<ReceiverClientEntry> Clients,
    int MaxPayloadBytes = 16 * 1024 * 1024,
    int MaxRowsPerRequest = 100_000);

internal static class ReceiverClientManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Usage Receiver", "receiver-settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool IsAvailable => File.Exists(ConfigPath);

    public static IReadOnlyList<ReceiverClientEntry> LoadClients() => Load().Clients;

    public static void Rotate(IReadOnlyCollection<string> clientIds, string passphrase)
    {
        if (clientIds.Count == 0) throw new InvalidOperationException("Select at least one receiver client.");
        if (passphrase.Length < 12) throw new InvalidOperationException("Use a shared passphrase of at least 12 characters.");
        var selected = clientIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = Load();
        if (!settings.Clients.Any(client => selected.Contains(client.ClientId)))
            throw new InvalidOperationException("The selected receiver clients no longer exist.");

        var clients = new List<ReceiverClientEntry>(settings.Clients.Count);
        foreach (var client in settings.Clients)
        {
            if (!selected.Contains(client.ClientId)) { clients.Add(client); continue; }
            var salt = AggregateProtocol.NewSalt();
            var key = AggregateProtocol.DeriveKey(passphrase, salt);
            try
            {
                clients.Add(client with { Salt = Convert.ToBase64String(salt), Key = Convert.ToBase64String(key) });
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        Save(settings with { Clients = clients });
    }

    private static ReceiverAdminSettings Load() =>
        JsonSerializer.Deserialize<ReceiverAdminSettings>(File.ReadAllText(ConfigPath))
        ?? throw new InvalidDataException("Receiver settings are empty.");

    private static void Save(ReceiverAdminSettings settings)
    {
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, ConfigPath, true);
    }
}

internal sealed class DashboardForm : Form
{
    private static ThemePalette Theme => ThemeCatalog.Current;
    private static Color Bg => Theme.Background;
    private static Color Panel => Theme.Panel;
    private static Color TextMain => Theme.Text;
    private static Color TextMuted => Theme.Muted;
    private readonly UsageCanvas canvas = new() { Dock = DockStyle.Fill };
    private readonly Label status = new()
    {
        AutoSize = false,
        Width = 245,
        Height = 24,
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = TextMuted,
        Font = new Font("Segoe UI", 9f)
    };
    private readonly Button refresh = MakeButton("↻  Refresh now", 126, ThemeCatalog.Current.Primary);
    private readonly Button rebuild = MakeButton("◷  Rebuild 30 days", 158, ThemeCatalog.Current.Secondary);
    private readonly Button view = MakeButton("◉  This PC", 150, ThemeCatalog.Current.Tertiary);
    private readonly ToolTip viewToolTip = new() { ShowAlways = true };
    private readonly Button settings = MakeButton("⚙", 42, ThemeCatalog.Current.Tertiary);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer snapshotTimer = new();
    private CancellationTokenSource? refreshCts;
    private bool isRefreshing;
    private bool isRebuilding;
    private bool isNetworkSyncing;
    private int refreshMinutes = 5;
    private DateTime? lastRefreshedAt;
    private Panel header = null!;
    private Label titleLabel = null!;
    private DashboardSettings appSettings = new();
    private NetworkSyncState networkState = NetworkReporter.LoadState();
    private UsageSnapshot? localSnapshot;
    private HistoryCache? localHistory;

    public DashboardForm()
    {
        Text = "Codex Usage";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        appSettings = SettingsStore.Load();
        if (string.IsNullOrWhiteSpace(appSettings.SessionsFolder))
        {
            appSettings = appSettings with { SessionsFolder = LogScanner.DefaultSessionsPath };
            SettingsStore.Save(appSettings);
        }
        if (string.IsNullOrWhiteSpace(appSettings.MachineName) || string.IsNullOrWhiteSpace(appSettings.ClientId))
        {
            appSettings = appSettings with
            {
                MachineName = string.IsNullOrWhiteSpace(appSettings.MachineName) ? Environment.MachineName : appSettings.MachineName,
                ClientId = string.IsNullOrWhiteSpace(appSettings.ClientId) ? Guid.NewGuid().ToString("N") : appSettings.ClientId
            };
            SettingsStore.Save(appSettings);
        }
        ThemeCatalog.Select(appSettings.Theme);
        BackColor = Bg;
        ForeColor = TextMain;
        MinimumSize = new Size(920, 700);
        Size = new Size(1180, 900);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        refreshMinutes = Math.Clamp(appSettings.RefreshMinutes, 1, 120);
        timer.Interval = refreshMinutes * 60 * 1000;
        ConfigureSnapshotTimer();
        settings.Font = new Font("Segoe UI Symbol", 12f, FontStyle.Bold);

        header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Bg, Padding = new Padding(34, 17, 34, 10) };
        titleLabel = new Label
        {
            Text = "CODEX  /  USAGE",
            AutoSize = true,
            ForeColor = TextMain,
            Font = new Font("Segoe UI Semibold", 13f),
            Location = new Point(34, 21)
        };
        status.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        status.Location = new Point(header.Width - 560, 22);
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        rebuild.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        view.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        void LayoutHeader()
        {
            settings.Left = header.ClientSize.Width - settings.Width - 34;
            rebuild.Left = settings.Left - rebuild.Width - 10;
            refresh.Left = rebuild.Left - refresh.Width - 10;
            view.Left = refresh.Left - view.Width - 10;
            status.Width = Math.Clamp(view.Left - titleLabel.Right - 38, 80, 245);
            status.Left = view.Left - status.Width - 18;
            refresh.Top = rebuild.Top = view.Top = settings.Top = (header.ClientSize.Height - refresh.Height) / 2;
            status.Top = (header.ClientSize.Height - status.Height) / 2;
        }
        header.Resize += (_, _) => LayoutHeader();
        header.Controls.AddRange([titleLabel, status, view, refresh, rebuild, settings]);
        LayoutHeader();
        Controls.Add(canvas);
        Controls.Add(header);

        refresh.Click += async (_, _) => await RefreshDataAsync();
        rebuild.Click += async (_, _) => await RebuildHistoryAsync();
        view.Click += (_, _) => CycleNetworkView();
        settings.Click += async (_, _) => await ShowSettingsDialogAsync();
        timer.Tick += async (_, _) => await RefreshDataAsync();
        snapshotTimer.Tick += (_, _) => ExportSnapshot();
        Shown += async (_, _) =>
        {
            timer.Start();
            await RefreshDataAsync();
            if (appSettings.SnapshotEnabled) ExportSnapshot();
        };
        FormClosed += (_, _) => { refreshCts?.Cancel(); snapshotTimer.Stop(); };
        canvas.NetworkState = networkState;
        view.Visible = appSettings.NetworkEnabled;
        ApplyTheme();
    }

    private async Task RefreshDataAsync()
    {
        if (isRefreshing) return;
        isRefreshing = true;
        refreshCts = new CancellationTokenSource();
        refresh.Enabled = false;
        status.Text = "Scanning local logs…";
        try
        {
            var snapshot = await LogScanner.ScanTodayAsync(refreshCts.Token, appSettings.SessionsFolder);
            while (snapshot.Day.Date != DateTime.Today)
                snapshot = await LogScanner.ScanTodayAsync(refreshCts.Token, appSettings.SessionsFolder);
            localSnapshot = snapshot;
            localHistory ??= HistoryStore.Load();
            if (localHistory is { } history)
            {
                localHistory = HistoryStore.MergeToday(history, snapshot);
                HistoryStore.Save(localHistory);
            }
            ApplySelectedView();
            lastRefreshedAt = snapshot.RefreshedAt;
            UpdateRefreshStatus();
            if (HistoryStore.ShouldAutoBuild(localHistory)) await RebuildHistoryAsync(true);
            await SyncNetworkAsync(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            status.Text = "Couldn’t read logs";
            MessageBox.Show(this, ex.Message, "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { refresh.Enabled = true; isRefreshing = false; }
    }

    private void ShowSettingsMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Panel,
            ForeColor = TextMain,
            Font = new Font("Segoe UI", 9.5f),
            ShowImageMargin = true,
            Renderer = new NightCityMenuRenderer()
        };
        menu.Items.Add(new ToolStripLabel("CODEX DATA")
        {
            ForeColor = Theme.Tertiary,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Padding = new Padding(8, 5, 8, 4)
        });
        var sourceItem = new ToolStripMenuItem("Choose session folder…")
        {
            ToolTipText = appSettings.SessionsFolder,
            Padding = new Padding(7, 3, 10, 3)
        };
        sourceItem.Click += async (_, _) =>
        {
            if (!ChooseSessionsFolder()) return;
            await RebuildHistoryAsync();
            await RefreshDataAsync();
        };
        menu.Items.Add(sourceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripLabel("REFRESH INTERVAL")
        {
            ForeColor = Theme.Tertiary,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Padding = new Padding(8, 5, 8, 4)
        });
        foreach (var minutes in new[] { 1, 2, 5, 10, 15, 30, 60 })
        {
            var item = new ToolStripMenuItem(minutes == 1 ? "Every minute" : $"Every {minutes} minutes")
            {
                Checked = refreshMinutes == minutes,
                CheckOnClick = false,
                Tag = minutes,
                Padding = new Padding(7, 3, 10, 3)
            };
            item.Click += (_, _) =>
            {
                refreshMinutes = (int)item.Tag!;
                timer.Stop();
                timer.Interval = refreshMinutes * 60 * 1000;
                timer.Start();
                appSettings = appSettings with { RefreshMinutes = refreshMinutes, Theme = Theme.Name };
                SettingsStore.Save(appSettings);
                UpdateRefreshStatus();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripLabel("COLOR SCHEME")
        {
            ForeColor = Theme.Tertiary,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Padding = new Padding(8, 5, 8, 4)
        });
        foreach (var palette in ThemeCatalog.All)
        {
            var item = new ToolStripMenuItem(palette.Name)
            {
                Checked = palette.Name == Theme.Name,
                Tag = palette.Name,
                Padding = new Padding(7, 3, 10, 3)
            };
            item.Click += (_, _) =>
            {
                ThemeCatalog.Select((string)item.Tag!);
                appSettings = appSettings with { RefreshMinutes = refreshMinutes, Theme = Theme.Name };
                SettingsStore.Save(appSettings);
                ApplyTheme();
                status.Text = $"Theme set to {Theme.Name}";
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripLabel("SNAPSHOT EXPORT")
        {
            ForeColor = Theme.Tertiary,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Padding = new Padding(8, 5, 8, 4)
        });
        var enabledItem = new ToolStripMenuItem("Enable latest snapshot")
        {
            Checked = appSettings.SnapshotEnabled,
            Padding = new Padding(7, 3, 10, 3)
        };
        enabledItem.Click += (_, _) =>
        {
            var enable = !appSettings.SnapshotEnabled;
            if (enable && string.IsNullOrWhiteSpace(appSettings.SnapshotFolder) && !ChooseSnapshotFolder()) return;
            appSettings = appSettings with { SnapshotEnabled = enable };
            SettingsStore.Save(appSettings);
            ConfigureSnapshotTimer();
            if (enable) ExportSnapshot();
            status.Text = enable ? "Local snapshot export enabled" : "Local snapshot export disabled";
        };
        menu.Items.Add(enabledItem);

        var folderItem = new ToolStripMenuItem("Choose snapshot folder…") { Padding = new Padding(7, 3, 10, 3) };
        folderItem.Click += (_, _) => ChooseSnapshotFolder();
        menu.Items.Add(folderItem);

        var privacyItem = new ToolStripMenuItem("Hide project names")
        {
            Checked = appSettings.HideProjectNames,
            Padding = new Padding(7, 3, 10, 3)
        };
        privacyItem.Click += (_, _) =>
        {
            appSettings = appSettings with { HideProjectNames = !appSettings.HideProjectNames };
            SettingsStore.Save(appSettings);
            status.Text = appSettings.HideProjectNames ? "Snapshot project names hidden" : "Snapshot project names visible";
        };
        menu.Items.Add(privacyItem);
        foreach (var minutes in new[] { 5, 15, 30, 60 })
        {
            var item = new ToolStripMenuItem($"Snapshot every {minutes} minutes")
            {
                Checked = appSettings.SnapshotMinutes == minutes,
                Tag = minutes,
                Padding = new Padding(7, 3, 10, 3)
            };
            item.Click += (_, _) =>
            {
                appSettings = appSettings with { SnapshotMinutes = (int)item.Tag! };
                SettingsStore.Save(appSettings);
                ConfigureSnapshotTimer();
                status.Text = $"Snapshot interval set to {appSettings.SnapshotMinutes} min";
            };
            menu.Items.Add(item);
        }
        menu.Show(settings, new Point(settings.Width - menu.PreferredSize.Width, settings.Height + 2));
    }

    private async Task ShowSettingsDialogAsync()
    {
        var previous = appSettings;
        using var dialog = new SettingsDialog(appSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        appSettings = dialog.Result;
        SettingsStore.Save(appSettings);
        refreshMinutes = Math.Clamp(appSettings.RefreshMinutes, 1, 120);
        timer.Stop();
        timer.Interval = refreshMinutes * 60 * 1000;
        timer.Start();
        ThemeCatalog.Select(appSettings.Theme);
        ConfigureSnapshotTimer();
        ApplyTheme();
        ApplySelectedView();
        UpdateRefreshStatus();

        if (!string.Equals(previous.SessionsFolder, appSettings.SessionsFolder, StringComparison.OrdinalIgnoreCase))
        {
            await RebuildHistoryAsync();
            await RefreshDataAsync();
        }
        if (appSettings.SnapshotEnabled) ExportSnapshot();
        await SyncNetworkAsync(false);
    }

    private bool ChooseSessionsFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the Codex sessions folder to scan",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = Directory.Exists(appSettings.SessionsFolder)
                ? appSettings.SessionsFolder
                : LogScanner.DefaultSessionsPath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        appSettings = appSettings with { SessionsFolder = Path.GetFullPath(dialog.SelectedPath) };
        SettingsStore.Save(appSettings);
        status.Text = "Codex session folder selected";
        return true;
    }

    private bool ChooseSnapshotFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a local or synced folder for codex-usage-latest.png",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(appSettings.SnapshotFolder)
                ? appSettings.SnapshotFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        appSettings = appSettings with { SnapshotFolder = dialog.SelectedPath };
        SettingsStore.Save(appSettings);
        status.Text = "Snapshot folder selected";
        return true;
    }

    private void ConfigureSnapshotTimer()
    {
        snapshotTimer.Stop();
        var minutes = appSettings.SnapshotMinutes is 5 or 15 or 30 or 60 ? appSettings.SnapshotMinutes : 15;
        snapshotTimer.Interval = minutes * 60 * 1000;
        if (appSettings.SnapshotEnabled && !string.IsNullOrWhiteSpace(appSettings.SnapshotFolder)) snapshotTimer.Start();
    }

    private void ExportSnapshot()
    {
        if (!appSettings.SnapshotEnabled || string.IsNullOrWhiteSpace(appSettings.SnapshotFolder) ||
            canvas.Snapshot is null || canvas.Snapshot.Day.Date != DateTime.Today ||
            WindowState == FormWindowState.Minimized || canvas.ClientSize.Width <= 0 || canvas.ClientSize.Height <= 0) return;
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(appSettings.SnapshotFolder);
            var finalPath = Path.Combine(appSettings.SnapshotFolder, "codex-usage-latest.png");
            temporaryPath = Path.Combine(appSettings.SnapshotFolder, $".codex-usage-{Guid.NewGuid():N}.tmp.png");
            canvas.ExportPng(temporaryPath, appSettings.HideProjectNames);
            File.Move(temporaryPath, finalPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            snapshotTimer.Stop();
            status.Text = "Snapshot export paused — check the selected folder";
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private async Task SyncNetworkAsync(bool forceFull)
    {
        if (!appSettings.NetworkEnabled || string.IsNullOrWhiteSpace(appSettings.ProtectedNetworkKey) || isNetworkSyncing) return;
        isNetworkSyncing = true;
        try
        {
            networkState = await NetworkReporter.SyncAsync(appSettings, forceFull, refreshCts?.Token ?? CancellationToken.None);
            canvas.NetworkState = networkState;
            ApplySelectedView();
            if (forceFull) status.Text = networkState.LastStatus;
        }
        finally { isNetworkSyncing = false; }
    }

    private string[] AvailableViews()
    {
        IEnumerable<string> machines = networkState.MachineRows?.Keys.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            ?? Enumerable.Empty<string>();
        return ["This PC", "All machines", .. machines];
    }

    private void CycleNetworkView()
    {
        var options = AvailableViews();
        var current = Array.FindIndex(options, option => string.Equals(option, appSettings.NetworkView, StringComparison.OrdinalIgnoreCase));
        appSettings = appSettings with { NetworkView = options[(current + 1 + options.Length) % options.Length] };
        SettingsStore.Save(appSettings);
        ApplySelectedView();
    }

    private void ApplySelectedView()
    {
        var options = AvailableViews();
        var selected = options.FirstOrDefault(option => string.Equals(option, appSettings.NetworkView, StringComparison.OrdinalIgnoreCase))
            ?? "This PC";
        if (!appSettings.NetworkEnabled || networkState.Rows is null)
            selected = "This PC";

        UsageSnapshot? snapshot = localSnapshot;
        HistoryCache? history = localHistory;
        if (selected == "All machines" && networkState.Rows is { } allRows)
            (snapshot, history) = HistoryStore.FromAggregates(allRows);
        else if (networkState.MachineRows?.FirstOrDefault(pair => string.Equals(pair.Key, selected, StringComparison.OrdinalIgnoreCase))
                     is { Key.Length: > 0 } machine)
            (snapshot, history) = HistoryStore.FromAggregates(machine.Value);

        canvas.Snapshot = snapshot;
        canvas.History = history;
        canvas.NetworkState = appSettings.NetworkEnabled ? networkState : null;
        canvas.SourceLabel = selected;
        view.Text = $"◉  {selected}";
        viewToolTip.SetToolTip(view, selected);
        view.Visible = appSettings.NetworkEnabled;
        canvas.Invalidate();
    }

    private void ApplyTheme()
    {
        BackColor = Bg;
        ForeColor = TextMain;
        header.BackColor = Bg;
        titleLabel.ForeColor = TextMain;
        status.ForeColor = TextMuted;
        canvas.BackColor = Bg;
        ((NeonButton)refresh).SetAccent(Theme.Primary);
        ((NeonButton)rebuild).SetAccent(Theme.Secondary);
        ((NeonButton)view).SetAccent(Theme.Tertiary);
        ((NeonButton)settings).SetAccent(Theme.Tertiary);
        Invalidate(true);
    }

    private void UpdateRefreshStatus()
    {
        status.Text = lastRefreshedAt is { } refreshed
            ? $"Updated {refreshed:h:mm tt}  •  every {refreshMinutes} min"
            : $"Refresh every {refreshMinutes} min";
    }

    private sealed class SettingsDialog : Form
    {
        private readonly Panel pageHost = new() { Dock = DockStyle.Fill, Padding = new Padding(38, 26, 38, 24) };
        private readonly List<Button> navigation = [];
        private readonly NeonSelect refresh = MakeCombo();
        private readonly NeonSelect theme = MakeCombo();
        private readonly PathDisplay sessions = MakePathDisplay();
        private readonly NeonToggle snapshotEnabled = new();
        private readonly PathDisplay snapshotFolder = MakePathDisplay();
        private readonly NeonSelect snapshotInterval = MakeCombo();
        private readonly NeonToggle hideProjects = new();
        private readonly NeonToggle networkEnabled = new();
        private readonly TextBox clientId = MakeInput();
        private readonly TextBox receiverUrl = MakeInput();
        private readonly TextBox machineName = MakeInput();
        private readonly TextBox passphrase = MakeInput(true);
        private readonly NeonSelect networkProjects = MakeCombo();
        private readonly Label networkStatus = MakeLabel("Not tested", 9f, Theme.Muted);
        private int selectedPage;

        public DashboardSettings Result { get; private set; }

        public SettingsDialog(DashboardSettings settings)
        {
            Result = settings;
            Text = "Codex Usage Settings";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Size = new Size(980, 680);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.5f);
            DoubleBuffered = true;
            Padding = new Padding(2);

            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Shade(Theme.Background, 3) };
            var eyebrow = MakeLabel("CODEX  /  CONTROL DECK", 9f, Theme.Tertiary, FontStyle.Bold);
            eyebrow.Location = new Point(28, 15);
            var heading = MakeLabel("Settings", 22f, Theme.Text, FontStyle.Bold);
            heading.Location = new Point(25, 32);
            var close = CompactButton("×", Theme.Secondary);
            close.Size = new Size(48, 44);
            close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            header.Controls.AddRange([eyebrow, heading, close]);
            void LayoutHeader() => close.Location = new Point(header.ClientSize.Width - close.Width - 18, 16);
            header.Resize += (_, _) => LayoutHeader();
            LayoutHeader();
            header.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0);
            };

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 78, BackColor = Shade(Theme.Background, 3) };
            var cancel = DialogButton("Cancel", Theme.Muted, 104);
            cancel.DialogResult = DialogResult.Cancel;
            var save = DialogButton("Save changes", Theme.Primary, 138);
            save.Click += async (_, _) => await SaveAndCloseAsync();
            footer.Controls.AddRange([cancel, save]);
            void LayoutFooter()
            {
                save.Location = new Point(footer.ClientSize.Width - save.Width - 28, 18);
                cancel.Location = new Point(save.Left - cancel.Width - 10, 18);
            }
            footer.Resize += (_, _) => LayoutFooter();
            LayoutFooter();

            var rail = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(18, 24, 18, 18), BackColor = Shade(Theme.Panel, -5) };
            AddNavigation(rail, "◫   Dashboard", 0);
            AddNavigation(rail, "⌂   Codex Data", 1);
            AddNavigation(rail, "▣   Snapshot Export", 2);
            AddNavigation(rail, "⌁   Network Reporting", 3);

            pageHost.BackColor = Theme.Background;
            Controls.Add(pageHost);
            Controls.Add(rail);
            Controls.Add(footer);
            Controls.Add(header);
            AcceptButton = save;
            CancelButton = cancel;

            foreach (var value in new[] { 1, 2, 5, 10, 15, 30, 60 }) refresh.Items.Add(value);
            refresh.SelectedItem = settings.RefreshMinutes;
            foreach (var palette in ThemeCatalog.All) theme.Items.Add(palette.Name);
            theme.SelectedItem = settings.Theme;
            sessions.Text = string.IsNullOrWhiteSpace(settings.SessionsFolder) ? LogScanner.DefaultSessionsPath : settings.SessionsFolder;
            snapshotEnabled.Checked = settings.SnapshotEnabled;
            snapshotFolder.Text = settings.SnapshotFolder;
            foreach (var value in new[] { 5, 15, 30, 60 }) snapshotInterval.Items.Add(value);
            snapshotInterval.SelectedItem = settings.SnapshotMinutes;
            hideProjects.Checked = settings.HideProjectNames;
            networkEnabled.Checked = settings.NetworkEnabled;
            clientId.Text = settings.ClientId;
            clientId.ReadOnly = true;
            clientId.BackColor = Shade(Theme.Panel, 7);
            receiverUrl.Text = settings.ReceiverUrl;
            machineName.Text = settings.MachineName;
            foreach (var value in new[] { "Anonymous project IDs", "Project names", "No project breakdown" }) networkProjects.Items.Add(value);
            networkProjects.SelectedItem = settings.NetworkProjectMode switch
            {
                "names" => "Project names",
                "none" => "No project breakdown",
                _ => "Anonymous project IDs"
            };
            networkStatus.Text = NetworkReporter.LoadState().LastStatus;
            ShowPage(0);
        }

        private void AddNavigation(Panel rail, string text, int index)
        {
            var button = new Button
            {
                Text = text,
                Location = new Point(18, 24 + index * 58),
                Width = 194,
                Height = 58,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(15, 0, 0, 0),
                BackColor = Color.Transparent,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 10f),
                Cursor = Cursors.Hand,
                Tag = index
            };
            button.Click += (_, _) => ShowPage((int)button.Tag!);
            navigation.Add(button);
            rail.Controls.Add(button);
        }

        private void ShowPage(int index)
        {
            selectedPage = index;
            for (var i = 0; i < navigation.Count; i++)
            {
                navigation[i].BackColor = i == index ? Color.FromArgb(36, Theme.Primary) : Color.Transparent;
                navigation[i].ForeColor = i == index ? Theme.Text : Theme.Muted;
            }
            pageHost.Controls.Clear();
            pageHost.Controls.Add(index switch
            {
                0 => DashboardPage(),
                1 => DataPage(),
                2 => SnapshotPage(),
                _ => NetworkPage()
            });
        }

        private Control DashboardPage()
        {
            var page = NewPage("Dashboard", "Tune the live experience without leaving the cockpit.");
            page.Controls.Add(SettingCard("COLOR SCHEME", "Choose the visual atmosphere.", theme));
            page.Controls.Add(SettingCard("REFRESH INTERVAL", "How often local session logs are rescanned.", refresh, "minutes"));
            return page;
        }

        private Control DataPage()
        {
            var page = NewPage("Codex Data", "Control where local usage is discovered.");
            var browse = CompactButton("Browse…", Theme.Secondary);
            browse.Click += (_, _) => PickFolder(sessions, "Choose the Codex sessions folder", false, LogScanner.DefaultSessionsPath);
            page.Controls.Add(PathSettingCard("SESSION LOG LOCATION", "Resolved from your home directory by default. No username is hard-coded.", sessions, browse));
            return page;
        }

        private Control SnapshotPage()
        {
            var page = NewPage("Snapshot Export", "Keep one fresh dashboard image wherever you need it.");
            var browse = CompactButton("Browse…", Theme.Secondary);
            browse.Click += (_, _) => PickFolder(snapshotFolder, "Choose a snapshot destination", true,
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            page.Controls.Add(SettingCard("LATEST SNAPSHOT", "Export codex-usage-latest.png automatically.", snapshotEnabled));
            page.Controls.Add(PathSettingCard("DESTINATION", "Local or user-managed synchronized folder.", snapshotFolder, browse));
            page.Controls.Add(SettingCard("CAPTURE INTERVAL", "The latest PNG replaces the previous capture.", snapshotInterval, "minutes"));
            page.Controls.Add(SettingCard("PROJECT PRIVACY", "Replace project names with anonymous labels in exported images.", hideProjects));
            return page;
        }

        private Control NetworkPage()
        {
            var page = NewPage("Network Reporting", "Encrypted aggregate reporting across your private network.");
            var card = new Panel { Width = 630, Height = 396, BackColor = Shade(Theme.Panel, 2), Padding = new Padding(24) };
            var badge = MakeLabel("AGGREGATES ONLY  /  RAW LOGS NEVER LEAVE THIS MACHINE", 8.5f, Theme.Tertiary, FontStyle.Bold);
            badge.Location = new Point(22, 18);
            AddNetworkRow(card, "REPORT TO RECEIVER", networkEnabled, 44);
            clientId.Width = 330;
            AddNetworkRow(card, "CLIENT ID  /  COPY TO RECEIVER", clientId, 84);
            receiverUrl.Width = 330;
            AddNetworkRow(card, "RECEIVER ADDRESS", receiverUrl, 124);
            machineName.Width = 220;
            AddNetworkRow(card, "MACHINE NAME", machineName, 164);
            passphrase.Width = 220;
            AddNetworkRow(card, "SHARED PASSPHRASE", passphrase, 204, "Leave blank to keep the configured key");
            networkProjects.Width = 220;
            AddNetworkRow(card, "PROJECT DETAIL", networkProjects, 252);
            var test = CompactButton("Test connection", Theme.Secondary);
            test.Size = new Size(132, 40);
            test.Location = new Point(20, 300);
            test.Click += async (_, _) => await TestConnectionAsync(test);
            var upload = CompactButton("Force full upload", Theme.Secondary);
            upload.Size = new Size(150, 40);
            upload.Location = new Point(166, 300);
            upload.Click += async (_, _) => await ForceFullUploadAsync(upload);
            networkStatus.Location = new Point(20, 354);
            networkStatus.MaximumSize = new Size(588, 36);
            var manage = CompactButton("Manage receiver clients", Theme.Tertiary);
            manage.Size = new Size(176, 40);
            manage.Location = new Point(432, 300);
            manage.Enabled = ReceiverClientManager.IsAvailable;
            manage.Click += (_, _) =>
            {
                using var dialog = new ReceiverClientsDialog();
                dialog.ShowDialog(this);
                networkStatus.ForeColor = Theme.Muted;
                networkStatus.Text = "Enter the matching passphrase on each rotated client.";
            };
            if (!manage.Enabled) manage.Text = "Receiver not on this PC";
            card.Controls.AddRange([badge, test, upload, networkStatus, manage]);
            page.Controls.Add(card);
            return page;
        }

        private static void AddNetworkRow(Panel card, string label, Control input, int y, string? hint = null)
        {
            var labelControl = MakeLabel(label, 8.5f, Theme.Muted, FontStyle.Bold);
            labelControl.Location = new Point(20, y + 8);
            input.Location = new Point(255, y);
            card.Controls.AddRange([labelControl, input]);
            if (hint is null) return;
            var hintControl = MakeLabel(hint, 8f, Theme.Muted);
            hintControl.Location = new Point(255, y + 31);
            card.Controls.Add(hintControl);
        }

        private async Task TestConnectionAsync(Control button)
        {
            button.Enabled = false;
            networkStatus.ForeColor = Theme.Muted;
            networkStatus.Text = "Testing encrypted exchange…";
            try
            {
                Result = BuildResult();
                if (!string.IsNullOrWhiteSpace(passphrase.Text))
                    Result = await NetworkReporter.ConfigurePassphraseAsync(Result, passphrase.Text, CancellationToken.None);
                var state = await NetworkReporter.TestAsync(Result, CancellationToken.None);
                var success = state.LastStatus == "Encrypted connection verified";
                networkStatus.ForeColor = success ? Theme.Primary : Theme.Secondary;
                networkStatus.Text = success ? "Encrypted connection verified" : state.LastStatus;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or CryptographicException or
                                          FormatException or UriFormatException or TaskCanceledException)
            {
                networkStatus.ForeColor = Theme.Secondary;
                networkStatus.Text = ex.Message;
            }
            finally { button.Enabled = true; }
        }

        private async Task ForceFullUploadAsync(Control button)
        {
            button.Enabled = false;
            networkStatus.ForeColor = Theme.Muted;
            networkStatus.Text = "Uploading full 30-day history…";
            try
            {
                Result = BuildResult();
                if (!string.IsNullOrWhiteSpace(passphrase.Text))
                    Result = await NetworkReporter.ConfigurePassphraseAsync(Result, passphrase.Text, CancellationToken.None);
                if (!Result.NetworkEnabled)
                    throw new InvalidDataException("Enable reporting before uploading.");
                await NetworkReporter.SyncAsync(Result, true, CancellationToken.None);
                var state = NetworkReporter.LoadState();
                var success = state.LastStatus == "Full reconciliation complete";
                networkStatus.ForeColor = success ? Theme.Primary : Theme.Secondary;
                networkStatus.Text = state.LastStatus;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or CryptographicException or
                                          FormatException or UriFormatException or TaskCanceledException)
            {
                networkStatus.ForeColor = Theme.Secondary;
                networkStatus.Text = ex.Message;
            }
            finally { button.Enabled = true; }
        }

        private FlowLayoutPanel NewPage(string title, string subtitle)
        {
            var page = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                BackColor = Theme.Background,
                Padding = new Padding(0)
            };
            var titleLabel = MakeLabel(title, 24f, Theme.Text, FontStyle.Bold);
            titleLabel.Margin = new Padding(0, 0, 0, 2);
            var subtitleLabel = MakeLabel(subtitle, 10f, Theme.Muted);
            subtitleLabel.Margin = new Padding(2, 0, 0, 18);
            page.Controls.Add(titleLabel);
            page.Controls.Add(subtitleLabel);
            return page;
        }

        private Panel SettingCard(string title, string description, Control input, string? suffix = null, Control? action = null)
        {
            var card = new Panel { Width = 630, Height = 78, BackColor = Shade(Theme.Panel, 2), Margin = new Padding(0, 0, 0, 8) };
            var titleLabel = MakeLabel(title, 8.5f, Theme.Tertiary, FontStyle.Bold);
            titleLabel.Location = new Point(20, 11);
            var descriptionLabel = MakeLabel(description, 9f, Theme.Muted);
            descriptionLabel.Location = new Point(20, 35);
            descriptionLabel.MaximumSize = new Size(350, 38);
            card.Controls.AddRange([titleLabel, descriptionLabel]);
            input.Location = new Point(425, 22);
            card.Controls.Add(input);
            if (suffix is not null)
            {
                var suffixLabel = MakeLabel(suffix, 9f, Theme.Muted);
                suffixLabel.Location = new Point(input.Right + 8, 29);
                card.Controls.Add(suffixLabel);
            }
            if (action is not null)
            {
                action.Location = new Point(560, 27);
                card.Controls.Add(action);
            }
            return card;
        }

        private Panel PathSettingCard(string title, string description, PathDisplay path, Control action)
        {
            var card = new Panel { Width = 630, Height = 126, BackColor = Shade(Theme.Panel, 2), Margin = new Padding(0, 0, 0, 8) };
            var titleLabel = MakeLabel(title, 8.5f, Theme.Tertiary, FontStyle.Bold);
            titleLabel.Location = new Point(20, 11);
            var descriptionLabel = MakeLabel(description, 9f, Theme.Muted);
            descriptionLabel.Location = new Point(20, 35);
            path.Location = new Point(20, 58);
            path.Size = new Size(590, 25);
            action.Location = new Point(20, 87);
            action.Size = new Size(96, 35);
            card.Controls.AddRange([titleLabel, descriptionLabel, path, action]);
            return card;
        }

        private void PickFolder(PathDisplay target, string description, bool allowNew, string fallback)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = allowNew,
                InitialDirectory = Directory.Exists(target.Text) ? target.Text : fallback
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        }

        private async Task SaveAndCloseAsync()
        {
            if (string.IsNullOrWhiteSpace(sessions.Text) || !Directory.Exists(sessions.Text))
            {
                ShowPage(1);
                MessageBox.Show(this, "Choose an existing Codex sessions folder.", "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (snapshotEnabled.Checked && string.IsNullOrWhiteSpace(snapshotFolder.Text))
            {
                ShowPage(2);
                MessageBox.Show(this, "Choose a snapshot destination or turn snapshot export off.", "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Result = BuildResult();
                if (!string.IsNullOrWhiteSpace(passphrase.Text))
                    Result = await NetworkReporter.ConfigurePassphraseAsync(Result, passphrase.Text, CancellationToken.None);
                if (Result.NetworkEnabled && string.IsNullOrWhiteSpace(Result.ProtectedNetworkKey))
                {
                    ShowPage(3);
                    networkStatus.ForeColor = Theme.Secondary;
                    networkStatus.Text = "Set the shared passphrase before enabling reporting.";
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or CryptographicException or
                                          FormatException or UriFormatException or TaskCanceledException)
            {
                ShowPage(3);
                networkStatus.ForeColor = Theme.Secondary;
                networkStatus.Text = ex.Message;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private DashboardSettings BuildResult()
        {
            if (!Uri.TryCreate(receiverUrl.Text.Trim(), UriKind.Absolute, out var receiver) || receiver.Scheme != Uri.UriSchemeHttp)
                throw new UriFormatException("Receiver address must be an http:// LAN address.");
            if (string.IsNullOrWhiteSpace(machineName.Text)) throw new InvalidDataException("Machine name is required.");
            return Result with
            {
                RefreshMinutes = refresh.SelectedItem is int refreshValue ? refreshValue : 5,
                Theme = theme.SelectedItem?.ToString() ?? ThemeCatalog.All[0].Name,
                SessionsFolder = Path.GetFullPath(sessions.Text),
                SnapshotEnabled = snapshotEnabled.Checked,
                SnapshotFolder = string.IsNullOrWhiteSpace(snapshotFolder.Text) ? "" : Path.GetFullPath(snapshotFolder.Text),
                SnapshotMinutes = snapshotInterval.SelectedItem is int snapshotValue ? snapshotValue : 15,
                HideProjectNames = hideProjects.Checked,
                NetworkEnabled = networkEnabled.Checked,
                ReceiverUrl = receiver.ToString().TrimEnd('/'),
                MachineName = machineName.Text.Trim(),
                NetworkProjectMode = networkProjects.SelectedItem?.ToString() switch
                {
                    "Project names" => "names",
                    "No project breakdown" => "none",
                    _ => "anonymous"
                }
            };
        }

        private static NeonSelect MakeCombo() => new()
        {
            Width = 150,
            Height = 34,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f)
        };

        private static PathDisplay MakePathDisplay() => new()
        {
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            ForeColor = Theme.Text,
            Font = new Font("Cascadia Mono", 9f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        private static TextBox MakeInput(bool secret = false) => new()
        {
            Height = 30,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Shade(Theme.Panel, 10),
            ForeColor = Theme.Text,
            Font = new Font(secret ? "Segoe UI" : "Cascadia Mono", 9f),
            UseSystemPasswordChar = secret
        };

        private static Label MakeLabel(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            Font = new Font("Segoe UI", size, style),
            BackColor = Color.Transparent
        };

        private static Button CompactButton(string text, Color accent) => new NeonButton(accent)
        {
            Text = text,
            Width = 88,
            Height = 40,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 8.5f),
            Cursor = Cursors.Hand
        };

        private static Button DialogButton(string text, Color accent, int width) => new NeonButton(accent)
        {
            Text = text,
            Width = width,
            Height = 46,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Cursor = Cursors.Hand
        };

        private static Color Shade(Color color, int amount) => Color.FromArgb(color.A,
            Math.Clamp(color.R + amount, 0, 255), Math.Clamp(color.G + amount, 0, 255), Math.Clamp(color.B + amount, 0, 255));

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width <= 5 || ClientSize.Height <= 5) return;
            using var glow = new Pen(Color.FromArgb(70, Theme.Primary), 6f);
            using var edge = new Pen(Color.FromArgb(235, Theme.Primary), 1.5f);
            var bounds = new Rectangle(2, 2, ClientSize.Width - 5, ClientSize.Height - 5);
            e.Graphics.DrawRectangle(glow, bounds);
            e.Graphics.DrawRectangle(edge, bounds);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern nint SendMessage(nint hWnd, int message, int wParam, int lParam);
    }

    private sealed class ReceiverClientsDialog : Form
    {
        private readonly CheckedListBox clientList = new() { CheckOnClick = true };
        private readonly TextBox password = new() { UseSystemPasswordChar = true };
        private readonly TextBox confirmation = new() { UseSystemPasswordChar = true };
        private readonly Label feedback = new() { AutoSize = true };
        private readonly ReceiverClientEntry[] clients;

        public ReceiverClientsDialog()
        {
            clients = ReceiverClientManager.LoadClients().ToArray();
            Text = "Receiver clients";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 430);
            BackColor = Bg;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f);

            var title = new Label
            {
                Text = "MANAGE RECEIVER CLIENTS", AutoSize = true, Location = new Point(28, 24),
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold), ForeColor = TextMain
            };
            var explanation = new Label
            {
                Text = "Select one or more machines, set their new shared passphrase, then enter that same passphrase in each selected app.",
                Location = new Point(30, 61), Size = new Size(555, 42), ForeColor = TextMuted
            };
            clientList.Location = new Point(30, 108);
            clientList.Size = new Size(560, 116);
            clientList.BackColor = Panel;
            clientList.ForeColor = TextMain;
            clientList.BorderStyle = BorderStyle.FixedSingle;
            foreach (var client in clients) clientList.Items.Add($"{client.MachineName}   ({client.ClientId})");

            var passwordLabel = new Label { Text = "NEW SHARED PASSPHRASE", AutoSize = true, Location = new Point(30, 246), ForeColor = TextMuted };
            password.Location = new Point(30, 269); password.Size = new Size(270, 28); password.BackColor = Panel; password.ForeColor = TextMain;
            var confirmLabel = new Label { Text = "CONFIRM PASSPHRASE", AutoSize = true, Location = new Point(320, 246), ForeColor = TextMuted };
            confirmation.Location = new Point(320, 269); confirmation.Size = new Size(270, 28); confirmation.BackColor = Panel; confirmation.ForeColor = TextMain;
            feedback.Location = new Point(31, 310);
            feedback.MaximumSize = new Size(555, 38);
            feedback.ForeColor = TextMuted;
            feedback.Text = "Tick multiple machines to give them the same passphrase in one step.";

            var rotate = MakeButton("Rotate selected clients", 190, Theme.Tertiary);
            rotate.Location = new Point(400, 360);
            rotate.Click += (_, _) => RotateSelected();
            var close = MakeButton("Close", 100, Theme.Muted);
            close.Location = new Point(290, 360);
            close.Click += (_, _) => Close();
            Controls.AddRange([title, explanation, clientList, passwordLabel, password, confirmLabel, confirmation, feedback, close, rotate]);
        }

        private void RotateSelected()
        {
            feedback.ForeColor = Theme.Secondary;
            if (password.Text != confirmation.Text)
            {
                feedback.Text = "The two passphrases do not match.";
                return;
            }
            try
            {
                var ids = clientList.CheckedIndices.Cast<int>().Select(index => clients[index].ClientId).ToArray();
                ReceiverClientManager.Rotate(ids, password.Text);
                password.Clear();
                confirmation.Clear();
                feedback.ForeColor = Theme.Primary;
                feedback.Text = $"Rotated {ids.Length} client{(ids.Length == 1 ? "" : "s")}. No receiver restart is needed.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                          CryptographicException or InvalidDataException or InvalidOperationException)
            {
                feedback.Text = ex.Message;
            }
        }
    }

    private sealed class PathDisplay : Label
    {
        private readonly ToolTip tip = new() { InitialDelay = 350, ReshowDelay = 100 };

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            tip.SetToolTip(this, Text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class NeonSelect : Control
    {
        private object? selectedItem;
        private bool hovering;
        private ContextMenuStrip? choicesMenu;

        public List<object> Items { get; } = [];
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object? SelectedItem
        {
            get => selectedItem;
            set { selectedItem = value; Invalidate(); }
        }

        public NeonSelect()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); ShowChoices(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
            {
                ShowChoices();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private void ShowChoices()
        {
            if (choicesMenu is null)
            {
                choicesMenu = new ContextMenuStrip
                {
                    BackColor = Theme.Panel,
                    ForeColor = Theme.Text,
                    Font = Font,
                    ShowImageMargin = false,
                    Renderer = new NightCityMenuRenderer()
                };
                foreach (var choice in Items)
                {
                    var item = new ToolStripMenuItem(choice.ToString())
                    {
                        Tag = choice,
                        Padding = new Padding(10, 5, 18, 5)
                    };
                    item.Click += (_, _) => SelectedItem = item.Tag;
                    choicesMenu.Items.Add(item);
                }
            }
            foreach (ToolStripMenuItem item in choicesMenu.Items)
            {
                var current = Equals(item.Tag, SelectedItem);
                item.ForeColor = current ? Theme.Primary : Theme.Text;
            }
            choicesMenu.Show(this, new Point(0, Height + 3));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) choicesMenu?.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width <= 5 || ClientSize.Height <= 7) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(2, 3, Width - 5, Height - 7);
            using var path = Rounded(bounds, 7);
            using var fill = new LinearGradientBrush(bounds, Shade(Theme.Panel, 13), Shade(Theme.Panel, 3), 90f);
            using var glow = new Pen(Color.FromArgb(hovering || Focused ? 95 : 40, Theme.Primary), hovering || Focused ? 5f : 3f);
            using var edge = new Pen(Color.FromArgb(hovering || Focused ? 240 : 165, Theme.Primary), 1.2f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(glow, path);
            e.Graphics.DrawPath(edge, path);
            var textBounds = new Rectangle(13, 0, Width - 43, Height);
            TextRenderer.DrawText(e.Graphics, SelectedItem?.ToString() ?? "Select…", Font, textBounds, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            var cx = Width - 19;
            var cy = Height / 2 + 1;
            using var arrow = new Pen(Theme.Primary, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawLine(arrow, cx - 4, cy - 2, cx, cy + 2);
            e.Graphics.DrawLine(arrow, cx, cy + 2, cx + 4, cy - 2);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Shade(Color color, int amount) => Color.FromArgb(color.A,
            Math.Clamp(color.R + amount, 0, 255), Math.Clamp(color.G + amount, 0, 255), Math.Clamp(color.B + amount, 0, 255));
    }

    private sealed class NeonToggle : CheckBox
    {
        public NeonToggle()
        {
            AutoSize = false;
            Size = new Size(58, 30);
            Cursor = Cursors.Hand;
            Text = "";
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Opaque, true);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent is not null) BackColor = Parent.BackColor;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width <= 4 || ClientSize.Height <= 8) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
            var track = new Rectangle(2, 4, Width - 4, Height - 8);
            using var path = RoundedRect(track, track.Height / 2);
            using var fill = new SolidBrush(Checked ? Color.FromArgb(115, Theme.Primary) : Color.FromArgb(65, Theme.Muted));
            using var edge = new Pen(Checked ? Theme.Primary : Theme.Muted, 1.2f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(edge, path);
            var diameter = track.Height - 6;
            var x = Checked ? track.Right - diameter - 3 : track.Left + 3;
            using var knob = new SolidBrush(Checked ? Color.White : Color.FromArgb(205, Theme.Muted));
            e.Graphics.FillEllipse(knob, x, track.Top + 3, diameter, diameter);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class NightCityMenuRenderer : ToolStripProfessionalRenderer
    {
        public NightCityMenuRenderer() : base(new NightCityColorTable()) { RoundedEdges = true; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            using var fill = new SolidBrush(e.Item.Selected ? Color.FromArgb(82, Theme.Primary) : Theme.Panel);
            e.Graphics.FillRectangle(fill, bounds);
            if (e.Item.Selected)
            {
                using var edge = new Pen(Color.FromArgb(210, Theme.Primary), 1f);
                e.Graphics.DrawRectangle(edge, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
            }
        }
    }

    private sealed class NightCityColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color MenuBorder => Color.FromArgb(205, Theme.Tertiary);
        public override Color MenuItemSelected => Color.FromArgb(62, Theme.Tertiary);
        public override Color MenuItemBorder => Color.FromArgb(150, Theme.Tertiary);
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color CheckBackground => Color.FromArgb(80, Theme.Tertiary);
        public override Color CheckSelectedBackground => Color.FromArgb(110, Theme.Tertiary);
    }

    private async Task RebuildHistoryAsync(bool automatic = false)
    {
        if (isRebuilding) return;
        isRebuilding = true;
        rebuild.Enabled = false;
        if (!automatic) status.Text = "Rebuilding 30-day history…";
        try
        {
            var history = await LogScanner.ScanHistoryAsync(
                30, refreshCts?.Token ?? CancellationToken.None, appSettings.SessionsFolder);
            HistoryStore.Save(history);
            localHistory = history;
            ApplySelectedView();
            status.Text = $"30-day history built {history.BuiltAt:h:mm tt}";
            await SyncNetworkAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            status.Text = "Couldn’t rebuild history";
            if (!automatic) MessageBox.Show(this, ex.Message, "Codex Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { rebuild.Enabled = true; isRebuilding = false; }
    }

    private static Button MakeButton(string text, int width, Color accent) => new NeonButton(accent)
    {
        Text = text,
        Width = width,
        Height = 40,
        BackColor = Panel,
        ForeColor = TextMain,
        Font = new Font("Segoe UI Semibold", 9f),
        Cursor = Cursors.Hand,
        TabStop = false
    };

    private sealed class NeonButton : Button
    {
        private Color accent;
        private bool hovering;

        public NeonButton(Color accent)
        {
            this.accent = accent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetAccent(Color color) { accent = color; BackColor = Panel; ForeColor = TextMain; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (ClientSize.Width <= 11 || ClientSize.Height <= 11) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Bg);
            var bounds = new Rectangle(5, 5, Width - 11, Height - 11);
            using var path = ButtonPath(bounds, 7);
            using var fill = new LinearGradientBrush(bounds,
                hovering ? Color.FromArgb(48, accent) : Shift(Panel, 8),
                Shift(Panel, -7), 90f);
            g.FillPath(fill, path);
            using var glow = new Pen(Color.FromArgb(hovering ? 80 : 36, accent), hovering ? 6f : 4f);
            using var edge = new Pen(Color.FromArgb(hovering ? 245 : 190, accent), 1.35f);
            g.DrawPath(glow, path);
            g.DrawPath(edge, path);
            var horizontalPadding = Width <= 50 ? 3 : 10;
            var textBounds = Rectangle.Inflate(bounds, -horizontalPadding, -2);
            TextRenderer.DrawText(g, Text, Font, textBounds, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }

        private static GraphicsPath ButtonPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Shift(Color color, int amount) => Color.FromArgb(color.A,
            Math.Clamp(color.R + amount, 0, 255), Math.Clamp(color.G + amount, 0, 255),
            Math.Clamp(color.B + amount, 0, 255));
    }

    private sealed class UsageCanvas : Control
    {
        private static Color[] Palette => Theme.Series;
        private Rectangle historyHitArea;
        private DailyUsage[] historyDays = [];
        private DateTime? hoveredDate;
        private Rectangle hourlyHitArea;
        private long[] hourlyTotals = [];
        private int? hoveredHour;
        private DateTime? pinnedDate;
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public UsageSnapshot? Snapshot { get; set; }
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HistoryCache? History { get; set; }
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public NetworkSyncState? NetworkState { get; set; }
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string SourceLabel { get; set; } = "This PC";

        public UsageCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Bg;
            SetStyle(ControlStyles.Selectable, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (hourlyTotals.Length == 24 && hourlyHitArea.Contains(e.Location))
            {
                var ratio = Math.Clamp((e.X - hourlyHitArea.Left) / (float)Math.Max(1, hourlyHitArea.Width), 0, .9999f);
                var nextHour = Math.Clamp((int)(ratio * 24), 0, 23);
                Cursor = Cursors.Hand;
                if (hoveredHour != nextHour || hoveredDate is not null)
                {
                    hoveredHour = nextHour;
                    hoveredDate = null;
                    Invalidate();
                }
                return;
            }
            if (historyDays.Length > 0 && historyHitArea.Contains(e.Location))
            {
                var ratio = Math.Clamp((e.X - historyHitArea.Left) / (float)Math.Max(1, historyHitArea.Width), 0, 1);
                var index = Math.Clamp((int)Math.Round(ratio * (historyDays.Length - 1)), 0, historyDays.Length - 1);
                var next = historyDays[index].Date;
                Cursor = Cursors.Hand;
                if (hoveredDate?.Date != next.Date || hoveredHour is not null)
                {
                    hoveredDate = next;
                    hoveredHour = null;
                    Invalidate();
                }
                return;
            }
            if (hoveredDate is not null || hoveredHour is not null)
            {
                hoveredDate = null;
                hoveredHour = null;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            if (hoveredDate is not null || hoveredHour is not null)
            { hoveredDate = null; hoveredHour = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || historyDays.Length == 0 || !historyHitArea.Contains(e.Location)) return;
            var ratio = Math.Clamp((e.X - historyHitArea.Left) / (float)Math.Max(1, historyHitArea.Width), 0, 1);
            var index = Math.Clamp((int)Math.Round(ratio * (historyDays.Length - 1)), 0, historyDays.Length - 1);
            var clicked = historyDays[index].Date.Date;
            pinnedDate = clicked == DateTime.Today || pinnedDate?.Date == clicked ? null : clicked;
            hoveredHour = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Render(e.Graphics, false);
        }

        public void ExportPng(string path, bool hideProjectNames)
        {
            using var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var savedHoveredDate = hoveredDate;
            var savedHoveredHour = hoveredHour;
            var savedPinnedDate = pinnedDate;
            try
            {
                hoveredDate = null;
                hoveredHour = null;
                pinnedDate = null;
                Render(graphics, hideProjectNames);
                bitmap.Save(path, ImageFormat.Png);
            }
            finally
            {
                hoveredDate = savedHoveredDate;
                hoveredHour = savedHoveredHour;
                pinnedDate = savedPinnedDate;
            }
        }

        private void Render(Graphics g, bool hideProjectNames)
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var background = new LinearGradientBrush(ClientRectangle,
                Lighten(Bg, 3), Darken(Bg, 4), 35f))
                g.FillRectangle(background, ClientRectangle);
            DrawAmbientLights(g);
            if (Snapshot is null) { DrawCentered(g, "Reading today’s usage…"); return; }

            var margin = Math.Max(28, Width / 35);
            var hero = new Rectangle(margin, 16, Width - margin * 2, 195);
            var chartHeight = Math.Max(220, (Height - 277) / 2);
            var contentWidth = Width - margin * 2;
            var hourlyWidth = (int)((contentWidth - 19) * .66f);
            var chart = new Rectangle(margin, 230, hourlyWidth, chartHeight);
            var projects = new Rectangle(chart.Right + 19, 230, contentWidth - hourlyWidth - 19, chartHeight);
            var history = new Rectangle(margin, chart.Bottom + 19, contentWidth, chartHeight);
            FillRound(g, hero, 22, Panel);
            FillRound(g, chart, 22, Panel);
            FillRound(g, projects, 22, Panel);
            FillRound(g, history, 22, Panel);
            StrokeRound(g, hero, 22, Color.FromArgb(55, Theme.Secondary));
            StrokeRound(g, chart, 22, Color.FromArgb(45, Theme.Primary));
            StrokeRound(g, projects, 22, Color.FromArgb(45, Theme.Tertiary));
            StrokeRound(g, history, 22, Color.FromArgb(45, Theme.Secondary));
            DrawHero(g, hero, Snapshot, NetworkState, SourceLabel);
            DrawChart(g, chart, Snapshot, History, pinnedDate);
            DrawProjects(g, projects, Snapshot, History, hoveredDate, hoveredHour, pinnedDate, hideProjectNames);
            DrawHistory(g, history, History);
        }

        private static void DrawHero(Graphics g, Rectangle r, UsageSnapshot s, NetworkSyncState? network, string sourceLabel)
        {
            using var label = new Font("Segoe UI Semibold", 10f);
            using var value = new Font("Segoe UI Variable Display", 37f, FontStyle.Bold);
            using var small = new Font("Segoe UI Semibold", 10f);
            g.DrawString("TODAY’S TOKEN USAGE", label, new SolidBrush(TextMuted), r.X + 28, r.Y + 24);
            g.DrawString(Compact(s.Total), value, new SolidBrush(TextMain), r.X + 24, r.Y + 47);
            var cards = new[]
            {
                ("INPUT", s.Input, Palette[0]),
                ("CACHED", s.Cached, Palette[1]),
                ("OUTPUT", s.Output, Palette[2]),
                ("REASONING", s.Reasoning, Palette[3])
            };
            var x = r.X + Math.Max(310, r.Width / 3);
            var width = Math.Max(110, (r.Right - x - 18) / 4);
            foreach (var item in cards)
            {
                using var dot = new SolidBrush(item.Item3);
                g.FillEllipse(dot, x, r.Y + 61, 8, 8);
                g.DrawString(item.Item1, small, new SolidBrush(TextMuted), x + 14, r.Y + 55);
                using var metric = new Font("Segoe UI Variable Display Semibold", 19f);
                g.DrawString(Compact(item.Item2), metric, new SolidBrush(TextMain), x, r.Y + 82);
                x += width;
            }
            if (!string.Equals(sourceLabel, "All machines", StringComparison.OrdinalIgnoreCase) &&
                network?.Combined is { } combined && network.LastSuccessUtc is { } received)
            {
                using var global = new Font("Segoe UI Semibold", 9f);
                g.DrawString($"ALL MACHINES  {Compact(combined.Total)}  ·  synced {received.ToLocalTime():h:mm tt}", global,
                    new SolidBrush(Theme.Tertiary), r.X + 29, r.Bottom - 61);
            }
            var detail = string.Equals(sourceLabel, "This PC", StringComparison.OrdinalIgnoreCase)
                ? $"{s.Responses:N0} responses across {s.FilesScanned:N0} log files"
                : $"{s.Responses:N0} responses  ·  {sourceLabel}";
            g.DrawString(detail, small,
                new SolidBrush(TextMuted), r.X + 29, r.Bottom - 38);
        }

        private void DrawChart(Graphics g, Rectangle r, UsageSnapshot s, HistoryCache? history, DateTime? selectedDate)
        {
            using var title = new Font("Segoe UI Semibold", 12f);
            using var caption = new Font("Segoe UI", 9f);
            var chartDate = selectedDate?.Date ?? DateTime.Today;
            var heading = chartDate == DateTime.Today ? "Usage through the day" : $"Usage through {chartDate:MMM d}";
            g.DrawString(heading, title, new SolidBrush(TextMain), r.X + 28, r.Y + 22);

            Dictionary<string, long[]> byModel;
            if (chartDate == DateTime.Today)
            {
                byModel = s.Aggregates.HourlyModels;
            }
            else
            {
                var day = history?.Days.FirstOrDefault(item => item.Date.Date == chartDate);
                byModel = (day?.Hours ?? []).SelectMany(hour => hour.Models.Select(model => (hour.Hour, model.Key, model.Value)))
                    .GroupBy(item => item.Key).ToDictionary(group => group.Key, group =>
                    {
                        var values = new long[24];
                        foreach (var item in group) values[item.Hour] = item.Value;
                        return values;
                    });
            }
            var models = byModel.OrderByDescending(model => model.Value.Sum()).ToArray();
            var legendX = r.Right - 28;
            for (var i = models.Length - 1; i >= 0; i--)
            {
                var text = $"●  {models[i].Key}";
                var size = g.MeasureString(text, caption);
                legendX -= (int)size.Width + 18;
                using var brush = new SolidBrush(Palette[i % Palette.Length]);
                g.DrawString(text, caption, brush, legendX, r.Y + 25);
            }

            var plot = Rectangle.FromLTRB(r.X + 60, r.Y + 72, r.Right - 28, r.Bottom - 42);
            var hourly = models.Select(model => model.Value).ToArray();
            var totals = Enumerable.Range(0, 24).Select(h => hourly.Sum(a => a[h])).ToArray();
            hourlyHitArea = plot;
            hourlyTotals = totals;
            var max = Math.Max(1, totals.Max());
            var niceMax = NiceCeiling(max);

            using var gridPen = new Pen(Color.FromArgb(48, TextMuted), 1);
            for (var i = 0; i <= 4; i++)
            {
                var y = plot.Bottom - plot.Height * i / 4f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                var label = Compact(niceMax * i / 4);
                var sz = g.MeasureString(label, caption);
                g.DrawString(label, caption, new SolidBrush(TextMuted), plot.Left - sz.Width - 10, y - sz.Height / 2);
            }

            var barSlot = plot.Width / 24f;
            var barWidth = Math.Max(5, barSlot * .64f);
            for (var h = 0; h < 24; h++)
            {
                var y = (float)plot.Bottom;
                for (var m = 0; m < models.Length; m++)
                {
                    var height = (float)(hourly[m][h] / (double)niceMax * plot.Height);
                    if (height <= 0) continue;
                    var color = Palette[m % Palette.Length];
                    var x = plot.Left + h * barSlot + (barSlot - barWidth) / 2;
                    DrawSoftBarGlow(g, new RectangleF(x, y - height, barWidth, height), color);
                    using var brush = new LinearGradientBrush(
                        new RectangleF(x, y - height, barWidth, Math.Max(1, height)),
                        Lighten(color, 80), Saturate(color, 1.28f), LinearGradientMode.Vertical);
                    g.FillRectangle(brush, x, y - height, barWidth, height);
                    using var shine = new Pen(Color.FromArgb(235, 235, 250, 255), 1.25f);
                    g.DrawLine(shine, x + 1, y - height, x + barWidth - 1, y - height);
                    y -= height;
                }
            }
            if (hoveredHour is { } selectedHour)
            {
                var x = plot.Left + selectedHour * barSlot;
                using var columnGlow = new SolidBrush(Color.FromArgb(22, Theme.Tertiary));
                using var columnEdge = new Pen(Color.FromArgb(120, Theme.Tertiary), 1f);
                g.FillRectangle(columnGlow, x + 1, plot.Top, barSlot - 2, plot.Height);
                g.DrawRectangle(columnEdge, x + 1, plot.Top, barSlot - 2, plot.Height);

                var barTop = plot.Bottom - totals[selectedHour] / (float)niceMax * plot.Height;
                var activeModels = models.Select((model, index) => new
                    {
                        model.Key,
                        Tokens = hourly[index][selectedHour],
                        Color = Palette[index % Palette.Length]
                    })
                    .Where(model => model.Tokens > 0)
                    .ToArray();
                var effortSource = chartDate == DateTime.Today ? s.Aggregates.HourlyEfforts[selectedHour]
                    : history?.Days.FirstOrDefault(day => day.Date.Date == chartDate)?.Hours?
                        .FirstOrDefault(hour => hour.Hour == selectedHour)?.Efforts;
                var efforts = (effortSource ?? new Dictionary<string, long> { ["Unknown"] = totals[selectedHour] })
                    .Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToArray();
                const int tooltipWidth = 230;
                var modelRows = activeModels.Length;
                var tooltipHeight = 54 + modelRows * 18 + (efforts.Length > 0
                    ? 24 + efforts.Length * 18 : 0);
                var tooltip = new Rectangle(
                    Math.Clamp((int)(x + barSlot / 2) - tooltipWidth / 2, plot.Left, plot.Right - tooltipWidth),
                    Math.Clamp((int)barTop - tooltipHeight - 14, 8, Math.Max(8, Height - tooltipHeight - 8)),
                    tooltipWidth, tooltipHeight);
                FillRound(g, tooltip, 8, Darken(Panel, 4));
                StrokeRound(g, tooltip, 8, Color.FromArgb(145, Theme.Tertiary));
                using var tipLabel = new Font("Segoe UI Semibold", 8.5f);
                using var tipValue = new Font("Segoe UI Semibold", 10f);
                g.DrawString(HourRange(selectedHour), tipLabel, new SolidBrush(TextMuted), tooltip.X + 9, tooltip.Y + 5);
                g.DrawString($"{totals[selectedHour]:N0} tokens", tipValue, new SolidBrush(TextMain), tooltip.X + 9, tooltip.Y + 21);
                for (var i = 0; i < activeModels.Length; i++)
                {
                    var model = activeModels[i];
                    var rowY = tooltip.Y + 47 + i * 18;
                    using var dot = new SolidBrush(model.Color);
                    g.FillEllipse(dot, tooltip.X + 10, rowY + 4, 7, 7);
                    var value = model.Tokens.ToString("N0");
                    var valueSize = g.MeasureString(value, tipLabel);
                    var nameBounds = new Rectangle(tooltip.X + 24, rowY, tooltip.Width - 41 - (int)valueSize.Width, 16);
                    TextRenderer.DrawText(g, model.Key, tipLabel, nameBounds, TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    g.DrawString(value, tipLabel, new SolidBrush(TextMain), tooltip.Right - valueSize.Width - 10, rowY);
                }
                if (efforts.Length > 0)
                {
                    var effortY = tooltip.Y + 47 + modelRows * 18;
                    using var divider = new Pen(Color.FromArgb(70, Theme.Tertiary));
                    g.DrawLine(divider, tooltip.X + 10, effortY + 1, tooltip.Right - 10, effortY + 1);
                    using var headingBrush = new SolidBrush(Theme.Tertiary);
                    g.DrawString("REASONING EFFORT", tipLabel, headingBrush, tooltip.X + 10, effortY + 5);
                    for (var i = 0; i < efforts.Length; i++)
                    {
                        var rowY = effortY + 24 + i * 18;
                        var value = efforts[i].Value.ToString("N0");
                        var valueSize = g.MeasureString(value, tipLabel);
                        TextRenderer.DrawText(g, efforts[i].Key, tipLabel,
                            new Rectangle(tooltip.X + 10, rowY, tooltip.Width - 27 - (int)valueSize.Width, 16),
                            TextMuted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                        using var valueBrush = new SolidBrush(TextMain);
                        g.DrawString(value, tipLabel, valueBrush, tooltip.Right - valueSize.Width - 10, rowY);
                    }
                }
            }
            foreach (var h in new[] { 0, 4, 8, 12, 16, 20, 23 })
            {
                var text = h == 0 ? "12a" : h < 12 ? $"{h}a" : h == 12 ? "12p" : $"{h - 12}p";
                var sz = g.MeasureString(text, caption);
                var x = plot.Left + (h + .5f) * barSlot - sz.Width / 2;
                g.DrawString(text, caption, new SolidBrush(TextMuted), x, plot.Bottom + 10);
            }
            if (totals.All(total => total == 0)) DrawCentered(g, $"No token usage recorded on {chartDate:MMM d}", plot);
        }

        private static void DrawProjects(Graphics g, Rectangle r, UsageSnapshot s, HistoryCache? history,
            DateTime? selectedDate, int? selectedHour, DateTime? pinnedDate, bool hideProjectNames)
        {
            using var title = new Font("Segoe UI Semibold", 12f);
            using var label = new Font("Segoe UI Semibold", 9f);
            using var caption = new Font("Segoe UI", 8.5f);
            g.DrawString("Usage by project", title, new SolidBrush(TextMain), r.X + 24, r.Y + 22);
            var effectiveDate = (selectedDate ?? pinnedDate ?? DateTime.Today).Date;
            var isHistoricalDay = effectiveDate != DateTime.Today;
            var dateLabel = selectedHour is { } hour
                ? $"{effectiveDate:MMM d} · {ShortHourRange(hour)}".ToUpperInvariant()
                : isHistoricalDay ? effectiveDate.ToString("MMM d").ToUpperInvariant() : "TODAY";
            var dateSize = g.MeasureString(dateLabel, caption);
            g.DrawString(dateLabel, caption, new SolidBrush(Theme.Tertiary), r.Right - dateSize.Width - 24, r.Y + 27);

            List<(string Name, long Tokens)> ranked;
            if (selectedHour is { } selected)
            {
                if (!isHistoricalDay)
                {
                    ranked = s.Aggregates.HourlyProjects[selected]
                        .Select(project => (Name: project.Key, Tokens: project.Value))
                        .OrderByDescending(project => project.Tokens).ToList();
                }
                else
                {
                    var hourBucket = history?.Days.FirstOrDefault(day => day.Date.Date == effectiveDate)?.Hours?
                        .FirstOrDefault(item => item.Hour == selected);
                    ranked = (hourBucket?.Projects ?? []).Select(project => (Name: project.Key, Tokens: project.Value))
                        .OrderByDescending(project => project.Tokens).ToList();
                }
            }
            else if (!isHistoricalDay)
            {
                ranked = s.Aggregates.Projects
                    .Select(project => (Name: project.Key, Tokens: project.Value))
                    .OrderByDescending(project => project.Tokens).ToList();
            }
            else
            {
                var day = history?.Days.FirstOrDefault(d => d.Date.Date == effectiveDate);
                ranked = (day?.Projects ?? []).Select(project => (Name: project.Key, Tokens: project.Value))
                    .OrderByDescending(project => project.Tokens).ToList();
            }
            if (ranked.Count > 6)
            {
                var other = ranked.Skip(5).Sum(project => project.Tokens);
                ranked = ranked.Take(5).Append(("Other", other)).ToList();
            }
            if (ranked.Count == 0)
            {
                DrawCentered(g, "No project usage today", Rectangle.FromLTRB(r.X, r.Y + 55, r.Right, r.Bottom));
                return;
            }

            var max = Math.Max(1, ranked[0].Tokens);
            var left = r.X + 24;
            var right = r.Right - 24;
            var available = r.Bottom - (r.Y + 70) - 18;
            var rowHeight = Math.Min(48, available / Math.Max(1, ranked.Count));
            for (var i = 0; i < ranked.Count; i++)
            {
                var item = ranked[i];
                var y = r.Y + 67 + i * rowHeight;
                var value = Compact(item.Tokens);
                var valueSize = g.MeasureString(value, caption);
                var nameRect = new Rectangle(left, y, Math.Max(40, right - left - (int)valueSize.Width - 12), 18);
                var displayName = hideProjectNames && item.Name != "Other" ? $"Project {i + 1}" : item.Name;
                TextRenderer.DrawText(g, displayName, label, nameRect, TextMain,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                g.DrawString(value, caption, new SolidBrush(TextMuted), right - valueSize.Width, y + 1);

                var track = new RectangleF(left, y + 23, right - left, 7);
                using var trackBrush = new SolidBrush(Color.FromArgb(45, TextMuted));
                g.FillRectangle(trackBrush, track);
                var fillWidth = Math.Max(2, track.Width * item.Tokens / (float)max);
                var color = Palette[i % Palette.Length];
                using var glow = new Pen(Color.FromArgb(52, color), 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var core = new Pen(Lighten(color, 38), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var x1 = track.Left + 3;
                var x2 = Math.Max(x1, track.Left + fillWidth - 3);
                g.DrawLine(glow, x1, track.Top + 3.5f, x2, track.Top + 3.5f);
                g.DrawLine(core, x1, track.Top + 3.5f, x2, track.Top + 3.5f);
            }
        }

        private void DrawHistory(Graphics g, Rectangle r, HistoryCache? history)
        {
            using var title = new Font("Segoe UI Semibold", 12f);
            using var caption = new Font("Segoe UI", 9f);
            g.DrawString("Rolling 30-day usage", title, new SolidBrush(TextMain), r.X + 28, r.Y + 22);
            if (history is null)
            {
                DrawCentered(g, "Building daily history…", Rectangle.FromLTRB(r.X, r.Y + 55, r.Right, r.Bottom));
                return;
            }
            g.DrawString($"Built {history.BuiltAt:MMM d, h:mm tt}", caption, new SolidBrush(TextMuted), r.Right - 145, r.Y + 27);
            var plot = Rectangle.FromLTRB(r.X + 60, r.Y + 72, r.Right - 28, r.Bottom - 42);
            var days = history.Days.OrderBy(d => d.Date).ToArray();
            historyHitArea = plot;
            historyDays = days;
            var max = Math.Max(1, days.Max(d => d.Tokens));
            var niceMax = NiceCeiling(max);
            using var gridPen = new Pen(Color.FromArgb(48, TextMuted), 1);
            for (var i = 0; i <= 4; i++)
            {
                var y = plot.Bottom - plot.Height * i / 4f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                var label = Compact(niceMax * i / 4);
                var size = g.MeasureString(label, caption);
                g.DrawString(label, caption, new SolidBrush(TextMuted), plot.Left - size.Width - 10, y - size.Height / 2);
            }
            if (days.Length > 1)
            {
                var points = days.Select((day, i) => new PointF(
                    plot.Left + i * plot.Width / (float)(days.Length - 1),
                    plot.Bottom - day.Tokens / (float)niceMax * plot.Height)).ToArray();
                using var area = new GraphicsPath();
                area.AddLines(points);
                area.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
                area.AddLine(points[^1].X, plot.Bottom, points[0].X, plot.Bottom);
                area.CloseFigure();
                using var areaBrush = new LinearGradientBrush(plot, Color.FromArgb(80, Theme.Primary),
                    Color.FromArgb(2, Theme.Secondary), LinearGradientMode.Vertical);
                g.FillPath(areaBrush, area);
                using var glowFar = new Pen(Color.FromArgb(13, Theme.Primary), 20f) { LineJoin = LineJoin.Round };
                using var glowMid = new Pen(Color.FromArgb(25, Theme.Primary), 14f) { LineJoin = LineJoin.Round };
                using var glowNear = new Pen(Color.FromArgb(48, Theme.Secondary), 9f) { LineJoin = LineJoin.Round };
                using var glowCore = new Pen(Color.FromArgb(76, Theme.Secondary), 5f) { LineJoin = LineJoin.Round };
                using var lineBrush = new LinearGradientBrush(plot, Theme.Primary, Theme.Secondary, 0f);
                using var line = new Pen(lineBrush, 2.8f) { LineJoin = LineJoin.Round };
                g.DrawLines(glowFar, points); g.DrawLines(glowMid, points);
                g.DrawLines(glowNear, points); g.DrawLines(glowCore, points); g.DrawLines(line, points);
                using var dot = new SolidBrush(Lighten(Theme.Secondary, 35));
                foreach (var p in points) g.FillEllipse(dot, p.X - 3.5f, p.Y - 3.5f, 7, 7);
                if (pinnedDate is { } pinned)
                {
                    var pinnedIndex = Array.FindIndex(days, day => day.Date.Date == pinned.Date);
                    if (pinnedIndex >= 0)
                    {
                        var point = points[pinnedIndex];
                        using var pinnedGlow = new Pen(Color.FromArgb(70, Theme.Tertiary), 8f);
                        using var pinnedRing = new Pen(Theme.Tertiary, 2f);
                        g.DrawEllipse(pinnedGlow, point.X - 8, point.Y - 8, 16, 16);
                        g.DrawEllipse(pinnedRing, point.X - 7, point.Y - 7, 14, 14);
                    }
                }
                if (hoveredDate is { } selected)
                {
                    var index = Array.FindIndex(days, day => day.Date.Date == selected.Date);
                    if (index >= 0)
                    {
                        var point = points[index];
                        using var guide = new Pen(Color.FromArgb(105, Theme.Tertiary), 1f);
                        g.DrawLine(guide, point.X, plot.Top, point.X, plot.Bottom);
                        using var halo = new SolidBrush(Color.FromArgb(65, Theme.Secondary));
                        using var center = new SolidBrush(Lighten(Theme.Secondary, 55));
                        g.FillEllipse(halo, point.X - 10, point.Y - 10, 20, 20);
                        g.FillEllipse(center, point.X - 5, point.Y - 5, 10, 10);

                        var modelTotals = (days[index].Hours ?? []).SelectMany(hour => hour.Models)
                            .GroupBy(model => model.Key)
                            .Select(group => (Name: group.Key, Tokens: group.Sum(model => model.Value)))
                            .OrderByDescending(model => model.Tokens).ToArray();
                        var effortTotals = (days[index].Efforts ?? new Dictionary<string, long> { ["Unknown"] = days[index].Tokens })
                            .Where(effort => effort.Value > 0)
                            .OrderBy(effort => effort.Key switch { "Light" => 0, "Medium" => 1, "High" => 2, "Unknown" => 4, _ => 3 })
                            .ToArray();
                        var maxModelRows = Math.Max(2, Math.Min(6, (r.Height - 86 - effortTotals.Length * 18) / 20));
                        var visibleModelCount = modelTotals.Length > maxModelRows ? maxModelRows - 1 : maxModelRows;
                        var visibleModels = modelTotals.Take(visibleModelCount).ToArray();
                        var hiddenModelCount = modelTotals.Length - visibleModels.Length;
                        const int tooltipWidth = 250;
                        var modelRows = visibleModels.Length + (hiddenModelCount > 0 ? 1 : 0);
                        var tooltipHeight = 76 + modelRows * 20 + effortTotals.Length * 18;
                        var tooltipX = Math.Clamp((int)point.X - tooltipWidth / 2, plot.Left, plot.Right - tooltipWidth);
                        var tooltipY = (int)point.Y - tooltipHeight - 12;
                        if (tooltipY < r.Top + 5) tooltipY = (int)point.Y + 14;
                        tooltipY = Math.Clamp(tooltipY, r.Top + 5, r.Bottom - tooltipHeight - 5);
                        var tooltip = new Rectangle(tooltipX, tooltipY, tooltipWidth, tooltipHeight);
                        FillRound(g, tooltip, 8, Darken(Panel, 4));
                        StrokeRound(g, tooltip, 8, Color.FromArgb(145, Theme.Tertiary));
                        using var tipDate = new Font("Segoe UI Semibold", 8.5f);
                        using var tipValue = new Font("Segoe UI Semibold", 10f);
                        g.DrawString(days[index].Date.ToString("dddd, MMM d"), tipDate,
                            new SolidBrush(TextMuted), tooltip.X + 10, tooltip.Y + 6);
                        g.DrawString($"{days[index].Tokens:N0} tokens", tipValue,
                            new SolidBrush(TextMain), tooltip.X + 10, tooltip.Y + 23);
                        using var modelFont = new Font("Segoe UI", 8.5f);
                        for (var modelIndex = 0; modelIndex < visibleModels.Length; modelIndex++)
                        {
                            var model = visibleModels[modelIndex];
                            var rowY = tooltip.Y + 48 + modelIndex * 20;
                            var color = Palette[modelIndex % Palette.Length];
                            using var modelDot = new SolidBrush(color);
                            g.FillEllipse(modelDot, tooltip.X + 11, rowY + 4, 7, 7);
                            var valueText = Compact(model.Tokens);
                            var valueSize = g.MeasureString(valueText, modelFont);
                            var nameRect = new Rectangle(tooltip.X + 25, rowY,
                                Math.Max(35, tooltip.Width - 47 - (int)valueSize.Width), 16);
                            TextRenderer.DrawText(g, model.Name, modelFont, nameRect, TextMuted,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                TextFormatFlags.NoPadding);
                            g.DrawString(valueText, modelFont, new SolidBrush(TextMain),
                                tooltip.Right - valueSize.Width - 11, rowY);
                        }
                        if (hiddenModelCount > 0)
                            g.DrawString($"+ {hiddenModelCount} more model{(hiddenModelCount == 1 ? "" : "s")}", modelFont,
                                new SolidBrush(TextMuted), tooltip.X + 25, tooltip.Y + 48 + visibleModels.Length * 20);

                        var effortTop = tooltip.Y + 52 + modelRows * 20;
                        using var divider = new Pen(Color.FromArgb(45, TextMuted), 1f);
                        g.DrawLine(divider, tooltip.X + 10, effortTop - 5, tooltip.Right - 10, effortTop - 5);
                        g.DrawString("REASONING EFFORT", tipDate, new SolidBrush(TextMuted), tooltip.X + 10, effortTop);
                        for (var effortIndex = 0; effortIndex < effortTotals.Length; effortIndex++)
                        {
                            var effort = effortTotals[effortIndex];
                            var rowY = effortTop + 18 + effortIndex * 18;
                            var color = effort.Key switch
                            {
                                "Light" => Theme.Primary,
                                "Medium" => Theme.Secondary,
                                "High" => Theme.Tertiary,
                                _ => TextMuted
                            };
                            using var effortDot = new SolidBrush(color);
                            g.FillEllipse(effortDot, tooltip.X + 11, rowY + 4, 7, 7);
                            g.DrawString(effort.Key, modelFont, new SolidBrush(TextMuted), tooltip.X + 25, rowY);
                            var valueText = Compact(effort.Value);
                            var valueSize = g.MeasureString(valueText, modelFont);
                            g.DrawString(valueText, modelFont, new SolidBrush(TextMain),
                                tooltip.Right - valueSize.Width - 11, rowY);
                        }
                    }
                }
            }
            foreach (var i in new[] { 0, 7, 14, 21, 29 })
            {
                if (i >= days.Length) continue;
                var text = days[i].Date.ToString("MMM d");
                var size = g.MeasureString(text, caption);
                var x = plot.Left + i * plot.Width / Math.Max(1f, days.Length - 1) - size.Width / 2;
                g.DrawString(text, caption, new SolidBrush(TextMuted), x, plot.Bottom + 10);
            }
        }

        private static long NiceCeiling(long value)
        {
            var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            var scaled = value / power;
            var nice = scaled <= 1 ? 1 :
                scaled <= 1.25 ? 1.25 :
                scaled <= 1.5 ? 1.5 :
                scaled <= 2 ? 2 :
                scaled <= 2.5 ? 2.5 :
                scaled <= 3 ? 3 :
                scaled <= 4 ? 4 :
                scaled <= 5 ? 5 :
                scaled <= 7.5 ? 7.5 : 10;
            return (long)Math.Ceiling(nice * power);
        }

        private static string Compact(long value) => value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
            >= 1_000 => $"{value / 1_000d:0.#}K",
            _ => value.ToString("N0")
        };

        private static string HourRange(int hour)
        {
            var start = DateTime.Today.AddHours(hour);
            return $"{start:h:mm tt} – {start.AddHours(1):h:mm tt}";
        }

        private static string ShortHourRange(int hour)
        {
            var start = DateTime.Today.AddHours(hour);
            return $"{start:h tt}–{start.AddHours(1):h tt}";
        }

        private static void FillRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(color); g.FillPath(brush, path);
        }

        private static void StrokeRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using var path = RoundedPath(r, radius);
            using var glow = new Pen(Color.FromArgb(Math.Max(8, color.A / 3), color), 5f);
            using var line = new Pen(color, 1f);
            g.DrawPath(glow, path);
            g.DrawPath(line, path);
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawAmbientLights(Graphics g)
        {
            var colors = new[] { Color.FromArgb(95, Theme.Primary), Color.FromArgb(90, Theme.Secondary), Color.FromArgb(75, Theme.Tertiary) };
            for (var i = 0; i < 42; i++)
            {
                var x = (i * 197 + 43) % Math.Max(1, g.VisibleClipBounds.Width);
                var y = (i * 83 + 29) % Math.Max(1, g.VisibleClipBounds.Height);
                var size = i % 9 == 0 ? 2.4f : 1.2f;
                using var brush = new SolidBrush(colors[i % colors.Length]);
                g.FillEllipse(brush, x, y, size, size);
            }
        }

        private static Color Lighten(Color color, int amount) => Color.FromArgb(color.A,
            Math.Min(255, color.R + amount), Math.Min(255, color.G + amount), Math.Min(255, color.B + amount));

        private static Color Darken(Color color, int amount) => Color.FromArgb(color.A,
            Math.Max(0, color.R - amount), Math.Max(0, color.G - amount), Math.Max(0, color.B - amount));

        private static Color Saturate(Color color, float amount)
        {
            var average = (color.R + color.G + color.B) / 3f;
            return Color.FromArgb(color.A,
                Math.Clamp((int)(average + (color.R - average) * amount), 0, 255),
                Math.Clamp((int)(average + (color.G - average) * amount), 0, 255),
                Math.Clamp((int)(average + (color.B - average) * amount), 0, 255));
        }

        private static void DrawSoftBarGlow(Graphics g, RectangleF bar, Color color)
        {
            // Several translucent one-pixel steps read as a soft halo at normal DPI,
            // without softening the bright bar drawn over the top.
            for (var spread = 10; spread >= 1; spread--)
            {
                var proximity = 11 - spread;
                var alpha = 2 + proximity * 2;
                using var brush = new SolidBrush(Color.FromArgb(alpha, color));
                g.FillRectangle(brush, bar.X - spread, bar.Y - spread,
                    bar.Width + spread * 2, bar.Height + spread * 2);
            }
        }

        private static void DrawCentered(Graphics g, string text, Rectangle? rect = null)
        {
            using var font = new Font("Segoe UI", 11f);
            var bounds = rect ?? new Rectangle(0, 0, (int)g.VisibleClipBounds.Width, (int)g.VisibleClipBounds.Height);
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, new SolidBrush(TextMuted), bounds.Left + (bounds.Width - size.Width) / 2,
                bounds.Top + (bounds.Height - size.Height) / 2);
        }
    }
}
