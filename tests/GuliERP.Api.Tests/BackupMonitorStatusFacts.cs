using System.Text.Json;
using GuliERP.Api.Crm;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class BackupMonitorStatusFacts
{
    [Fact]
    public void Fresh_healthy_status_is_returned_without_local_path()
    {
        var now = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
        var path = WriteStatus(now, "PASS", "PASS", "none");
        try
        {
            var result = JsonSerializer.Serialize(BackupMonitorStatus.ReadFile(path, now));
            Assert.Contains("\"overall\":\"PASS\"", result);
            Assert.DoesNotContain(path, result, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Old_or_inconsistent_status_fails_closed()
    {
        var now = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
        var old = WriteStatus(now.AddHours(-4), "PASS", "PASS", "none");
        var inconsistent = WriteStatus(now, "PASS", "FAIL", "CHECK_ERROR");
        try
        {
            Assert.Contains("\"UNKNOWN\"", JsonSerializer.Serialize(BackupMonitorStatus.ReadFile(old, now)));
            Assert.Contains("\"UNKNOWN\"", JsonSerializer.Serialize(BackupMonitorStatus.ReadFile(inconsistent, now)));
        }
        finally { File.Delete(old); File.Delete(inconsistent); }
    }

    private static string WriteStatus(DateTimeOffset checkedAt, string overall, string daily, string dailyReason)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gulierp-backup-status-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            checkedAtUtc = checkedAt.ToString("O"),
            overall,
            checks = new[]
            {
                new { cadence = "daily", status = daily, reason = dailyReason },
                new { cadence = "weekly", status = "PASS", reason = "none" }
            }
        }));
        return path;
    }
}
