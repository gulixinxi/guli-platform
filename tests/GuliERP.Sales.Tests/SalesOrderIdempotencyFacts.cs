using GuliERP.DocumentKernel.Application;
using GuliERP.DocumentKernel.Domain.Enums;
using GuliERP.Foundation.Kernel;
using GuliERP.Mdm.Domain.Entities;
using GuliERP.Mdm.Domain.Enums;
using GuliERP.Mdm.Infrastructure.Persistence;
using GuliERP.Sales.Application;
using GuliERP.Sales.Domain.Enums;
using GuliERP.Sales.Infrastructure.Persistence;
using GuliERP.Sales.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Sales.Tests;

/// <summary>
/// TASK 3 B-3：销售订单 <c>Idempotency-Key</c> 重放。
///
/// <para>
/// <b>缺陷</b>：<c>SalesOrderService.CreateDraftAsync</c> 只把 key 传给单号生成器
/// （幂等返回同一单号），随后<b>无条件</b>再插一条订单 ⇒ 撞
/// <c>ux_gulierp_sales_order_scope_orderno</c> ⇒ 23505 ⇒ HTTP 500。
/// 端点用 <c>Idempotency-Key</c> 头对外承诺了重放安全。
/// </para>
///
/// <para>
/// <b>修法</b>：单号生成后按 <c>(TenantId, CompanyId, OrderNo)</c> 回查已存在的
/// 订单；payload 相同 ⇒ 返回原单；payload 不同 ⇒ 拒绝（ValidationFailed → 400）。
/// </para>
/// </summary>
public sealed class SalesOrderIdempotencyFacts
{
    [Fact]
    public async Task CreateDraft_SameKey_SamePayload_Returns_The_Original_Order()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();

        var first = await fixture.Service.CreateDraftAsync(NewCreateRequest(), "idem-replay-001");
        var second = await fixture.Service.CreateDraftAsync(NewCreateRequest(), "idem-replay-001");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.OrderNo, second.OrderNo);

        // The whole point: exactly ONE order row, not two.
        Assert.Equal(1, await fixture.Sales.SalesOrders.CountAsync());
        Assert.Equal(1, await fixture.Sales.SalesOrderLines.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_SameKey_DifferentPayload_Is_Rejected()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();

        await fixture.Service.CreateDraftAsync(NewCreateRequest(), "idem-replay-002");

        var ex = await Assert.ThrowsAsync<SalesValidationException>(() =>
            fixture.Service.CreateDraftAsync(NewCreateRequest(quantity: 9m), "idem-replay-002"));

        Assert.Equal(SalesErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("Idempotency-Key", ex.Message, StringComparison.Ordinal);

        // The rejected replay must not have created anything.
        Assert.Equal(1, await fixture.Sales.SalesOrders.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_WithoutKey_Creates_Distinct_Orders()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();

        var first = await fixture.Service.CreateDraftAsync(NewCreateRequest(), null);
        var second = await fixture.Service.CreateDraftAsync(NewCreateRequest(), null);

        Assert.NotEqual(first.OrderNo, second.OrderNo);
        Assert.Equal(2, await fixture.Sales.SalesOrders.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_DifferentKeys_Create_Distinct_Orders()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();

        var first = await fixture.Service.CreateDraftAsync(NewCreateRequest(), "idem-a");
        var second = await fixture.Service.CreateDraftAsync(NewCreateRequest(), "idem-b");

        Assert.NotEqual(first.OrderNo, second.OrderNo);
        Assert.Equal(2, await fixture.Sales.SalesOrders.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_Replay_Ignores_ExtraDecimal_Precision()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();

        // Quantity is persisted as numeric(18,4): 1.00005 is stored as 1.0000
        // on PostgreSQL. A legitimate replay must still be recognised as the
        // SAME payload instead of being rejected as "different payload".
        var first = await fixture.Service.CreateDraftAsync(NewCreateRequest(quantity: 1.00005m), "idem-precision");

        var replay = await fixture.Service.CreateDraftAsync(
            NewCreateRequest(quantity: 1.00005m), "idem-precision");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await fixture.Sales.SalesOrders.CountAsync());
        Assert.Equal(first.Lines.Single().Quantity, replay.Lines.Single().Quantity);
    }

    private static CreateSalesOrderRequest NewCreateRequest(decimal quantity = 1m)
        => new(
            100,
            new DateOnly(2026, 8, 22),
            null,
            null,
            [new SalesOrderLineInput(200, null, quantity, 10m, 0m, 0m, null)]);

    /// <summary>
    /// Mimics the real <c>DocumentNumberService</c>: the same Idempotency-Key
    /// resolves to the SAME document number (and does not burn a new one).
    /// </summary>
    private sealed class KeyCachingDocumentNumberService : IDocumentNumberService
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);
        private int _next = 1;

        public Task<DocumentNumberResult> GenerateAsync(
            DocumentNumberRequest request, CancellationToken ct = default)
        {
            Assert.Equal(DocumentType.SalesOrder, request.DocumentType);

            if (!string.IsNullOrEmpty(request.IdempotencyKey)
                && _byKey.TryGetValue(request.IdempotencyKey, out var cached))
            {
                return Task.FromResult(new DocumentNumberResult(cached, 0, IdempotencyReplayed: true));
            }

            var value = _next++;
            var documentNo = $"SO-{request.BusinessDate:yyyyMMdd}-{value:0000}";
            if (!string.IsNullOrEmpty(request.IdempotencyKey))
            {
                _byKey[request.IdempotencyKey] = documentNo;
            }

            return Task.FromResult(new DocumentNumberResult(documentNo, value, IdempotencyReplayed: false));
        }
    }

    private sealed class IdempotencyFixture : IAsyncDisposable
    {
        private IdempotencyFixture(
            SalesDbContext sales,
            MdmDbContext mdm,
            MutableCurrentTenant currentTenant,
            MutableCurrentCompany currentCompany,
            SalesOrderService service)
        {
            Sales = sales;
            Mdm = mdm;
            CurrentTenant = currentTenant;
            CurrentCompany = currentCompany;
            Service = service;
        }

        public SalesDbContext Sales { get; }
        public MdmDbContext Mdm { get; }
        public MutableCurrentTenant CurrentTenant { get; }
        public MutableCurrentCompany CurrentCompany { get; }
        public SalesOrderService Service { get; }

        public static async Task<IdempotencyFixture> CreateAsync()
        {
            var dbName = Guid.NewGuid().ToString("N");
            var sales = new SalesDbContext(new DbContextOptionsBuilder<SalesDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options);
            var mdm = new MdmDbContext(new DbContextOptionsBuilder<MdmDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options);

            var uom = new Uom
            {
                Id = 300,
                Code = "PCS",
                Name = "Piece",
                Dimension = UomDimension.Count,
                Kind = UomKind.Discrete,
                Status = MasterDataStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow,
                ConcurrencyVersion = 1,
            };
            var customer = new BusinessPartner
            {
                Id = 100,
                TenantId = 1,
                Code = "CUST_001",
                Name = "Acme Customer",
                Role = BusinessPartnerRole.Customer,
                Status = MasterDataStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow,
                ConcurrencyVersion = 1,
            };
            var item = new Item
            {
                Id = 200,
                TenantId = 1,
                Code = "ITEM_001",
                Name = "Finished Item",
                BaseUomId = uom.Id,
                ItemNature = ItemNature.FinishedGood,
                Status = MasterDataStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                ModifiedAt = DateTimeOffset.UtcNow,
                ConcurrencyVersion = 1,
            };

            mdm.Uoms.Add(uom);
            mdm.BusinessPartners.Add(customer);
            mdm.Items.Add(item);
            await mdm.SaveChangesAsync();

            var service = new SalesOrderService(
                sales,
                mdm,
                new MutableCurrentTenant(1),
                new MutableCurrentCompany(10),
                new MutableCurrentUser(99),
                new KeyCachingDocumentNumberService(),
                NullLogger<SalesOrderService>.Instance);

            return new IdempotencyFixture(sales, mdm, new MutableCurrentTenant(1), new MutableCurrentCompany(10), service);
        }

        public async ValueTask DisposeAsync()
        {
            await Sales.DisposeAsync();
            await Mdm.DisposeAsync();
        }
    }

    private sealed class MutableCurrentTenant(long id) : ICurrentTenant
    {
        public long? Id { get; private set; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? tenantId)
        {
            var previous = Id;
            Id = tenantId;
            return new Restore(() => Id = previous);
        }
        public void Set(long? id) => Id = id;
    }

    private sealed class MutableCurrentCompany(long id) : ICurrentCompany
    {
        public long? Id { get; private set; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? companyId)
        {
            var previous = Id;
            Id = companyId;
            return new Restore(() => Id = previous);
        }
        public void Set(long? id) => Id = id;
    }

    private sealed class MutableCurrentUser(long id) : ICurrentUser
    {
        public long? Id { get; private set; } = id;
        public string? UserName => "tester";
        public bool IsAuthenticated => Id.HasValue;
        public bool IsPlatformAdmin => false;
        public IDisposable Change(long? userId)
        {
            var previous = Id;
            Id = userId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
