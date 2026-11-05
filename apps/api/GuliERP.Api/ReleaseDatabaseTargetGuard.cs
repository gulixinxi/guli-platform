using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GuliERP.Api;

/// <summary>
/// Production startup gate. It must run before WebApplication.Run starts hosted workers.
/// No credentials or connection string are included in failure messages.
/// </summary>
public static class ReleaseDatabaseTargetGuard
{
    private static readonly Regex NonProductionName = new(
        @"(^|_)(test|dev|poc|rehearsal|restore|backup|g2)(_|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ReleaseDatabaseTarget Validate(string connectionString, IConfiguration targetConfig)
    {
        ArgumentNullException.ThrowIfNull(targetConfig);

        var expectedHost = Required(targetConfig, "Host");
        var expectedDatabase = Required(targetConfig, "Database");
        var expectedUsername = Required(targetConfig, "Username");
        var expectedServerAddress = Required(targetConfig, "ServerAddress");
        if (!int.TryParse(Required(targetConfig, "Port"), out var expectedPort) || expectedPort is < 1 or > 65535)
            throw new InvalidOperationException("Release database target port is invalid.");
        if (NonProductionName.IsMatch(expectedDatabase))
            throw new InvalidOperationException("Release database target is a reserved non-production database.");

        NpgsqlConnectionStringBuilder actual;
        try
        {
            actual = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Release database connection string cannot be parsed.");
        }

        if (!string.Equals(actual.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            actual.Port != expectedPort ||
            !string.Equals(actual.Database, expectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(actual.Username, expectedUsername, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Release database connection target differs from the approved target.");
        }

        return new ReleaseDatabaseTarget(expectedDatabase, expectedUsername, expectedServerAddress, expectedPort);
    }

    public static async Task VerifyAsync(string connectionString, IConfiguration targetConfig)
    {
        var expected = Validate(connectionString, targetConfig);
        bool matches;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "SELECT current_database(), host(inet_server_addr()), inet_server_port(), current_user",
                connection) { CommandTimeout = 5 };
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            matches = await reader.ReadAsync(timeout.Token) &&
                string.Equals(reader.GetString(0), expected.Database, StringComparison.Ordinal) &&
                !reader.IsDBNull(1) &&
                string.Equals(reader.GetString(1), expected.ServerAddress, StringComparison.Ordinal) &&
                !reader.IsDBNull(2) && reader.GetInt32(2) == expected.Port &&
                string.Equals(reader.GetString(3), expected.Username, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Release database server identity could not be verified.");
        }
        if (!matches)
            throw new InvalidOperationException("Release database server identity differs from the approved target.");
    }

    private static string Required(IConfiguration config, string name)
    {
        var value = config[name]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Release database target {name} is missing.");
        return value;
    }
}

public sealed record ReleaseDatabaseTarget(
    string Database, string Username, string ServerAddress, int Port);
