using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GuliERP.Crm.Infrastructure.Persistence;

public sealed class DesignTimeCrmDbContextFactory : IDesignTimeDbContextFactory<CrmDbContext>
{
    public CrmDbContext CreateDbContext(string[] args)
    {
        // Design-time scaffolding must never inherit the API host's generic
        // ConnectionStrings__GuliERP value. On this workstation it points to
        // a different test database. A local placeholder is sufficient to
        // build the model and generate SQL without opening a connection.
        // Set this dedicated variable only for an explicitly selected target.
        var connectionString = Environment.GetEnvironmentVariable("GULIERP_CRM_DESIGNTIME_CONNECTION")
            ?? "Host=127.0.0.1;Database=gulierp_design_time_placeholder;Username=design;Password=placeholder";
        var options = new DbContextOptionsBuilder<CrmDbContext>()
            .UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable(
                "__ef_migrations_history", CrmDbContext.DefaultSchema))
            .Options;
        return new CrmDbContext(options);
    }
}
