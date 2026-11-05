using System.Text.Json;

namespace GuliERP.Api.Crm;

/// <summary>Reads a local, non-secret status file written by the backup monitor.</summary>
public static class BackupMonitorStatus
{
    private const string DefaultPath = @"C:\ProgramData\GuliERP\BackupMonitor\status.json";
    private const int MaximumBytes = 8192;
    // The monitor is scheduled hourly. A stopped monitor must become visible
    // before the next daily backup is due, even if the last backup was healthy.
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(3);

    public static object Read(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        var configured = configuration["Crm:BackupMonitor:StatusPath"];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;
        return ReadFile(path, (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    public static object ReadFile(string path, DateTimeOffset now)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumBytes)
                return Unknown();

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                return Unknown();
            var overall = root.GetProperty("overall").GetString();
            if (overall is not ("PASS" or "FAIL"))
                return Unknown();
            if (!DateTimeOffset.TryParse(root.GetProperty("checkedAtUtc").GetString(), out var checkedAt) ||
                checkedAt > now.AddMinutes(5) || now - checkedAt > MaximumAge)
                return Unknown();

            var checks = root.GetProperty("checks").EnumerateArray()
                .Select(item => new
                {
                    cadence = item.GetProperty("cadence").GetString(),
                    status = item.GetProperty("status").GetString(),
                    reason = item.GetProperty("reason").GetString()
                }).ToArray();
            if (checks.Length is < 2 or > 3 ||
                checks.Count(item => item.cadence == "daily") != 1 ||
                checks.Count(item => item.cadence == "weekly") != 1 ||
                checks.Count(item => item.cadence == "all") > 1 ||
                checks.Any(item => item.cadence is not ("daily" or "weekly" or "all") ||
                    item.status is not ("PASS" or "FAIL") ||
                    item.reason is not ("none" or "TASK_STATE_OR_LAST_RESULT" or "ARCHIVE_TOO_OLD_OR_ABSENT" or
                        "ARCHIVE_IDENTITY_OR_HASH" or "CHECK_ERROR" or "PARTIAL_ARCHIVE_PRESENT") ||
                    (item.status == "PASS") != (item.reason == "none")) ||
                (overall == "PASS") != checks.All(item => item.status == "PASS"))
                return Unknown();

            return new { overall, checkedAtUtc = checkedAt, checks };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            return Unknown();
        }
    }

    private static object Unknown() => new { overall = "UNKNOWN", checkedAtUtc = (DateTimeOffset?)null,
        checks = Array.Empty<object>() };
}
