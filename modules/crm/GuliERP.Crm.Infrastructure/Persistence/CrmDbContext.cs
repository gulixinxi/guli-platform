using GuliERP.Crm.Domain.Entities;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Persistence;

public class CrmDbContext : DbContext
{
    public const string DefaultSchema = "crm";

    private readonly ICurrentTenant? _currentTenant;
    private readonly ICurrentCompany? _currentCompany;

    /// <summary>
    /// DI constructor with tenant/company context for automatic scope filling.
    /// </summary>
    public CrmDbContext(
        DbContextOptions<CrmDbContext> options,
        ICurrentTenant? currentTenant = null,
        ICurrentCompany? currentCompany = null) : base(options)
    {
        _currentTenant = currentTenant;
        _currentCompany = currentCompany;
    }

    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ImportExportLog> ImportExportLogs => Set<ImportExportLog>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<QuakeQueryHistory> QuakeQueryHistories => Set<QuakeQueryHistory>();
    public DbSet<QuakeAssetRecord> QuakeAssetRecords => Set<QuakeAssetRecord>();
    public DbSet<IdentityCandidate> IdentityCandidates => Set<IdentityCandidate>();
    public DbSet<IdentityEvidence> IdentityEvidence => Set<IdentityEvidence>();
    public DbSet<IdentityReviewEvent> IdentityReviewEvents => Set<IdentityReviewEvent>();
    public DbSet<WebsiteDiagnosticReport> WebsiteDiagnosticReports => Set<WebsiteDiagnosticReport>();
    public DbSet<WebsiteContentEvidence> WebsiteContentEvidence => Set<WebsiteContentEvidence>();
    public DbSet<AiPreparationDraft> AiPreparationDrafts => Set<AiPreparationDraft>();
    public DbSet<WebsiteRecheckSchedule> WebsiteRecheckSchedules => Set<WebsiteRecheckSchedule>();
    public DbSet<WebsiteRecheckChange> WebsiteRecheckChanges => Set<WebsiteRecheckChange>();
    public DbSet<ContactCandidate> ContactCandidates => Set<ContactCandidate>();
    public DbSet<ContactCandidateEvent> ContactCandidateEvents => Set<ContactCandidateEvent>();
    public DbSet<DataSourceAuthorization> DataSourceAuthorizations => Set<DataSourceAuthorization>();
    public DbSet<DataSourceAuthorizationRevision> DataSourceAuthorizationRevisions => Set<DataSourceAuthorizationRevision>();
    public DbSet<DataSourceExecutionAttempt> DataSourceExecutionAttempts => Set<DataSourceExecutionAttempt>();
    public DbSet<LegacyRequestObservation> LegacyRequestObservations => Set<LegacyRequestObservation>();
    public DbSet<ReverseLookupRecord> ReverseLookupRecords => Set<ReverseLookupRecord>();
    public DbSet<LeadDemandAssessment> LeadDemandAssessments => Set<LeadDemandAssessment>();
    public DbSet<LeadDemandAssessmentRevision> LeadDemandAssessmentRevisions => Set<LeadDemandAssessmentRevision>();
    public DbSet<LeadAssessmentEvidence> LeadAssessmentEvidence => Set<LeadAssessmentEvidence>();

    /// <summary>
    /// Current tenant ID for global query filters. Returns 0 when no
    /// tenant context is available (e.g. during design-time migrations).
    /// </summary>
    private long CurrentTenantId => _currentTenant?.Id ?? 0;
    private long CurrentCompanyId => _currentCompany?.Id ?? 0;

    /// <summary>
    /// Automatically fills TenantId/CompanyId for newly added ICompanyScoped
    /// entities from the current request context. Throws when context is
    /// unavailable and the entity has no explicit scope set.
    /// </summary>
    private void ApplyTenantCompanyScope()
    {
        foreach (var entry in ChangeTracker.Entries<ICompanyScoped>())
        {
            if (entry.State != EntityState.Added)
                continue;

            var entity = entry.Entity;

            // TenantId: reject explicit value that differs from current context
            // to prevent cross-tenant scope injection. Fill from context if 0.
            if (entity.TenantId == 0)
            {
                if (_currentTenant is { IsAvailable: true })
                {
                    entry.Property("TenantId").CurrentValue = _currentTenant.Id!.Value;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot create {entity.GetType().Name} without a tenant scope. " +
                        "ICurrentTenant is unavailable and TenantId was not explicitly set.");
                }
            }
            else if (_currentTenant is { IsAvailable: true } && entity.TenantId != _currentTenant.Id!.Value)
            {
                throw new InvalidOperationException(
                    $"Cannot create {entity.GetType().Name} with TenantId={entity.TenantId} " +
                    $"because the current tenant scope is {_currentTenant.Id.Value}. " +
                    "Cross-tenant entity creation is not allowed.");
            }

            // CompanyId: reject explicit value that differs from current context.
            if (entity.CompanyId == 0)
            {
                if (_currentCompany is { IsAvailable: true })
                {
                    entry.Property("CompanyId").CurrentValue = _currentCompany.Id!.Value;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot create {entity.GetType().Name} without a company scope. " +
                        "ICurrentCompany is unavailable and CompanyId was not explicitly set.");
                }
            }
            else if (_currentCompany is { IsAvailable: true } && entity.CompanyId != _currentCompany.Id!.Value)
            {
                throw new InvalidOperationException(
                    $"Cannot create {entity.GetType().Name} with CompanyId={entity.CompanyId} " +
                    $"because the current company scope is {_currentCompany.Id.Value}. " +
                    "Cross-company entity creation is not allowed.");
            }
        }
    }

    public override int SaveChanges()
    {
        ApplyTenantCompanyScope();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantCompanyScope();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTenantCompanyScope();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyTenantCompanyScope();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>("company_no_seq", DefaultSchema);
        modelBuilder.HasSequence<long>("deal_no_seq", DefaultSchema);
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(DefaultSchema);

        // ===== 线索池表 =====
        modelBuilder.Entity<Lead>(entity =>
        {
            entity.ToTable("leads");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.WebsiteDomain);
            entity.HasIndex(e => e.Mobile);
            entity.HasIndex(e => e.Email);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.ExternalKey);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.SourceIdentityCandidateId })
                .IsUnique().HasFilter("\"SourceIdentityCandidateId\" IS NOT NULL");
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.SourceIdentityCandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.ConvertedToCompanyId);

            entity.Property(e => e.CompanyName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.WebsiteDomain).HasMaxLength(200);
            entity.Property(e => e.ContactName).HasMaxLength(100);
            entity.Property(e => e.ContactPosition).HasMaxLength(100);
            entity.Property(e => e.Mobile).HasMaxLength(50);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Wechat).HasMaxLength(100);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.District).HasMaxLength(50);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.SourceUrl).HasMaxLength(500);
            entity.Property(e => e.ExternalKey).HasMaxLength(100);
            entity.Property(e => e.ExternalSource).HasMaxLength(50);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
            entity.Property(e => e.Tags).HasColumnType("text[]");
        });

        // ===== 企业/客户主档表 =====
        modelBuilder.Entity<Company>(entity =>
        {
            entity.ToTable("companies");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.CompanyNo).IsUnique();
            entity.HasIndex(e => e.CreditCode);
            entity.HasIndex(e => e.WebsiteDomain);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.ExternalKey);

            entity.Property(e => e.CompanyNo).HasMaxLength(50);
            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.CreditCode).HasMaxLength(18);
            entity.Property(e => e.ShortName).HasMaxLength(100);
            entity.Property(e => e.WebsiteDomain).HasMaxLength(200);
            entity.Property(e => e.District).HasMaxLength(50);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.SourceUrl).HasMaxLength(500);
            entity.Property(e => e.ExternalKey).HasMaxLength(100);
            entity.Property(e => e.ExternalSource).HasMaxLength(50);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
            entity.Property(e => e.Tags).HasColumnType("text[]");

            entity.HasMany(e => e.Contacts)
                .WithOne(c => c.CrmCompany)
                .HasForeignKey(c => c.CrmCompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Deals)
                .WithOne(d => d.CrmCompany)
                .HasForeignKey(d => d.CrmCompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Activities)
                .WithOne(a => a.CrmCompany)
                .HasForeignKey(a => a.CrmCompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ===== 联系人表 =====
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.ToTable("contacts");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.CrmCompanyId);
            entity.HasIndex(e => e.Mobile);

            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Position).HasMaxLength(100);
            entity.Property(e => e.Mobile).HasMaxLength(50);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Wechat).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
        });

        // ===== 商机表 =====
        modelBuilder.Entity<Deal>(entity =>
        {
            entity.ToTable("deals");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.DealNo).IsUnique();
            entity.HasIndex(e => e.CrmCompanyId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.Stage);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.SourceLeadDemandAssessmentId).IsUnique();

            entity.Property(e => e.DealNo).HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ExpectedAmount).HasPrecision(18, 2);
            entity.Property(e => e.ExpectedCloseMonth).HasMaxLength(7);
            entity.Property(e => e.Remarks).HasMaxLength(2000);

            entity.HasMany(e => e.Activities)
                .WithOne(a => a.Deal)
                .HasForeignKey(a => a.DealId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ===== 跟进记录表 =====
        modelBuilder.Entity<Activity>(entity =>
        {
            entity.ToTable("activities");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.CrmCompanyId);
            entity.HasIndex(e => e.DealId);
            entity.HasIndex(e => e.NextActionDate);
            entity.HasIndex(e => e.OwnerId);

            entity.Property(e => e.Content).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.NextAction).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Attachments).HasMaxLength(1000);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
        });

        // ===== 导入导出留痕表 =====
        modelBuilder.Entity<ImportExportLog>(entity =>
        {
            entity.ToTable("import_export_logs");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.OperationType);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.OperatedAt);

            entity.Property(e => e.Scope).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SourceFile).HasMaxLength(500);
            entity.Property(e => e.FailureDetails).HasMaxLength(4000);
            entity.Property(e => e.Remarks).HasMaxLength(2000);
        });

        // ===== 企业情报信号表 =====
        modelBuilder.Entity<Signal>(entity =>
        {
            entity.ToTable("signals");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.LeadId);
            entity.HasIndex(e => e.CompanyRefId);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CollectedAt);

            entity.Property(e => e.CompanyName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Detail).HasMaxLength(4000);
            entity.Property(e => e.Source).HasMaxLength(50).IsRequired();
            entity.Property(e => e.SourceUrl).HasMaxLength(500);
            entity.Property(e => e.HandleNote).HasMaxLength(2000);
            entity.Property(e => e.EvidenceType).HasMaxLength(50);
            entity.Property(e => e.QueryParams).HasMaxLength(1000);
            entity.Property(e => e.RawContent).HasMaxLength(8000);
        });

        // ===== Quake 查询历史表 =====
        modelBuilder.Entity<QuakeQueryHistory>(entity =>
        {
            entity.ToTable("quake_query_history");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.QueryHash, e.QueriedAt });
            entity.HasIndex(e => e.QueryHash);
            entity.HasIndex(e => e.QueriedAt);

            entity.Property(e => e.QueryText).HasMaxLength(500).IsRequired();
            entity.Property(e => e.QueryHash).HasMaxLength(64).IsRequired();
        });

        // ===== Quake 资产表（15 字段 + 清洗判定）=====
        modelBuilder.Entity<QuakeAssetRecord>(entity =>
        {
            entity.ToTable("quake_asset_records");
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.TenantId, e.CompanyId, e.Id });
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            // One asset per domain+port per company; re-observing updates in place.
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Domain, e.Port }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CleanVerdict });
            entity.HasIndex(e => e.BatchId);
            entity.HasIndex(e => e.ObservedAt);

            entity.Property(e => e.RawJson).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<IdentityCandidate>(entity =>
        {
            entity.ToTable("identity_candidates");
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.TenantId, e.CompanyId, e.Id });
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<QuakeAssetRecord>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.AssetId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.AssetId, e.Status });
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.Domain });
            entity.Property(e => e.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<IdentityEvidence>(entity =>
        {
            entity.ToTable("identity_evidence");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.RecordedAt });
        });

        modelBuilder.Entity<IdentityReviewEvent>(entity =>
        {
            entity.ToTable("identity_review_events");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.Version }).IsUnique();
        });

        modelBuilder.Entity<WebsiteDiagnosticReport>(entity =>
        {
            entity.ToTable("website_diagnostic_reports");
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.Id });
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.Version }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.RequestedAt });
            entity.Property(e => e.ChecksJson).HasColumnType("jsonb");
            entity.Property(e => e.AttemptLogJson).HasColumnType("jsonb");
            entity.Property(e => e.Trigger).HasDefaultValue("manual");
        });

        modelBuilder.Entity<WebsiteContentEvidence>(entity =>
        {
            entity.ToTable("website_content_evidence");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WebsiteDiagnosticReport>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.ReportId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.RecordedAt });
            entity.Property(e => e.Status).IsConcurrencyToken();
        });

        modelBuilder.Entity<AiPreparationDraft>(entity =>
        {
            entity.ToTable("ai_preparation_drafts", table =>
            {
                table.HasCheckConstraint("CK_ai_preparation_status",
                    "\"Status\" IN ('draft','unsupported','failed','cost_limit','approved','rejected')");
                table.HasCheckConstraint("CK_ai_preparation_cost",
                    "\"Version\" > 0 AND \"EstimatedCostCents\" >= 0 AND \"MaxCostCents\" >= 0 AND \"ActualCostCents\" >= 0");
            });
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.CandidateId, e.TaskType, e.Version }).IsUnique();
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.RequestKey }).IsUnique();
            entity.Property(e => e.InputEvidenceJson).HasColumnType("jsonb");
            entity.Property(e => e.ClaimsJson).HasColumnType("jsonb");
            entity.Property(e => e.Status).IsConcurrencyToken();
        });

        modelBuilder.Entity<WebsiteRecheckSchedule>(entity =>
        {
            entity.ToTable("website_recheck_schedules");
            entity.HasKey(e => e.CandidateId);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithOne()
                .HasForeignKey<WebsiteRecheckSchedule>(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey<IdentityCandidate>(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.Enabled, e.NextDueAt });
            entity.Property(e => e.IntervalDays).HasDefaultValue(7);
        });

        modelBuilder.Entity<WebsiteRecheckChange>(entity =>
        {
            entity.ToTable("website_recheck_changes");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.CandidateId, e.ChangedAt });
            entity.Property(e => e.IntervalDays).HasDefaultValue(7);
        });

        modelBuilder.Entity<ContactCandidate>(entity =>
        {
            entity.ToTable("contact_candidates");
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.TenantId, e.CompanyId, e.Id });
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<IdentityCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.IdentityCandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.IdentityCandidateId, e.Status });
            entity.Property(e => e.FieldSourcesJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ContactCandidateEvent>(entity =>
        {
            entity.ToTable("contact_candidate_events");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<ContactCandidate>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.CandidateId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.CandidateId, e.OccurredAt });
        });

        modelBuilder.Entity<DataSourceAuthorization>(entity =>
        {
            entity.ToTable("data_source_authorizations");
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.TenantId, e.CompanyId, e.Id });
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.SourceCode }).IsUnique();
            entity.Property(e => e.SourceCode).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.AuthorizationReference).HasMaxLength(500).IsRequired();
            entity.Property(e => e.PermittedFields).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.CostNote).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<DataSourceAuthorizationRevision>(entity =>
        {
            entity.ToTable("data_source_authorization_revisions");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<DataSourceAuthorization>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.AuthorizationId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.AuthorizationId, e.Version }).IsUnique();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.AuthorizationReference).HasMaxLength(500).IsRequired();
            entity.Property(e => e.PermittedFields).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.CostNote).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<DataSourceExecutionAttempt>(entity =>
        {
            entity.ToTable("data_source_execution_attempts");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasOne<DataSourceAuthorization>().WithMany()
                .HasForeignKey(e => new { e.TenantId, e.CompanyId, e.AuthorizationId })
                .HasPrincipalKey(e => new { e.TenantId, e.CompanyId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.AuthorizationId, e.ReservedAt });
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.RequestId, e.AttemptNumber }).IsUnique();
            entity.Property(e => e.SourceCode).HasMaxLength(40).IsRequired();
            entity.Property(e => e.RequestedFields).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(30).IsRequired();
            entity.Property(e => e.FailureCode).HasMaxLength(60);
        });

        modelBuilder.Entity<LegacyRequestObservation>(entity =>
        {
            entity.ToTable("legacy_request_observations");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.SourceCode, e.ObservedAt });
            entity.Property(e => e.SourceCode).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Route).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Outcome).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<ReverseLookupRecord>(entity =>
        {
            entity.ToTable("reverse_lookup_records");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.QueriedAt });
            entity.HasIndex(e => e.BatchId);
            entity.Property(e => e.CompanyName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.InputWebsite).HasMaxLength(500);
            entity.Property(e => e.ResultJson).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<LeadDemandAssessment>(entity =>
        {
            entity.ToTable("lead_demand_assessments");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.LeadId, e.Category }).IsUnique();
            entity.Property(e => e.Category).HasConversion<int>().IsRequired();
            entity.Property(e => e.State).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Confidence).HasMaxLength(20);
            entity.Property(e => e.Evidence).HasMaxLength(2000);
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.NextAction).HasMaxLength(500);
        });

        modelBuilder.Entity<LeadDemandAssessmentRevision>(entity =>
        {
            entity.ToTable("lead_demand_assessment_revisions");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.LeadId, e.Category, e.Version }).IsUnique();
            entity.Property(e => e.Category).HasConversion<int>().IsRequired();
            entity.Property(e => e.State).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Confidence).HasMaxLength(20);
            entity.Property(e => e.Evidence).HasMaxLength(2000);
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.NextAction).HasMaxLength(500);
        });

        // ===== 需求评估证据 =====
        modelBuilder.Entity<LeadAssessmentEvidence>(entity =>
        {
            entity.ToTable("lead_assessment_evidence");
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(e => CurrentTenantId > 0 && CurrentCompanyId > 0
                && e.TenantId == CurrentTenantId && e.CompanyId == CurrentCompanyId);

            // One assessment can carry many evidence rows (e.g. 官网 + ICP +
            // GEO), so this index is NOT unique. It serves the "show all
            // evidence for this lead / this direction" read path.
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.LeadId, e.Category });
            // Serves the evidence of the currently saved assessment version.
            // Prior evidence rows are replaced on resave by B1 design.
            entity.HasIndex(e => new { e.TenantId, e.CompanyId, e.AssessmentId, e.Version });

            entity.Property(e => e.Category).HasConversion<int>().IsRequired();
            entity.Property(e => e.EvidenceType).HasMaxLength(40).IsRequired();
            entity.Property(e => e.VerificationStatus).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(1000);
            entity.Property(e => e.Note).HasMaxLength(2000);
        });
    }
}
