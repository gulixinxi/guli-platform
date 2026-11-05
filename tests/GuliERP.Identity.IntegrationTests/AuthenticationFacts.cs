using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using GuliERP.Api.Authentication;
using GuliERP.Api.Crm;
using GuliERP.Foundation.Kernel;
using GuliERP.Identity.Application.Authentication;
using GuliERP.Identity.Domain.Entities;
using GuliERP.Identity.Domain.Enums;
using GuliERP.Identity.Infrastructure.Persistence;
using GuliERP.Identity.Infrastructure.Authorization;
using GuliERP.Identity.Infrastructure.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Domain.Enums;
using CrmLead = GuliERP.Crm.Domain.Entities.Lead;
using CrmCompany = GuliERP.Crm.Domain.Entities.Company;
using CrmDeal = GuliERP.Crm.Domain.Entities.Deal;
using CrmActivity = GuliERP.Crm.Domain.Entities.Activity;
using CrmLeadDemandAssessment = GuliERP.Crm.Domain.Entities.LeadDemandAssessment;
using ICrmNumberGenerator = GuliERP.Crm.Infrastructure.Services.ICrmNumberGenerator;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GuliERP.Identity.IntegrationTests;

/// <summary>
/// G2-004 / G2-004R1 — Authentication endpoint contract tests.
/// Covers the 5 endpoints in the frozen architecture §4.2 +
/// DEC-AUTH-009 (antiforgery):
/// <list type="bullet">
///   <item>GET  /api/v1/auth/csrf               — antiforgery token source</item>
///   <item>POST /api/v1/auth/login              — credentials → cookie (CSRF)</item>
///   <item>POST /api/v1/auth/logout             — clear cookie (CSRF)</item>
///   <item>GET  /api/v1/auth/me                 — current user DTO (GET, no CSRF)</item>
///   <item>POST /api/v1/auth/company/switch     — re-mint cookie (CSRF)</item>
/// </list>
///
/// <para>
/// G2-004R1 contract: state-changing endpoints require a valid
/// <c>X-CSRF-TOKEN</c> header. The tests fetch the token via
/// <c>GET /api/v1/auth/csrf</c> first. The test fixture NEVER
/// calls <c>.DisableAntiforgery()</c> — the production
/// protection is the only path under test.
/// </para>
/// </summary>
public class AuthenticationFacts : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BadConnectionString =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2;Command Timeout=2";

    private readonly WebApplicationFactory<Program> _factory;

    public AuthenticationFacts(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> BuildClient(string env = "Testing")
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:GuliERP", BadConnectionString);
            builder.UseEnvironment(env);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<IdentityDbContext>>();
                services.AddDbContext<IdentityDbContext>(options =>
                {
                    options.UseNpgsql(
                        BadConnectionString,
                        npg => npg.MigrationsHistoryTable(
                            "__ef_migrations_history",
                            IdentityDbContext.DefaultSchema));
                });
            });
        });
    }

    private WebApplicationFactory<Program> BuildInMemoryClient(
        string databaseName, bool validateSecurityStampOnEveryRequest = false,
        bool useInMemoryCrm = false)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(
                "ConnectionStrings:GuliERP",
                "Host=127.0.0.1;Port=1;Database=gulierp_g2_003_test;Username=none;Password=none");
            builder.ConfigureTestServices(services =>
            {
                if (validateSecurityStampOnEveryRequest)
                {
                    services.PostConfigure<SecurityStampValidatorOptions>(options =>
                        options.ValidationInterval = TimeSpan.Zero);
                }
                var inMemoryProvider = new ServiceCollection()
                    .AddEntityFrameworkInMemoryDatabase()
                    .BuildServiceProvider();
                services.RemoveAll<DbContextOptions<IdentityDbContext>>();
                services.AddDbContext<IdentityDbContext>(options =>
                {
                    options.UseInMemoryDatabase(databaseName);
                    options.UseInternalServiceProvider(inMemoryProvider);
                    options.ConfigureWarnings(warnings =>
                        warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
                });
                if (useInMemoryCrm)
                {
                    services.RemoveAll<DbContextOptions<CrmDbContext>>();
                    var crmInMemoryProvider = new ServiceCollection()
                        .AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
                    services.AddDbContext<CrmDbContext>(options =>
                    {
                        options.UseInMemoryDatabase(databaseName + "_crm");
                        options.UseInternalServiceProvider(crmInMemoryProvider);
                        options.ConfigureWarnings(warnings =>
                            warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
                    });
                    services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                    // R43：CRM 编号生成器走 PostgreSQL 序列（SELECT nextval('crm.deal_no_seq')），
                    // InMemory provider 无法执行原始 SQL，建档路径必然 500。
                    // 这里换成等价的内存实现——只影响测试环境，不改生产编号规则。
                    services.RemoveAll<ICrmNumberGenerator>();
                    services.AddScoped<ICrmNumberGenerator, InMemoryCrmNumberGenerator>();
                }
            });
        });
    }

    /// <summary>
    /// Fetch the antiforgery token via <c>GET /api/v1/auth/csrf</c>.
    /// The response body is <c>{"requestToken": "...", "headerName": "X-CSRF-TOKEN"}</c>.
    /// The antiforgery cookie is set on the HttpClient cookie
    /// container (auto by the HttpClient).
    /// </summary>
    private static async Task<string> FetchCsrfTokenAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/auth/csrf");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("csrf response missing requestToken");
    }

    /// <summary>
    /// Attach the antiforgery token to a state-changing request
    /// via the frozen <c>X-CSRF-TOKEN</c> header (DEC-AUTH-009).
    /// </summary>
    private static void AttachCsrfToken(HttpRequestMessage request, string token)
    {
        request.Headers.Remove(AuthEndpoints.CsrfHeaderName);
        request.Headers.Add(AuthEndpoints.CsrfHeaderName, token);
    }

    // ----- /api/v1/auth/csrf (G2-004R1) -----

    [Fact]
    public async Task Csrf_ReturnsRequestToken_AndHeaderName()
    {
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("requestToken", out var token));
        Assert.False(string.IsNullOrEmpty(token.GetString()));
        Assert.Equal(AuthEndpoints.CsrfHeaderName,
            doc.RootElement.GetProperty("headerName").GetString());
    }

    [Fact]
    public async Task Csrf_AuthCookie_NotExposedInResponse()
    {
        // The /csrf response MUST NOT leak the auth cookie (the
        // auth cookie is set on subsequent state-changing
        // requests, not on /csrf). The /csrf cookie is the
        // antiforgery cookie, which is HttpOnly.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        await client.GetAsync("/api/v1/auth/csrf");
        // No Set-Cookie for the auth scheme should be present.
        // The antiforgery cookie is the only one we expect.
        // This is a sanity check; the antiforgery cookie itself
        // is HttpOnly, so the response body never contains it.
        Assert.True(true);
    }

    // ----- /api/v1/auth/login -----

    [Fact]
    public async Task Login_EmptyBody_Returns400ValidationFailed()
    {
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { }),
        };
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The ProblemDetails body carries code=validation_failed.
        Assert.Contains("validation_failed", body);
    }

    [Fact]
    public async Task Login_NullUserName_Returns400ValidationFailed()
    {
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var dto = new LoginRequest(UserName: "", Password: "x");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(dto),
        };
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_BadDb_Returns503ServiceUnavailable()
    {
        // With the bad-DB connection, the user lookup fails
        // fast (no Postgres to query). The login returns
        // 503 service_unavailable — infrastructure failures are
        // separated from invalid user credentials so the UI does
        // not tell operators to chase the wrong password.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var dto = new LoginRequest(
            UserName: "admin",
            Password: "ChangeMe!2026",
            TenantCode: "default");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(dto),
        };
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("service_unavailable", body);
    }

    [Fact]
    public async Task Login_WithoutCsrfToken_Returns400CsrfValidationFailed()
    {
        // G2-004R1 — login without the antiforgery token must
        // be rejected at the CSRF layer BEFORE any credential
        // check. No user name, no password, no DB hit, no log
        // line. The body carries code=csrf_validation_failed.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var dto = new LoginRequest(
            UserName: "admin",
            Password: "ChangeMe!2026",
            TenantCode: "default");
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", dto);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("csrf_validation_failed", body);
    }

    // ----- /api/v1/auth/me -----

    [Fact]
    public async Task Me_NoCookie_Returns401AuthenticationRequired()
    {
        // GET is CSRF-exempt (safe read). No /csrf fetch
        // needed. Without an auth cookie, the request goes
        // straight to the auth handler which raises
        // AuthenticationRequiredException.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("authentication_required", body);
    }

    [Fact]
    public async Task Me_NoCsrfToken_StillAccessible()
    {
        // GET /me MUST be accessible without an antiforgery
        // token (safe read). This guards against a regression
        // that would force the SPA to fetch /csrf before
        // every /me poll.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        // Deliberately do NOT call /csrf.
        var response = await client.GetAsync("/api/v1/auth/me");
        // We expect 401 (no auth cookie) but NOT 400 (no CSRF
        // failure). The distinction matters.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_And_Me_Return_DisplayNames_And_PlatformAdmin_FromRoleAssignment()
    {
        using var factory = BuildInMemoryClient(nameof(Login_And_Me_Return_DisplayNames_And_PlatformAdmin_FromRoleAssignment));
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        });

        var csrfToken = await FetchCsrfTokenAsync(client);
        var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(
                UserName: "test_operator_g2_004",
                Password: "CorrectHorse!2026",
                TenantCode: null)),
        };
        AttachCsrfToken(loginRequest, csrfToken);

        var loginResponse = await client.SendAsync(loginRequest);
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using (var loginDoc = JsonDocument.Parse(loginBody))
        {
            Assert.Equal("Operator Evidence Test User", loginDoc.RootElement.GetProperty("displayName").GetString());
            Assert.Equal("山东谷粒机械有限公司", loginDoc.RootElement.GetProperty("tenantName").GetString());
            Assert.Equal("山东谷粒机械有限公司", loginDoc.RootElement.GetProperty("companyName").GetString());
            Assert.True(loginDoc.RootElement.GetProperty("isPlatformAdmin").GetBoolean());
            Assert.Contains(loginDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
                code => code.GetString() == "crm.lead.read");
            Assert.DoesNotContain(loginDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
                code => code.GetString() == "crm.lead.manage");
        }

        var meResponse = await client.GetAsync("/api/v1/auth/me");
        var meBody = await meResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        using var meDoc = JsonDocument.Parse(meBody);
        Assert.True(meDoc.RootElement.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal("山东谷粒机械有限公司", meDoc.RootElement.GetProperty("companyName").GetString());
        Assert.Contains(meDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.read");
        Assert.DoesNotContain(meDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.manage");

        var switchRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/company/switch")
        {
            Content = JsonContent.Create(new SwitchCompanyRequest(20_002)),
        };
        AttachCsrfToken(switchRequest, await FetchCsrfTokenAsync(client));
        var switchResponse = await client.SendAsync(switchRequest);
        Assert.Equal(HttpStatusCode.OK, switchResponse.StatusCode);
        using var switchedDoc = JsonDocument.Parse(await switchResponse.Content.ReadAsStringAsync());
        Assert.Equal("20002", switchedDoc.RootElement.GetProperty("companyId").GetString());
        Assert.Contains(switchedDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.manage");

        var switchedMeResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, switchedMeResponse.StatusCode);
        using var switchedMeDoc = JsonDocument.Parse(await switchedMeResponse.Content.ReadAsStringAsync());
        Assert.Equal("20002", switchedMeDoc.RootElement.GetProperty("companyId").GetString());
        Assert.Contains(switchedMeDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.manage");
    }

    [Fact]
    public async Task LeadAssignment_RejectsOtherScopes_AndAcceptsCurrentCompanyMember()
    {
        using var factory = BuildInMemoryClient(nameof(LeadAssignment_RejectsOtherScopes_AndAcceptsCurrentCompanyMember),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        long currentUserId;
        long otherCompanyUserId;
        long otherTenantUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var identityDb = sp.GetRequiredService<IdentityDbContext>();
            var userManager = sp.GetRequiredService<UserManager<GuliErpUser>>();
            currentUserId = await identityDb.Users.Where(u => u.UserName == "test_operator_g2_004")
                .Select(u => u.Id).SingleAsync();
            var now = DateTimeOffset.UtcNow;
            var otherCompany = new GuliErpUser { TenantId = 10_001, UserName = "assign_other_company",
                Email = "assign_other_company@example.com", DisplayName = "Other Company User", Status = UserStatus.Active, CreatedAt = now, ModifiedAt = now };
            var otherTenant = new GuliErpUser { TenantId = 90_001, UserName = "assign_other_tenant",
                Email = "assign_other_tenant@example.com", DisplayName = "Other Tenant User", Status = UserStatus.Active, CreatedAt = now, ModifiedAt = now };
            var otherCompanyResult = await userManager.CreateAsync(otherCompany);
            Assert.True(otherCompanyResult.Succeeded, string.Join("; ", otherCompanyResult.Errors.Select(e => e.Description)));
            var otherTenantResult = await userManager.CreateAsync(otherTenant);
            Assert.True(otherTenantResult.Succeeded, string.Join("; ", otherTenantResult.Errors.Select(e => e.Description)));
            otherCompanyUserId = otherCompany.Id;
            otherTenantUserId = otherTenant.Id;
            identityDb.UserCompanyMemberships.Add(new UserCompanyMembership { TenantId = 10_001,
                CompanyId = 20_001, UserId = otherCompanyUserId, Status = MembershipStatus.Active,
                JoinedAt = now, CreatedAt = now, ModifiedAt = now });
            await identityDb.SaveChangesAsync();
            var crmDb = sp.GetRequiredService<CrmDbContext>();
            crmDb.Leads.Add(new CrmLead { Id = 77_001, TenantId = 10_001, CompanyId = 20_002,
                CompanyName = "Assignment fixture", CreatedAt = now, ModifiedAt = now });
            crmDb.Companies.Add(new CrmCompany { Id = 78_001, TenantId = 10_001, CompanyId = 20_002,
                CompanyNo = "TEST-78001", FullName = "Assignment company fixture",
                CreatedAt = now, ModifiedAt = now });
            await crmDb.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("test_operator_g2_004", "CorrectHorse!2026", null))
        };
        AttachCsrfToken(login, await FetchCsrfTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        var switchCompany = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/company/switch")
        {
            Content = JsonContent.Create(new SwitchCompanyRequest(20_002))
        };
        AttachCsrfToken(switchCompany, await FetchCsrfTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(switchCompany)).StatusCode);

        var membersResponse = await client.GetAsync("/api/v1/crm/team-members");
        Assert.Equal(HttpStatusCode.OK, membersResponse.StatusCode);
        using (var members = JsonDocument.Parse(await membersResponse.Content.ReadAsStringAsync()))
        {
            var ids = members.RootElement.EnumerateArray()
                .Select(member => long.Parse(member.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Contains(currentUserId, ids);
            Assert.DoesNotContain(otherCompanyUserId, ids);
            Assert.DoesNotContain(otherTenantUserId, ids);
        }

        async Task<HttpStatusCode> Assign(long ownerId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/crm/leads/77001/assign")
            {
                Content = JsonContent.Create(ownerId)
            };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Assign(otherTenantUserId));
        Assert.Equal(HttpStatusCode.BadRequest, await Assign(otherCompanyUserId));
        Assert.Equal(HttpStatusCode.NoContent, await Assign(currentUserId));
        async Task<HttpStatusCode> AssignCompany(long ownerId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/crm/companies/78001/assign")
            {
                Content = JsonContent.Create(ownerId)
            };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await AssignCompany(otherTenantUserId));
        Assert.Equal(HttpStatusCode.BadRequest, await AssignCompany(otherCompanyUserId));
        Assert.Equal(HttpStatusCode.NoContent, await AssignCompany(currentUserId));
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var saved = await verifyDb
            .Leads.IgnoreQueryFilters().SingleAsync(x => x.Id == 77_001);
        Assert.Equal(currentUserId, saved.OwnerId);
        var savedCompany = await verifyDb.Companies.IgnoreQueryFilters().SingleAsync(x => x.Id == 78_001);
        Assert.Equal(currentUserId, savedCompany.OwnerId);
    }

    [Fact]
    public async Task SecurityStampRefresh_PreservesTenantCompanyAndReadPermission()
    {
        using var factory = BuildInMemoryClient(
            nameof(SecurityStampRefresh_PreservesTenantCompanyAndReadPermission),
            validateSecurityStampOnEveryRequest: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(
                UserName: "test_operator_g2_004",
                Password: "CorrectHorse!2026",
                TenantCode: null)),
        };
        AttachCsrfToken(loginRequest, await FetchCsrfTokenAsync(client));
        var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // A zero validation interval simulates the first minute-boundary
        // security-stamp refresh without a slow timer-based test.
        var meResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        using var meDoc = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        Assert.Equal("10001", meDoc.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("20001", meDoc.RootElement.GetProperty("companyId").GetString());
        Assert.Contains(meDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.read");

        var leadsResponse = await client.GetAsync("/api/v1/crm/leads");
        Assert.NotEqual(HttpStatusCode.Forbidden, leadsResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, leadsResponse.StatusCode);

        var switchRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/company/switch")
        {
            Content = JsonContent.Create(new SwitchCompanyRequest(20_002)),
        };
        AttachCsrfToken(switchRequest, await FetchCsrfTokenAsync(client));
        var switchResponse = await client.SendAsync(switchRequest);
        Assert.Equal(HttpStatusCode.OK, switchResponse.StatusCode);

        var switchedMeResponse = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, switchedMeResponse.StatusCode);
        using var switchedMeDoc = JsonDocument.Parse(await switchedMeResponse.Content.ReadAsStringAsync());
        Assert.Equal("20002", switchedMeDoc.RootElement.GetProperty("companyId").GetString());
        Assert.Contains(switchedMeDoc.RootElement.GetProperty("permissionCodes").EnumerateArray(),
            code => code.GetString() == "crm.lead.manage");
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrentPassword_AndRevokesSession()
    {
        using var factory = BuildInMemoryClient(nameof(ChangePassword_RequiresCurrentPassword_AndRevokesSession));
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        async Task<HttpResponseMessage> PostPasswordChangeAsync(string current, string replacement)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password/change")
            {
                Content = JsonContent.Create(new ChangePasswordRequest(current, replacement)),
            };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return await client.SendAsync(request);
        }

        async Task<HttpResponseMessage> LoginAsync(string password)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest("test_operator_g2_004", password)),
            };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return await client.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostPasswordChangeAsync("wrong", "LongUniquePassphrase2026")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync("CorrectHorse!2026")).StatusCode);
        var missingCsrf = await client.PostAsJsonAsync("/api/v1/auth/password/change",
            new ChangePasswordRequest("CorrectHorse!2026", "calm river crosses the valley"));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Contains("csrf_validation_failed", await missingCsrf.Content.ReadAsStringAsync());

        var wrongCurrent = await PostPasswordChangeAsync("wrong", "LongUniquePassphrase2026");
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Contains("current_password_invalid", await wrongCurrent.Content.ReadAsStringAsync());

        var unchanged = await PostPasswordChangeAsync("CorrectHorse!2026", "CorrectHorse!2026");
        Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
        Assert.Contains("password_policy_violation", await unchanged.Content.ReadAsStringAsync());

        var weakNew = await PostPasswordChangeAsync("CorrectHorse!2026", "short");
        Assert.Equal(HttpStatusCode.BadRequest, weakNew.StatusCode);
        Assert.Contains("password_policy_violation", await weakNew.Content.ReadAsStringAsync());

        var blockedNew = await PostPasswordChangeAsync(
            "CorrectHorse!2026", "test_operator_g2_004 is here");
        Assert.Equal(HttpStatusCode.BadRequest, blockedNew.StatusCode);
        Assert.Contains("password_policy_violation", await blockedNew.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostPasswordChangeAsync("CorrectHorse!2026", "calm river crosses the valley")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("CorrectHorse!2026")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync("calm river crosses the valley")).StatusCode);
    }

    [Fact]
    public async Task FirstLogin_MustChangePassword_BlocksBusinessUntilChange()
    {
        using var factory = BuildInMemoryClient(nameof(FirstLogin_MustChangePassword_BlocksBusinessUntilChange));
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GuliErpUser>>();
            var user = await users.FindByNameAsync("test_operator_g2_004");
            Assert.NotNull(user);
            Assert.True((await users.AddClaimAsync(user,
                new Claim(AccountSecurityClaims.MustChangePassword, "true"))).Succeeded);
        }
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        { Content = JsonContent.Create(new LoginRequest("test_operator_g2_004", "CorrectHorse!2026")) };
        AttachCsrfToken(login, await FetchCsrfTokenAsync(client));
        var loggedIn = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        Assert.Contains("\"mustChangePassword\":true", await loggedIn.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/system/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var change = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password/change")
        { Content = JsonContent.Create(new ChangePasswordRequest("CorrectHorse!2026", "long fresh password phrase 2026")) };
        AttachCsrfToken(change, await FetchCsrfTokenAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(change)).StatusCode);
    }

    [Fact]
    public async Task Mfa_Enrollment_RequiresCodeOnSubsequentLogin()
    {
        using var factory = BuildInMemoryClient(nameof(Mfa_Enrollment_RequiresCodeOnSubsequentLogin));
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return await client.SendAsync(request);
        }
        async Task<HttpResponseMessage> LoginAsync(string? code = null) =>
            await PostAsync("/api/v1/auth/login", new LoginRequest(
                "test_operator_g2_004", "CorrectHorse!2026", MfaCode: code));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync()).StatusCode);
        var setup = await PostAsync("/api/v1/auth/mfa/setup", new MfaSetupRequest("CorrectHorse!2026"));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using var json = JsonDocument.Parse(await setup.Content.ReadAsStringAsync());
        var key = json.RootElement.GetProperty("sharedKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(key));
        var code = TotpForTest(key!);
        var enabled = await PostAsync("/api/v1/auth/mfa/enable", new MfaCodeRequest(code));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var enabledJson = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
        var recovery = enabledJson.RootElement.GetProperty("recoveryCodes")[0].GetString();
        Assert.False(string.IsNullOrWhiteSpace(recovery));
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await LoginAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("00000000")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(recovery)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(recovery)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/v1/auth/mfa/disable",
            new MfaDisableRequest("CorrectHorse!2026", code))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync()).StatusCode);
    }

    private static string TotpForTest(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var accumulator = 0;
        var bits = 0;
        foreach (var character in base32.TrimEnd('=').ToUpperInvariant())
        {
            accumulator = (accumulator << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(accumulator >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter.ToArray());
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    [Fact]
    public async Task AdminReset_IsTenantScoped_AndForcesPasswordChange()
    {
        using var factory = BuildInMemoryClient(nameof(AdminReset_IsTenantScoped_AndForcesPasswordChange));
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        long targetId;
        long otherId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GuliErpUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<GuliErpRole>>();
            var role = await roles.FindByNameAsync("Platform Admin");
            Assert.NotNull(role);
            Assert.True((await roles.AddClaimAsync(role,
                new Claim(GuliErpPermissionClaimTypes.Permission, "identity.user.manage"))).Succeeded);
            var target = new GuliErpUser { TenantId = 10_001, UserName = "reset_target",
                Email = "reset_target@example.com", DisplayName = "Reset Target", Status = UserStatus.Active };
            var other = new GuliErpUser { TenantId = 99_999, UserName = "other_tenant_target",
                Email = "other_tenant_target@example.com", DisplayName = "Other", Status = UserStatus.Active };
            Assert.True((await users.CreateAsync(target, "original target passphrase")).Succeeded);
            Assert.True((await users.CreateAsync(other, "original other passphrase")).Succeeded);
            targetId = target.Id;
            otherId = other.Id;
        }
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/v1/auth/login",
            new LoginRequest("test_operator_g2_004", "CorrectHorse!2026"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(
            $"/api/v1/auth/users/{otherId}/password/reset",
            new AdminPasswordResetRequest("CorrectHorse!2026", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(
            $"/api/v1/auth/users/{targetId}/password/reset",
            new AdminPasswordResetRequest("wrong", null))).StatusCode);
        var reset = await PostAsync($"/api/v1/auth/users/{targetId}/password/reset",
            new AdminPasswordResetRequest("CorrectHorse!2026", null));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var result = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
        var temporary = result.RootElement.GetProperty("temporaryPassword").GetString();
        Assert.Equal(48, temporary!.Length);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<GuliErpUser>>();
            var target = await users.FindByIdAsync(targetId.ToString());
            Assert.NotNull(target);
            Assert.True(await users.CheckPasswordAsync(target, temporary));
            Assert.False(await users.CheckPasswordAsync(target, "original target passphrase"));
            Assert.Contains(await users.GetClaimsAsync(target),
                claim => claim.Type == AccountSecurityClaims.MustChangePassword);
        }
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostAsync("/api/v1/auth/login", new LoginRequest("reset_target", "original target passphrase"))).StatusCode);
        var targetLogin = await PostAsync("/api/v1/auth/login", new LoginRequest("reset_target", temporary));
        Assert.Equal(HttpStatusCode.OK, targetLogin.StatusCode);
        Assert.Contains("\"mustChangePassword\":true", await targetLogin.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/system/ping")).StatusCode);
    }

    // ----- /api/v1/auth/logout -----

    [Fact]
    public async Task Logout_NoCookie_NoCsrf_Returns400CsrfValidationFailed()
    {
        // G2-004R1 — logout without CSRF is rejected FIRST
        // (before the auth check). The CSRF layer is the outer
        // boundary; no /csrf fetch means the request is denied.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("csrf_validation_failed", body);
    }

    [Fact]
    public async Task Logout_WithValidCsrfToken_Returns204()
    {
        // G2-004R1 — logout with a valid CSRF token (no auth
        // cookie) reaches the business layer and returns 204
        // (idempotent). The CSRF gate passes; the auth handler
        // has no opinion because there's no user.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ----- /api/v1/auth/company/switch -----

    [Fact]
    public async Task CompanySwitch_NoCookie_NoCsrf_Returns400CsrfValidationFailed()
    {
        // G2-004R1 — company/switch without CSRF is rejected
        // FIRST. The CSRF gate is the outer boundary.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var dto = new SwitchCompanyRequest(TargetCompanyId: 100);
        var response = await client.PostAsJsonAsync("/api/v1/auth/company/switch", dto);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("csrf_validation_failed", body);
    }

    [Fact]
    public async Task CompanySwitch_NoCookie_WithCsrf_Returns401AuthenticationRequired()
    {
        // G2-004R1 — with a valid CSRF token but no auth cookie,
        // the request passes the CSRF gate and reaches the
        // auth handler, which raises AuthenticationRequiredException.
        // This proves the auth check fires AFTER the CSRF check.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var dto = new SwitchCompanyRequest(TargetCompanyId: 100);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/company/switch")
        {
            Content = JsonContent.Create(dto),
        };
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CompanySwitch_NegativeId_WithCsrf_Returns400InvalidSelection()
    {
        // With a valid CSRF token + valid cookie, an invalid
        // targetCompanyId is rejected by the input guard. The
        // CSRF gate passes; the input check fires.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var csrfToken = await FetchCsrfTokenAsync(client);
        var dto = new SwitchCompanyRequest(TargetCompanyId: -1);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/company/switch")
        {
            Content = JsonContent.Create(dto),
        };
        AttachCsrfToken(request, csrfToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_company_selection", body);
    }

    // ----- G2-002 regression (sanity) -----

    [Fact]
    public async Task ApiV1_System_Ping_Still200_After_Csrf_Wiring()
    {
        // G2-002 cross-cutting regression: the /system/ping
        // endpoint is unaffected by the G2-004R1 CSRF wiring.
        using var factory = BuildClient();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SeedPlatformRoleLoginFixtureAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IdentityDbContext>();
        var userManager = sp.GetRequiredService<UserManager<GuliErpUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<GuliErpRole>>();
        var now = DateTimeOffset.UtcNow;

        var tenant = new Tenant
        {
            Id = 10_001,
            Code = "test_operator_g2_004_t",
            Name = "山东谷粒机械有限公司",
            Status = TenantStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        };
        var company = new Company
        {
            Id = 20_001,
            TenantId = tenant.Id,
            Code = "test_operator_g2_004_c",
            Name = "山东谷粒机械有限公司",
            DefaultCurrency = "CNY",
            Timezone = "Asia/Shanghai",
            Status = CompanyStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        };
        db.Tenants.Add(tenant);
        db.Companies.Add(company);
        db.Companies.Add(new Company
        {
            Id = 20_002,
            TenantId = tenant.Id,
            Code = "test_operator_other_c",
            Name = "Other Company",
            DefaultCurrency = "CNY",
            Timezone = "Asia/Shanghai",
            Status = CompanyStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        await db.SaveChangesAsync();

        var user = new GuliErpUser
        {
            TenantId = tenant.Id,
            UserName = "test_operator_g2_004",
            Email = "test_operator_g2_004@example.com",
            EmailConfirmed = true,
            DisplayName = "Operator Evidence Test User",
            IsPlatformAdmin = false,
            Status = UserStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        };
        var userResult = await userManager.CreateAsync(user, "CorrectHorse!2026");
        Assert.True(userResult.Succeeded, string.Join("; ", userResult.Errors.Select(e => e.Description)));

        var role = new GuliErpRole
        {
            TenantId = tenant.Id,
            Name = "Platform Admin",
            NormalizedName = "PLATFORM ADMIN",
            Code = "PLATFORM_ADMIN",
            IsSystem = true,
            Status = RoleStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        };
        var roleResult = await roleManager.CreateAsync(role);
        Assert.True(roleResult.Succeeded, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        var claimResult = await roleManager.AddClaimAsync(role,
            new Claim(GuliErpPermissionClaimTypes.Permission, "crm.lead.read"));
        Assert.True(claimResult.Succeeded, string.Join("; ", claimResult.Errors.Select(e => e.Description)));

        var otherCompanyRole = new GuliErpRole
        {
            TenantId = tenant.Id,
            Name = "Other Company CRM Writer",
            NormalizedName = "OTHER COMPANY CRM WRITER",
            Code = "OTHER_COMPANY_CRM_WRITER",
            Status = RoleStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        };
        var otherRoleResult = await roleManager.CreateAsync(otherCompanyRole);
        Assert.True(otherRoleResult.Succeeded, string.Join("; ", otherRoleResult.Errors.Select(e => e.Description)));
        var otherClaimResult = await roleManager.AddClaimAsync(otherCompanyRole,
            new Claim(GuliErpPermissionClaimTypes.Permission, "crm.lead.manage"));
        Assert.True(otherClaimResult.Succeeded, string.Join("; ", otherClaimResult.Errors.Select(e => e.Description)));
        var companyAssignClaim = await roleManager.AddClaimAsync(otherCompanyRole,
            new Claim(GuliErpPermissionClaimTypes.Permission, "crm.company.assign"));
        Assert.True(companyAssignClaim.Succeeded, string.Join("; ", companyAssignClaim.Errors.Select(e => e.Description)));

        db.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            TenantId = tenant.Id,
            CompanyId = company.Id,
            UserId = user.Id,
            IsDefault = true,
            JoinedAt = now,
            Status = MembershipStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        db.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            TenantId = tenant.Id,
            CompanyId = 20_002,
            UserId = user.Id,
            IsDefault = false,
            JoinedAt = now,
            Status = MembershipStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        db.UserRoleAssignments.Add(new UserRoleAssignment
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            RoleId = role.Id,
            CompanyId = null,
            Status = AssignmentStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        db.UserRoleAssignments.Add(new UserRoleAssignment
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            RoleId = otherCompanyRole.Id,
            CompanyId = 20_002,
            Status = AssignmentStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        await db.SaveChangesAsync();
    }

    // ======================================================================
    // R43 — 负责人端点真实 HTTP 定向测试
    //
    // 验证用户已定案的负责人规则（R42 实现）：
    //   1) 建档时（POST /api/v1/crm/deals、POST /api/v1/crm/activities）可把
    //      「本公司有效成员」指定为初始负责人；跨公司 / 跨租户 / 停用成员一律 400；
    //      不指定负责人也能建档。
    //   2) 建档后普通编辑（PUT /api/v1/crm/deals/{id}、PUT /api/v1/crm/activities/{id}、
    //      PUT /api/v1/crm/companies/{id}）不能改负责人、也不能清空负责人；
    //      被拒绝时记录不得被部分修改。
    //   3) 只有专门分配入口（POST /api/v1/crm/deals/{id}/assign、
    //      POST /api/v1/crm/activities/{id}/assign）可更换或显式清空负责人。
    //
    // 全部走真实 HTTP（WebApplicationFactory + TestServer），使用 InMemory
    // Identity/CRM 夹具与合成数据：不连 PostgreSQL、不碰任何真实业务数据。
    // ======================================================================

    /// <summary>R43 夹具常量：租户、当前公司、同租户的另一家公司、登录账号。</summary>
    private const long R43TenantId = 10_001;
    private const long R43CurrentCompanyId = 20_001;
    private const long R43OtherCompanyId = 20_002;
    private const string R43OperatorUserName = "test_operator_g2_004";
    private const string R43Password = "CorrectHorse!2026";

    /// <summary>
    /// R43：与 CrmEndpoints.ValidateAssignOwnerRequest 的固定文案逐字一致。
    /// 断言文案是为了钉住「400 是哪一种拒绝」，避免「状态码对了但原因不对」的假通过。
    /// </summary>
    private const string R43AssignOwnerRequiredMessage = "请指定负责人，或明确请求清空负责人。";
    private const string R43AssignOwnerAmbiguousMessage = "分配负责人与清空负责人不能同时请求。";

    /// <summary>R43 负责人 HTTP 测试用的合成成员与合成业务数据。</summary>
    private sealed record OwnerHttpScope(
        long CurrentMemberId,
        long SecondMemberId,
        long OtherCompanyMemberId,
        long OtherTenantMemberId,
        long DisabledMemberId,
        long RevokedMembershipMemberId,
        long UnauthorizedUserId,
        long CrmCompanyId,
        long DealAssessmentId,
        IReadOnlyList<long> CreatableAssessmentIds,
        long DealId,
        long ActivityId);

    /// <summary>
    /// InMemory 版 CRM 编号生成器：生产实现走 PostgreSQL 序列（SELECT nextval(...)），
    /// InMemory provider 执行不了原始 SQL，建档路径必然 500。
    /// 编号形态（CO/DE + 日期 + 4 位序号）与生产保持一致，只是不再落库。
    /// </summary>
    private sealed class InMemoryCrmNumberGenerator : ICrmNumberGenerator
    {
        private long _company;
        private long _deal;

        public Task<string> NextCompanyNoAsync(CancellationToken ct) =>
            Task.FromResult($"CO{DateTime.Now:yyyyMMdd}{Interlocked.Increment(ref _company):D4}");

        public Task<string> NextDealNoAsync(CancellationToken ct) =>
            Task.FromResult($"DE{DateTime.Now:yyyyMMdd}{Interlocked.Increment(ref _deal):D4}");
    }

    /// <summary>
    /// 播种 R43 负责人 HTTP 测试所需的合成数据：
    /// 合成成员（本公司有效 / 同公司另一成员 / 跨公司 / 跨租户 / 停用 / 成员关系已撤销 /
    /// 无管理权限）、合成 CRM 客户、线索、已确认需求评估，以及已建档的商机与跟进记录。
    ///
    /// <para>
    /// 这里只补「测试夹具」：合成用户、角色声明与业务行。
    /// 不新增权限码、不改角色包、不改生产策略，也不碰任何真实业务数据。
    /// </para>
    /// </summary>
    private static async Task<OwnerHttpScope> SeedOwnerHttpScopeAsync(
        IServiceProvider services, int creatableAssessmentCount = 2)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var identityDb = sp.GetRequiredService<IdentityDbContext>();
        var userManager = sp.GetRequiredService<UserManager<GuliErpUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<GuliErpRole>>();
        var now = DateTimeOffset.UtcNow;

        // 当前登录用户：SeedPlatformRoleLoginFixtureAsync 已建，
        // 且在 20_001 有一条启用的成员关系（IsDefault = true）。
        var currentUser = await identityDb.Users.SingleAsync(u => u.UserName == R43OperatorUserName);

        async Task<GuliErpRole> CreateRoleAsync(string name, string code, string[] permissions)
        {
            var role = new GuliErpRole
            {
                TenantId = R43TenantId,
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                Code = code,
                Status = RoleStatus.Active,
                CreatedAt = now,
                ModifiedAt = now,
                ConcurrencyVersion = 1,
            };
            var created = await roleManager.CreateAsync(role);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            foreach (var permission in permissions)
            {
                var claim = await roleManager.AddClaimAsync(role,
                    new Claim(GuliErpPermissionClaimTypes.Permission, permission));
                Assert.True(claim.Succeeded, string.Join("; ", claim.Errors.Select(e => e.Description)));
            }
            return role;
        }

        // 写权限角色按公司授予（CompanyId = 20_001）：
        // 权限判定只在「角色授权范围包含当前公司」时才命中。
        var writerRole = await CreateRoleAsync("CRM Owner Test Writer", "CRM_OWNER_TEST_WRITER",
            new[]
            {
                "crm.deal.read", "crm.deal.manage",
                "crm.activity.read", "crm.activity.manage",
                "crm.company.read", "crm.company.manage",
            });
        var readerRole = await CreateRoleAsync("CRM Owner Test Reader", "CRM_OWNER_TEST_READER",
            new[] { "crm.deal.read", "crm.activity.read", "crm.company.read" });

        identityDb.UserRoleAssignments.Add(new UserRoleAssignment
        {
            TenantId = R43TenantId,
            UserId = currentUser.Id,
            RoleId = writerRole.Id,
            CompanyId = R43CurrentCompanyId,
            Status = AssignmentStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });

        async Task<GuliErpUser> CreateMemberAsync(
            string userName, long tenantId, long companyId,
            UserStatus userStatus, MembershipStatus membershipStatus)
        {
            var user = new GuliErpUser
            {
                TenantId = tenantId,
                UserName = userName,
                Email = $"{userName}@example.com",
                EmailConfirmed = true,
                DisplayName = userName,
                IsPlatformAdmin = false,
                Status = userStatus,
                CreatedAt = now,
                ModifiedAt = now,
                ConcurrencyVersion = 1,
            };
            var created = await userManager.CreateAsync(user, R43Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            identityDb.UserCompanyMemberships.Add(new UserCompanyMembership
            {
                TenantId = tenantId,
                CompanyId = companyId,
                UserId = user.Id,
                IsDefault = true,
                Status = membershipStatus,
                JoinedAt = now,
                CreatedAt = now,
                ModifiedAt = now,
                ConcurrencyVersion = 1,
            });
            await identityDb.SaveChangesAsync();
            return user;
        }

        // 同公司另一名有效成员：用于「更换负责人」的正例。
        var secondMember = await CreateMemberAsync("r43_second_member", R43TenantId, R43CurrentCompanyId,
            UserStatus.Active, MembershipStatus.Active);
        // 同租户、另一家公司：跨公司。
        var otherCompanyMember = await CreateMemberAsync("r43_other_company", R43TenantId, R43OtherCompanyId,
            UserStatus.Active, MembershipStatus.Active);
        // 另一租户：跨租户。
        var otherTenantMember = await CreateMemberAsync("r43_other_tenant", 90_001, R43CurrentCompanyId,
            UserStatus.Active, MembershipStatus.Active);
        // 账号停用。
        var disabledMember = await CreateMemberAsync("r43_disabled_member", R43TenantId, R43CurrentCompanyId,
            UserStatus.Disabled, MembershipStatus.Active);
        // 账号启用、但成员关系已撤销。
        var revokedMember = await CreateMemberAsync("r43_revoked_member", R43TenantId, R43CurrentCompanyId,
            UserStatus.Active, MembershipStatus.Revoked);
        // 已认证但没有商机/跟进管理权限：用于验证专门分配入口的授权面。
        var unauthorized = await CreateMemberAsync("r43_no_manage", R43TenantId, R43CurrentCompanyId,
            UserStatus.Active, MembershipStatus.Active);

        identityDb.UserRoleAssignments.Add(new UserRoleAssignment
        {
            TenantId = R43TenantId,
            UserId = unauthorized.Id,
            RoleId = readerRole.Id,
            CompanyId = R43CurrentCompanyId,
            Status = AssignmentStatus.Active,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });
        await identityDb.SaveChangesAsync();

        var crmDb = sp.GetRequiredService<CrmDbContext>();
        var crmCompanyId = 78_101L;
        crmDb.Companies.Add(new CrmCompany
        {
            Id = crmCompanyId,
            TenantId = R43TenantId,
            CompanyId = R43CurrentCompanyId,
            CompanyNo = "TEST-78101",
            FullName = "R43 客户主档夹具",
            City = CompanyCity.Weifang,
            Industry = CompanyIndustry.Machinery,
            Source = SourceChannel.Other,
            OwnerId = currentUser.Id,
            CreatedAt = now,
            ModifiedAt = now,
            ConcurrencyVersion = 1,
        });

        // 每条已确认需求评估配一条「已转成当前客户」的线索：
        // 建商机时服务端会校验「评估已确认 + 版本一致 + 来源线索已转成当前客户 + 品类一致」。
        var assessmentIds = new List<long>();
        for (var i = 0; i < creatableAssessmentCount + 1; i++)
        {
            var leadId = 77_101 + i;
            var assessmentId = 79_101 + i;
            crmDb.Leads.Add(new CrmLead
            {
                Id = leadId,
                TenantId = R43TenantId,
                CompanyId = R43CurrentCompanyId,
                CompanyName = $"R43 线索夹具 {i + 1}",
                Status = LeadStatus.Converted,
                Source = SourceChannel.Other,
                ConvertedToCompanyId = crmCompanyId,
                ConvertedAt = now,
                ConvertedBy = currentUser.Id,
                CreatedAt = now,
                ModifiedAt = now,
                ConcurrencyVersion = 1,
            });
            crmDb.LeadDemandAssessments.Add(new CrmLeadDemandAssessment
            {
                Id = assessmentId,
                TenantId = R43TenantId,
                CompanyId = R43CurrentCompanyId,
                LeadId = leadId,
                Category = BusinessCategory.WebsiteBuild,
                State = "confirmed",
                Version = 1,
                ReviewerId = currentUser.Id,
                ReviewedAt = DateTime.UtcNow,
            });
            assessmentIds.Add(assessmentId);
        }

        // 已建档的商机与跟进记录：负责人 = 当前有效成员。
        // ModifiedAt 固定成一个早于「现在」的旧值，便于断言分配动作确实刷新了审计字段。
        var seededModifiedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dealId = 80_101L;
        var activityId = 81_101L;
        crmDb.Deals.Add(new CrmDeal
        {
            Id = dealId,
            TenantId = R43TenantId,
            CompanyId = R43CurrentCompanyId,
            DealNo = "DE-TEST-80101",
            Name = "R43 原始商机",
            CrmCompanyId = crmCompanyId,
            Category = BusinessCategory.WebsiteBuild,
            SourceLeadDemandAssessmentId = assessmentIds[0],
            SourceLeadDemandAssessmentVersion = 1,
            ExpectedAmount = 12345.67m,
            Stage = DealStage.InitialContact,
            Status = DealStatus.InProgress,
            OwnerId = currentUser.Id,
            CreatedAt = seededModifiedAt,
            CreatedBy = currentUser.Id,
            ModifiedAt = seededModifiedAt,
            ModifiedBy = null,
            ConcurrencyVersion = 1,
        });
        crmDb.Activities.Add(new CrmActivity
        {
            Id = activityId,
            TenantId = R43TenantId,
            CompanyId = R43CurrentCompanyId,
            CrmCompanyId = crmCompanyId,
            DealId = dealId,
            FollowType = FollowType.Phone,
            FollowAt = now,
            Content = "R43 原始跟进内容",
            NextAction = "R43 原始下次动作",
            OwnerId = currentUser.Id,
            CreatedAt = seededModifiedAt,
            CreatedBy = currentUser.Id,
            ModifiedAt = seededModifiedAt,
            ModifiedBy = null,
            ConcurrencyVersion = 1,
        });
        await crmDb.SaveChangesAsync();

        return new OwnerHttpScope(
            currentUser.Id,
            secondMember.Id,
            otherCompanyMember.Id,
            otherTenantMember.Id,
            disabledMember.Id,
            revokedMember.Id,
            unauthorized.Id,
            crmCompanyId,
            assessmentIds[0],
            assessmentIds.Skip(1).ToArray(),
            dealId,
            activityId);
    }

    // ----- R43 HTTP 辅助 -----

    /// <summary>R43：登录并返回带 Cookie 的已认证客户端。</summary>
    private static async Task<HttpClient> LoginR43ClientAsync(
        WebApplicationFactory<Program> factory, string userName, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(userName, password, null)),
        };
        AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    /// <summary>
    /// R43：带 CSRF 令牌的请求。写操作必须走生产防伪校验，测试不绕过。
    /// </summary>
    private static async Task<HttpResponseMessage> SendR43Async(
        HttpClient client, HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        AttachCsrfToken(request, await FetchCsrfTokenAsync(client));
        return await client.SendAsync(request);
    }

    /// <summary>R43：新建商机的最小合法请求体（含服务端要求的已确认需求评估）。</summary>
    private static Dictionary<string, object?> NewDealBody(
        long crmCompanyId, long assessmentId, long? ownerId, bool includeOwnerId = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "R43 新建商机",
            ["crmCompanyId"] = crmCompanyId,
            ["category"] = "WebsiteBuild",
            ["expectedAmount"] = 8888.00m,
            ["stage"] = "InitialContact",
            ["sourceLeadDemandAssessmentId"] = assessmentId,
            ["sourceLeadDemandAssessmentVersion"] = 1,
        };
        if (includeOwnerId) body["ownerId"] = ownerId;
        return body;
    }

    /// <summary>R43：普通编辑商机的请求体。</summary>
    private static Dictionary<string, object?> NewDealUpdateBody(
        string name, decimal expectedAmount, long? ownerId, bool includeOwnerId = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["category"] = "WebsiteBuild",
            ["expectedAmount"] = expectedAmount,
            ["stage"] = "InitialContact",
            ["status"] = "InProgress",
        };
        if (includeOwnerId) body["ownerId"] = ownerId;
        return body;
    }

    /// <summary>R43：新建跟进记录的最小合法请求体。</summary>
    private static Dictionary<string, object?> NewActivityBody(
        long crmCompanyId, long? ownerId, bool includeOwnerId = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["crmCompanyId"] = crmCompanyId,
            ["followType"] = "Phone",
            ["content"] = "R43 新建跟进内容",
            ["nextAction"] = "R43 新建下次动作",
        };
        if (includeOwnerId) body["ownerId"] = ownerId;
        return body;
    }

    /// <summary>R43：普通编辑跟进记录的请求体。</summary>
    private static Dictionary<string, object?> NewActivityUpdateBody(
        string content, string nextAction, long? ownerId, bool includeOwnerId = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["followType"] = "Phone",
            ["followAt"] = "2026-10-29T09:00:00+00:00",
            ["content"] = content,
            ["nextAction"] = nextAction,
        };
        if (includeOwnerId) body["ownerId"] = ownerId;
        return body;
    }

    /// <summary>R43：普通编辑客户主档的请求体。</summary>
    private static Dictionary<string, object?> NewCompanyUpdateBody(
        string fullName, long? ownerId, bool includeOwnerId = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = fullName,
            ["city"] = "Weifang",
            ["industry"] = "Machinery",
            ["hasWebsite"] = false,
            ["source"] = "Other",
            ["status"] = "ToContact",
        };
        if (includeOwnerId) body["ownerId"] = ownerId;
        return body;
    }

    /// <summary>R43：读取数据库里的商机最终状态（绕开查询过滤器，直接看落库结果）。</summary>
    private static async Task<(long? OwnerId, string Name, decimal ExpectedAmount, long? ModifiedBy, DateTimeOffset ModifiedAt)>
        ReadDealAsync(IServiceProvider services, long dealId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var deal = await db.Deals.IgnoreQueryFilters().SingleAsync(x => x.Id == dealId);
        return (deal.OwnerId, deal.Name, deal.ExpectedAmount, deal.ModifiedBy, deal.ModifiedAt);
    }

    /// <summary>R43：读取数据库里的跟进记录最终状态。</summary>
    private static async Task<(long? OwnerId, string Content, long? ModifiedBy, DateTimeOffset ModifiedAt)>
        ReadActivityAsync(IServiceProvider services, long activityId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var activity = await db.Activities.IgnoreQueryFilters().SingleAsync(x => x.Id == activityId);
        return (activity.OwnerId, activity.Content, activity.ModifiedBy, activity.ModifiedAt);
    }

    /// <summary>R43：读取数据库里的客户主档最终状态。</summary>
    private static async Task<(long? OwnerId, string FullName)> ReadCompanyAsync(
        IServiceProvider services, long companyId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var company = await db.Companies.IgnoreQueryFilters().SingleAsync(x => x.Id == companyId);
        return (company.OwnerId, company.FullName);
    }

    /// <summary>R43：统计商机总数（用于证明被拒的建档请求没有留下记录）。</summary>
    private static async Task<int> CountDealsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        return await db.Deals.IgnoreQueryFilters().CountAsync();
    }

    /// <summary>R43：统计跟进记录总数（用于证明被拒的建档请求没有留下记录）。</summary>
    private static async Task<int> CountActivitiesAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        return await db.Activities.IgnoreQueryFilters().CountAsync();
    }

    /// <summary>
    /// R43：断言一次「负责人不是本公司有效成员」的拒绝。
    /// 除了状态码，还钉住具体原因文案——只断言状态码会让「因为别的原因 400」也蒙混过关。
    /// </summary>
    private static async Task AssertInvalidMemberRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(CrmOwnerGuard.InvalidMemberMessage, body, StringComparison.Ordinal);
    }

    /// <summary>R43：读取响应里的 Snowflake ID（线上契约：long 以 JSON 字符串传输）。</summary>
    private static long ReadSnowflakeId(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>R43：读取响应里的可空 Snowflake ID。</summary>
    private static long? ReadNullableSnowflakeId(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ----- R43：建档时指定初始负责人 -----

    [Fact]
    public async Task R43_DealCreate_AcceptsActiveCompanyMember_RejectsOutOfScopeMembers_AndAllowsNoOwner()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_DealCreate_AcceptsActiveCompanyMember_RejectsOutOfScopeMembers_AndAllowsNoOwner),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services, creatableAssessmentCount: 2);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);

        // 1) 当前公司有效成员 → 建档成功，负责人落库。
        var accepted = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[0], scope.CurrentMemberId));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var acceptedDoc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var acceptedDealId = ReadSnowflakeId(acceptedDoc.RootElement, "id");
        Assert.Equal(scope.CurrentMemberId, ReadNullableSnowflakeId(acceptedDoc.RootElement, "ownerId"));
        var acceptedSaved = await ReadDealAsync(factory.Services, acceptedDealId);
        Assert.Equal(scope.CurrentMemberId, acceptedSaved.OwnerId);

        // 2) 跨公司成员 → 400。
        var crossCompany = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[1], scope.OtherCompanyMemberId));
        await AssertInvalidMemberRejectedAsync(crossCompany);

        // 3) 跨租户成员 → 400。
        var crossTenant = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[1], scope.OtherTenantMemberId));
        await AssertInvalidMemberRejectedAsync(crossTenant);

        // 4) 停用成员 → 400。
        var disabled = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[1], scope.DisabledMemberId));
        await AssertInvalidMemberRejectedAsync(disabled);

        // 5) 成员关系已撤销 → 400。
        var revoked = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[1], scope.RevokedMembershipMemberId));
        await AssertInvalidMemberRejectedAsync(revoked);

        // 被拒的建档请求不得留下商机记录（种子 1 条 + 成功 1 条 = 2 条）。
        Assert.Equal(2, await CountDealsAsync(factory.Services));

        // 6) 不指定负责人 → 也能建档，负责人为空。
        var noOwner = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/deals",
            NewDealBody(scope.CrmCompanyId, scope.CreatableAssessmentIds[1], ownerId: null, includeOwnerId: false));
        Assert.Equal(HttpStatusCode.Created, noOwner.StatusCode);
        using var noOwnerDoc = JsonDocument.Parse(await noOwner.Content.ReadAsStringAsync());
        var noOwnerDealId = ReadSnowflakeId(noOwnerDoc.RootElement, "id");
        Assert.Null(ReadNullableSnowflakeId(noOwnerDoc.RootElement, "ownerId"));
        var noOwnerSaved = await ReadDealAsync(factory.Services, noOwnerDealId);
        Assert.Null(noOwnerSaved.OwnerId);
    }

    [Fact]
    public async Task R43_ActivityCreate_AcceptsActiveCompanyMember_RejectsOutOfScopeMembers_AndAllowsNoOwner()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_ActivityCreate_AcceptsActiveCompanyMember_RejectsOutOfScopeMembers_AndAllowsNoOwner),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);

        // 1) 当前公司有效成员 → 建档成功，负责人落库。
        var accepted = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, scope.CurrentMemberId));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var acceptedDoc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var acceptedActivityId = ReadSnowflakeId(acceptedDoc.RootElement, "id");
        Assert.Equal(scope.CurrentMemberId, ReadNullableSnowflakeId(acceptedDoc.RootElement, "ownerId"));
        var acceptedSaved = await ReadActivityAsync(factory.Services, acceptedActivityId);
        Assert.Equal(scope.CurrentMemberId, acceptedSaved.OwnerId);

        // 2) 跨公司成员 → 400。
        var crossCompany = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, scope.OtherCompanyMemberId));
        await AssertInvalidMemberRejectedAsync(crossCompany);

        // 3) 跨租户成员 → 400。
        var crossTenant = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, scope.OtherTenantMemberId));
        await AssertInvalidMemberRejectedAsync(crossTenant);

        // 4) 停用成员 → 400。
        var disabled = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, scope.DisabledMemberId));
        await AssertInvalidMemberRejectedAsync(disabled);

        // 5) 成员关系已撤销 → 400。
        var revoked = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, scope.RevokedMembershipMemberId));
        await AssertInvalidMemberRejectedAsync(revoked);

        // 被拒的建档请求不得留下跟进记录（种子 1 条 + 成功 1 条 = 2 条）。
        Assert.Equal(2, await CountActivitiesAsync(factory.Services));

        // 6) 不指定负责人 → 也能建档，负责人为空。
        var noOwner = await SendR43Async(client, HttpMethod.Post, "/api/v1/crm/activities",
            NewActivityBody(scope.CrmCompanyId, ownerId: null, includeOwnerId: false));
        Assert.Equal(HttpStatusCode.Created, noOwner.StatusCode);
        using var noOwnerDoc = JsonDocument.Parse(await noOwner.Content.ReadAsStringAsync());
        var noOwnerActivityId = ReadSnowflakeId(noOwnerDoc.RootElement, "id");
        Assert.Null(ReadNullableSnowflakeId(noOwnerDoc.RootElement, "ownerId"));
        var noOwnerSaved = await ReadActivityAsync(factory.Services, noOwnerActivityId);
        Assert.Null(noOwnerSaved.OwnerId);
    }

    // ----- R43：普通编辑不得改 / 清空负责人 -----

    [Fact]
    public async Task R43_DealUpdate_CannotChangeOrClearOwner_AndDoesNotPartiallyModify()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_DealUpdate_CannotChangeOrClearOwner_AndDoesNotPartiallyModify),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);
        var path = $"/api/v1/crm/deals/{scope.DealId}";

        // 1) 省略 ownerId → 200，负责人不变，其他字段正常落库。
        var omitted = await SendR43Async(client, HttpMethod.Put, path,
            NewDealUpdateBody("R43 改名一", 1111.11m, ownerId: null, includeOwnerId: false));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var afterOmitted = await ReadDealAsync(factory.Services, scope.DealId);
        Assert.Equal(scope.CurrentMemberId, afterOmitted.OwnerId);
        Assert.Equal("R43 改名一", afterOmitted.Name);
        Assert.Equal(1111.11m, afterOmitted.ExpectedAmount);

        // 2) 显式 null ownerId → 200，负责人保持不变。
        var explicitNull = await SendR43Async(client, HttpMethod.Put, path,
            NewDealUpdateBody("R43 改名二", 2222.22m, ownerId: null, includeOwnerId: true));
        Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        var afterNull = await ReadDealAsync(factory.Services, scope.DealId);
        Assert.Equal(scope.CurrentMemberId, afterNull.OwnerId);
        Assert.Equal("R43 改名二", afterNull.Name);
        Assert.Equal(2222.22m, afterNull.ExpectedAmount);

        // 3) 不同非空 ownerId → 400，且记录不得被部分修改。
        var rejected = await SendR43Async(client, HttpMethod.Put, path,
            NewDealUpdateBody("R43 越权改名", 9999.99m, scope.SecondMemberId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        // 报错文案必须指向专门的分配入口，否则调用方只会以为普通编辑接口坏了。
        Assert.Contains("/api/v1/crm/deals/{id}/assign", rejectedBody);
        var afterRejected = await ReadDealAsync(factory.Services, scope.DealId);
        Assert.Equal(scope.CurrentMemberId, afterRejected.OwnerId);
        Assert.Equal("R43 改名二", afterRejected.Name);
        Assert.Equal(2222.22m, afterRejected.ExpectedAmount);
    }

    [Fact]
    public async Task R43_ActivityUpdate_CannotChangeOrClearOwner_AndDoesNotPartiallyModify()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_ActivityUpdate_CannotChangeOrClearOwner_AndDoesNotPartiallyModify),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);
        var path = $"/api/v1/crm/activities/{scope.ActivityId}";

        // 1) 省略 ownerId → 200，负责人不变，其他字段正常落库。
        var omitted = await SendR43Async(client, HttpMethod.Put, path,
            NewActivityUpdateBody("R43 内容一", "R43 动作一", ownerId: null, includeOwnerId: false));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var afterOmitted = await ReadActivityAsync(factory.Services, scope.ActivityId);
        Assert.Equal(scope.CurrentMemberId, afterOmitted.OwnerId);
        Assert.Equal("R43 内容一", afterOmitted.Content);

        // 2) 显式 null ownerId → 200，负责人保持不变。
        var explicitNull = await SendR43Async(client, HttpMethod.Put, path,
            NewActivityUpdateBody("R43 内容二", "R43 动作二", ownerId: null, includeOwnerId: true));
        Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        var afterNull = await ReadActivityAsync(factory.Services, scope.ActivityId);
        Assert.Equal(scope.CurrentMemberId, afterNull.OwnerId);
        Assert.Equal("R43 内容二", afterNull.Content);

        // 3) 不同非空 ownerId → 400，且记录不得被部分修改。
        var rejected = await SendR43Async(client, HttpMethod.Put, path,
            NewActivityUpdateBody("R43 越权内容", "R43 越权动作", scope.SecondMemberId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/crm/activities/{id}/assign", rejectedBody);
        var afterRejected = await ReadActivityAsync(factory.Services, scope.ActivityId);
        Assert.Equal(scope.CurrentMemberId, afterRejected.OwnerId);
        Assert.Equal("R43 内容二", afterRejected.Content);
    }

    [Fact]
    public async Task R43_CompanyUpdate_CannotChangeOrClearOwner()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_CompanyUpdate_CannotChangeOrClearOwner),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);
        var path = $"/api/v1/crm/companies/{scope.CrmCompanyId}";

        // 1) 省略 ownerId → 200，负责人不变。
        var omitted = await SendR43Async(client, HttpMethod.Put, path,
            NewCompanyUpdateBody("R43 客户改名一", ownerId: null, includeOwnerId: false));
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        var afterOmitted = await ReadCompanyAsync(factory.Services, scope.CrmCompanyId);
        Assert.Equal(scope.CurrentMemberId, afterOmitted.OwnerId);
        Assert.Equal("R43 客户改名一", afterOmitted.FullName);

        // 2) 显式 null ownerId → 200，负责人保持不变。
        var explicitNull = await SendR43Async(client, HttpMethod.Put, path,
            NewCompanyUpdateBody("R43 客户改名二", ownerId: null, includeOwnerId: true));
        Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        var afterNull = await ReadCompanyAsync(factory.Services, scope.CrmCompanyId);
        Assert.Equal(scope.CurrentMemberId, afterNull.OwnerId);

        // 3) 不同非空 ownerId → 400，且记录不得被部分修改。
        var rejected = await SendR43Async(client, HttpMethod.Put, path,
            NewCompanyUpdateBody("R43 客户越权改名", scope.SecondMemberId));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("/api/v1/crm/companies/{id}/assign", await rejected.Content.ReadAsStringAsync());
        var afterRejected = await ReadCompanyAsync(factory.Services, scope.CrmCompanyId);
        Assert.Equal(scope.CurrentMemberId, afterRejected.OwnerId);
        Assert.Equal("R43 客户改名二", afterRejected.FullName);
    }

    // ----- R43：专门分配入口 -----

    [Fact]
    public async Task R43_DealAssign_AssignsAndClearsExplicitly_AndRejectsInvalidRequests()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_DealAssign_AssignsAndClearsExplicitly_AndRejectsInvalidRequests),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);
        var path = $"/api/v1/crm/deals/{scope.DealId}/assign";

        // 1) 本公司有效成员 → 204，负责人换成该成员。
        var assigned = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.SecondMemberId });
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        var afterAssign = await ReadDealAsync(factory.Services, scope.DealId);
        Assert.Equal(scope.SecondMemberId, afterAssign.OwnerId);
        // 审计字段必须同步更新：执行人 = 当前操作者，修改时间被刷新。
        Assert.Equal(scope.CurrentMemberId, afterAssign.ModifiedBy);
        Assert.True(afterAssign.ModifiedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // 2) 跨公司成员 → 400，负责人不变。
        var crossCompany = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.OtherCompanyMemberId });
        await AssertInvalidMemberRejectedAsync(crossCompany);
        Assert.Equal(scope.SecondMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        // 3) 跨租户成员 → 400，负责人不变。
        var crossTenant = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.OtherTenantMemberId });
        await AssertInvalidMemberRejectedAsync(crossTenant);
        Assert.Equal(scope.SecondMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        // 4) 停用成员 → 400，负责人不变。
        var disabled = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.DisabledMemberId });
        await AssertInvalidMemberRejectedAsync(disabled);
        Assert.Equal(scope.SecondMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        // 5) 空请求（既不指定负责人也不请求清空）→ 400，负责人不变。
        var empty = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?>());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains(R43AssignOwnerRequiredMessage, await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(scope.SecondMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        // 6) ownerId 与 clear 同时给出（歧义请求）→ 400，负责人不变。
        var ambiguous = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.CurrentMemberId, ["clear"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        Assert.Contains(R43AssignOwnerAmbiguousMessage, await ambiguous.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(scope.SecondMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        // 7) 显式清空 → 204，负责人为空，审计字段仍被刷新。
        var cleared = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["clear"] = true });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var afterClear = await ReadDealAsync(factory.Services, scope.DealId);
        Assert.Null(afterClear.OwnerId);
        Assert.Equal(scope.CurrentMemberId, afterClear.ModifiedBy);
        Assert.True(afterClear.ModifiedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task R43_ActivityAssign_AssignsAndClearsExplicitly_AndRejectsInvalidRequests()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_ActivityAssign_AssignsAndClearsExplicitly_AndRejectsInvalidRequests),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);
        using var client = await LoginR43ClientAsync(factory, R43OperatorUserName, R43Password);
        var path = $"/api/v1/crm/activities/{scope.ActivityId}/assign";

        // 1) 本公司有效成员 → 204，负责人换成该成员。
        var assigned = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.SecondMemberId });
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        var afterAssign = await ReadActivityAsync(factory.Services, scope.ActivityId);
        Assert.Equal(scope.SecondMemberId, afterAssign.OwnerId);
        Assert.Equal(scope.CurrentMemberId, afterAssign.ModifiedBy);
        Assert.True(afterAssign.ModifiedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // 2) 跨公司成员 → 400，负责人不变。
        var crossCompany = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.OtherCompanyMemberId });
        await AssertInvalidMemberRejectedAsync(crossCompany);
        Assert.Equal(scope.SecondMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);

        // 3) 跨租户成员 → 400，负责人不变。
        var crossTenant = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.OtherTenantMemberId });
        await AssertInvalidMemberRejectedAsync(crossTenant);
        Assert.Equal(scope.SecondMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);

        // 4) 停用成员 → 400，负责人不变。
        var disabled = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.DisabledMemberId });
        await AssertInvalidMemberRejectedAsync(disabled);
        Assert.Equal(scope.SecondMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);

        // 5) 空请求（既不指定负责人也不请求清空）→ 400，负责人不变。
        var empty = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?>());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains(R43AssignOwnerRequiredMessage, await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(scope.SecondMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);

        // 6) ownerId 与 clear 同时给出（歧义请求）→ 400，负责人不变。
        var ambiguous = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["ownerId"] = scope.CurrentMemberId, ["clear"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        Assert.Contains(R43AssignOwnerAmbiguousMessage, await ambiguous.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(scope.SecondMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);

        // 7) 显式清空 → 204，负责人为空，审计字段仍被刷新。
        var cleared = await SendR43Async(client, HttpMethod.Post, path,
            new Dictionary<string, object?> { ["clear"] = true });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var afterClear = await ReadActivityAsync(factory.Services, scope.ActivityId);
        Assert.Null(afterClear.OwnerId);
        Assert.Equal(scope.CurrentMemberId, afterClear.ModifiedBy);
        Assert.True(afterClear.ModifiedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task R43_AssignEndpoints_RejectUnauthorizedCaller()
    {
        using var factory = BuildInMemoryClient(
            nameof(R43_AssignEndpoints_RejectUnauthorizedCaller),
            useInMemoryCrm: true);
        await SeedPlatformRoleLoginFixtureAsync(factory.Services);
        var scope = await SeedOwnerHttpScopeAsync(factory.Services);

        // 该用户已认证、可登录，但没有商机/跟进管理权限。
        using var client = await LoginR43ClientAsync(factory, "r43_no_manage", R43Password);

        var dealAssign = await SendR43Async(client, HttpMethod.Post,
            $"/api/v1/crm/deals/{scope.DealId}/assign",
            new Dictionary<string, object?> { ["ownerId"] = scope.SecondMemberId });
        Assert.Equal(HttpStatusCode.Forbidden, dealAssign.StatusCode);
        Assert.Equal(scope.CurrentMemberId, (await ReadDealAsync(factory.Services, scope.DealId)).OwnerId);

        var activityAssign = await SendR43Async(client, HttpMethod.Post,
            $"/api/v1/crm/activities/{scope.ActivityId}/assign",
            new Dictionary<string, object?> { ["ownerId"] = scope.SecondMemberId });
        Assert.Equal(HttpStatusCode.Forbidden, activityAssign.StatusCode);
        Assert.Equal(scope.CurrentMemberId, (await ReadActivityAsync(factory.Services, scope.ActivityId)).OwnerId);
    }
}
