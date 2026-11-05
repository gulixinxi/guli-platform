using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class ContactCandidateFacts
{
    [Fact]
    public async Task CorrectionReplacesPendingCandidateAndClearsOldPersonalFields()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 100, AssetId = 200,
            Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" });
        await db.SaveChangesAsync();
        var service = new ContactCandidateService(db);
        const string source = "https://example.com/contact";
        var original = new ContactCandidateInput("100", "原姓名", null, "old@example.com", null,
            "public_site", source, new() { ["name"] = source, ["email"] = source }, DateTime.UtcNow);
        var oldId = long.Parse(await service.CreateAsync(original, 7, default));
        var corrected = original with { Name = "新姓名" };
        var newId = long.Parse(await service.CorrectAsync(oldId,
            new(corrected, "公开网页内容已更正，重新核对"), 8, default));
        Assert.NotEqual(oldId, newId);
        var old = await db.ContactCandidates.SingleAsync(x => x.Id == oldId);
        var replacement = await db.ContactCandidates.SingleAsync(x => x.Id == newId);
        Assert.Equal("redacted", old.Status);
        Assert.Null(old.Email);
        Assert.Equal("pending", replacement.Status);
        Assert.Equal("old@example.com", replacement.Email);
        Assert.Empty(await db.Contacts.ToListAsync());
        Assert.Equal(1, await db.ContactCandidateEvents.CountAsync(x => x.CandidateId == oldId && x.Action == "correct"));
    }

    [Fact]
    public async Task ReviewPromotionRecruitmentAndRedactionRespectGates()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 10, AssetId = 20,
            Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" });
        db.Leads.Add(new Lead { Id = 30, CompanyName = "测试企业", SourceIdentityCandidateId = 10,
            ConvertedToCompanyId = 40 });
        db.Companies.Add(new Company { Id = 40, FullName = "测试企业" });
        await db.SaveChangesAsync();
        var service = new ContactCandidateService(db);
        const string source = "https://example.com/contact";
        var fields = new Dictionary<string, string> { ["name"] = source, ["email"] = source };
        var id = long.Parse(await service.CreateAsync(new("10", "李四", null,
            "li@example.com", null, "public_site", source, fields, DateTime.UtcNow), 7, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromoteAsync(id, "人工核实公开联系用途", 8, default));
        await service.ReviewAsync(id, new("confirm", "人工核实公开联系用途"), 8, default);
        var contactId = await service.PromoteAsync(id, "确认用于销售联系并转入", 8, default);
        Assert.Equal(contactId, await service.PromoteAsync(id, "重复请求保持同一联系人", 8, default));
        Assert.Single(await db.Contacts.ToListAsync());
        await service.RedactAsync(id, "收到删除个人信息请求", 8, default);
        Assert.Null((await db.ContactCandidates.SingleAsync()).Email);
        Assert.Equal(4, await db.ContactCandidateEvents.CountAsync());

        var recruitId = long.Parse(await service.CreateAsync(new("10", "招聘人员", null,
            "jobs@example.com", null, "recruitment", source, fields, DateTime.UtcNow), 7, default));
        await service.ReviewAsync(recruitId, new("confirm", "确认招聘页联系方式真实"), 8, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromoteAsync(recruitId,
            "尝试将招聘联系人转销售", 8, default));
    }

    [Fact]
    public async Task SavesSourcedCandidateWithoutCreatingOfficialContactAndShowsFullContactToAuthorizedList()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 1, AssetId = 2,
            Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" });
        await db.SaveChangesAsync();
        var service = new ContactCandidateService(db);
        var source = "https://example.com/contact";
        var input = new ContactCandidateInput("1", "张三", "销售", "zhang@example.com", "12345678",
            "public_site", source, new() { ["name"] = source, ["position"] = source,
                ["email"] = source, ["phone"] = source }, DateTime.UtcNow);
        await service.CreateAsync(input, 7, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(input, 7, default));
        Assert.Empty(await db.Contacts.ToListAsync());
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(await service.ListAsync(1, 8, default)));
        var item = result.RootElement.GetProperty("items")[0];
        Assert.Equal("zhang@example.com", item.GetProperty("Email").GetString());
        Assert.Equal("12345678", item.GetProperty("Phone").GetString());
        Assert.Equal(1, await db.ContactCandidateEvents.CountAsync(x => x.Action == "list_view" && x.ActorId == 8));
        Assert.Equal("pending", item.GetProperty("Status").GetString());
        var candidate = await db.ContactCandidates.SingleAsync();
        candidate.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        using var expiredResult = JsonDocument.Parse(JsonSerializer.Serialize(await service.ListAsync(1, 8, default)));
        var expiredItem = expiredResult.RootElement.GetProperty("items")[0];
        Assert.Equal("expired", expiredItem.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, expiredItem.GetProperty("Phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, expiredItem.GetProperty("Email").ValueKind);
        Assert.Equal(1, await db.ContactCandidateEvents.CountAsync(x => x.Action == "list_view"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(1, 0, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(input with
        {
            FieldSources = new() { ["email"] = source }
        }, 7, default));
    }
}

