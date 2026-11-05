using System.Net;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class QuakeSearchServiceConfigurationFacts
{
    [Fact]
    public async Task TransportFailure_DoesNotWriteExceptionTextToLog()
    {
        const string sensitiveMarker = "synthetic-secret-in-transport-error";
        using var client = new HttpClient(new ThrowingHandler(sensitiveMarker));
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(100, 200, "synthetic-key");
        var logger = new RecordingLogger();
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200), logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("example"));

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(sensitiveMarker, entry.Text);
            Assert.Null(entry.Exception);
        });
    }

    [Fact]
    public async Task MissingKey_DoesNotCallQuake()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var service = new QuakeSearchService(client,
            new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build()),
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ConfiguredKey_IsSentInQuakeHeader()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quake:ApiKey"] = "test-token",
                ["Quake:TenantId"] = "100", ["Quake:CompanyId"] = "200",
                ["Quake:Host"] = "localhost", ["Quake:Port"] = "5432", ["Quake:Database"] = "quake_scope_test",
                ["ConnectionStrings:GuliERP"] = "Host=localhost;Port=5432;Database=quake_scope_test;Username=test" }).Build();
        var service = new QuakeSearchService(client, new QuakeRuntimeCredentialStore(config),
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);

        var results = await service.SearchAsync("example");

        Assert.Empty(results);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("test-token", handler.Token);
    }

    [Fact]
    public async Task TenantCompanyScopedEnvironmentKey_DoesNotCrossDatabaseBoundary()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quake:ApiKey"] = "synthetic-old-test-key",
                ["Quake:TenantId"] = "100", ["Quake:CompanyId"] = "200",
                ["Quake:Host"] = "localhost", ["Quake:Port"] = "5432", ["Quake:Database"] = "old_test_db",
                ["ConnectionStrings:GuliERP"] = "Host=localhost;Port=5432;Database=new_formal_db;Username=test" }).Build();
        var credentials = new QuakeRuntimeCredentialStore(config);
        Assert.Equal((false, "none"), credentials.GetStatus(100, 200));
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RuntimeKey_IsScopedToCompany_AndNeverReturnedByStatus()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(100, 200, "runtime-test-token");
        var tenant = new MutableCurrentTenant(100);
        var company = new MutableCurrentCompany(200);
        var service = new QuakeSearchService(client, credentials, tenant, company,
            NullLogger<QuakeSearchService>.Instance);

        Assert.Equal((true, "runtime"), credentials.GetStatus(100, 200));
        Assert.Equal((false, "none"), credentials.GetStatus(100, 201));
        await service.SearchAsync("example");
        Assert.Equal("runtime-test-token", handler.Token);

        company.Set(201);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UnboundEnvironmentKey_CannotSendAQuakeRequest()
    {
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Quake:ApiKey"] = "legacy-global-key" }).Build();
        var credentials = new QuakeRuntimeCredentialStore(config);
        Assert.Equal((false, "none"), credentials.GetStatus(100, 200));
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectedKey_FallsBackToNextKey_WithoutExposingValues()
    {
        var handler = new StubHandler { RejectFirst = true };
        using var client = new HttpClient(handler);
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(100, 200, "first-token");
        credentials.SetKey(100, 200, "backup-token");
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);

        await service.SearchAsync("example");

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("backup-token", handler.Token);
        Assert.Equal(2, credentials.GetRuntimeCount(100, 200));
        Assert.DoesNotContain("token", System.Text.Json.JsonSerializer.Serialize(credentials.GetPublicEntries(100, 200)));
    }

    [Fact]
    public async Task ServerFailure_DoesNotSwitchKey()
    {
        var handler = new StubHandler { ServerFailure = true };
        using var client = new HttpClient(handler);
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(100, 200, "first-token");
        credentials.SetKey(100, 200, "backup-token");
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Redirect_DoesNotSwitchToBackupKey(HttpStatusCode redirectStatus)
    {
        var handler = new StubHandler { RedirectStatus = redirectStatus };
        using var client = new HttpClient(handler);
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(100, 200, "first-token");
        credentials.SetKey(100, 200, "backup-token");
        var service = new QuakeSearchService(client, credentials,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200),
            NullLogger<QuakeSearchService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("example"));
        Assert.Contains($"HTTP {(int)redirectStatus}", error.Message);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("first-token", handler.Token);
    }

    [Fact]
    public void PersistedKeys_ReloadInOrderAndRemainCompanyScoped()
    {
        var persistence = new FakePersistence();
        var config = new ConfigurationBuilder().Build();
        var firstProcess = new QuakeRuntimeCredentialStore(config, persistence);
        var primary = firstProcess.SetKey(100, 200, "primary-secret");
        var backup = firstProcess.SetKey(100, 200, "backup-secret");

        var afterRestart = new QuakeRuntimeCredentialStore(config, persistence);
        Assert.Equal((true, "windows_credential"), afterRestart.GetStatus(100, 200));
        Assert.Equal(new[] { "primary-secret", "backup-secret" },
            afterRestart.GetCandidates(100, 200).Select(k => k.Value));
        Assert.Equal((false, "none"), afterRestart.GetStatus(100, 201));
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(afterRestart.GetPublicEntries(100, 200)));

        Assert.True(afterRestart.RemoveKey(100, 200, primary));
        var replacement = afterRestart.SetKey(100, 200, "replacement-secret");
        var nextProcess = new QuakeRuntimeCredentialStore(config, persistence);
        Assert.Equal(new[] { "backup-secret", "replacement-secret" },
            nextProcess.GetCandidates(100, 200).Select(k => k.Value));
        Assert.True(nextProcess.RemoveKey(100, 200, backup));
        Assert.True(nextProcess.RemoveKey(100, 200, replacement));
        Assert.Equal((false, "none"), new QuakeRuntimeCredentialStore(config, persistence).GetStatus(100, 200));
    }

    [Fact]
    public void WindowsCredentialManager_ReloadsSyntheticKeyAndDeletesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:GuliERP"] = $"Host=127.0.0.1;Database=quake_scope_{Guid.NewGuid():N};Username=test"
        }).Build();
        var persistence = new WindowsQuakeCredentialPersistence(config);
        var tenant = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var company = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var secret = "synthetic-quake-" + Guid.NewGuid().ToString("N");
        try
        {
            new QuakeRuntimeCredentialStore(config, persistence).SetKey(tenant, company, secret);
            var restarted = new QuakeRuntimeCredentialStore(config, persistence);
            Assert.Equal(secret, restarted.GetCandidates(tenant, company).Single().Value);
            Assert.Equal((true, "windows_credential"), restarted.GetStatus(tenant, company));
            var otherConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GuliERP"] = $"Host=127.0.0.1;Database=other_scope_{Guid.NewGuid():N};Username=test"
            }).Build();
            Assert.Empty(new QuakeRuntimeCredentialStore(otherConfig,
                new WindowsQuakeCredentialPersistence(otherConfig)).GetCandidates(tenant, company));
            var id = restarted.GetCandidates(tenant, company).Single().Id;
            Assert.True(restarted.RemoveKey(tenant, company, id));
            Assert.Empty(new QuakeRuntimeCredentialStore(config, persistence).GetCandidates(tenant, company));
        }
        finally { persistence.Delete(tenant, company, 1); }
    }

    private sealed class FakePersistence : IQuakeCredentialPersistence
    {
        private readonly Dictionary<(long Tenant, long Company, int Slot), QuakeStoredCredential> _items = [];
        public IReadOnlyList<QuakeStoredCredential> Load(long tenantId, long companyId) => _items
            .Where(x => x.Key.Tenant == tenantId && x.Key.Company == companyId)
            .Select(x => x.Value).OrderBy(x => x.Sequence).ToArray();
        public void Write(long tenantId, long companyId, QuakeStoredCredential credential) =>
            _items[(tenantId, companyId, credential.Slot)] = credential;
        public void Delete(long tenantId, long companyId, int slot) => _items.Remove((tenantId, companyId, slot));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Token { get; private set; }
        public bool RejectFirst { get; init; }
        public bool ServerFailure { get; init; }
        public HttpStatusCode? RedirectStatus { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Token = request.Headers.GetValues("X-QuakeToken").Single();
            var status = RedirectStatus ?? (ServerFailure ? HttpStatusCode.InternalServerError
                : RejectFirst && CallCount == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"code\":0,\"data\":[]}")
            });
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(message));
    }

    private sealed class RecordingLogger : ILogger<QuakeSearchService>
    {
        public List<(string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
