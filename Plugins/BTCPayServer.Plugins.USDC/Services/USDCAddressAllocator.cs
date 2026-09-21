using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Client.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.USDC.Services;

// Allocation and release share one transaction lock per payment method. The
// current address claim can change owners; the invoice assignment never does.
public sealed class USDCAddressAllocator(ApplicationDbContextFactory factory)
{
    private static string AddressKey(PaymentMethodId pmi, string address) => $"USDC.Address.{pmi}.{address.ToLowerInvariant()}";
    private static string InvoiceKey(PaymentMethodId pmi, string invoiceId) => $"USDC.InvoiceAddress.{pmi}.{invoiceId}";
    internal sealed record Claim(string StoreId, string InvoiceId, string Address,
        int Version = 1, DateTimeOffset? CreatedAt = null, DateTimeOffset? MonitoringExpiration = null);

    public async Task<string?> Allocate(PaymentMethodId pmi, string storeId, string invoiceId, IEnumerable<string> addresses,
        DateTimeOffset? createdAt = null, DateTimeOffset? monitoringExpiration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        var candidates = addresses.Where(EVMAddressHelper.IsValid).Select(a => a.ToLowerInvariant()).Distinct().ToArray();
        await using var strategyContext = factory.CreateContext();
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = factory.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var invoiceKey = InvoiceKey(pmi, invoiceId);
            var lockKey = $"USDC.Reservations.{pmi}";
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))");
            var saved = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == invoiceKey);
            if (saved is not null)
            {
                var claim = JsonConvert.DeserializeObject<Claim>(saved.Value)!;
                if (claim.StoreId != storeId)
                    throw new InvalidOperationException("The invoice address belongs to a different store.");
                await transaction.CommitAsync();
                return claim.Address;
            }
            foreach (var address in candidates)
            {
                var addressKey = AddressKey(pmi, address);
                var existing = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == addressKey);
                if (existing is not null && await IsReserved(context, JsonConvert.DeserializeObject<Claim>(existing.Value)!, DateTimeOffset.UtcNow))
                    continue;
                var json = JsonConvert.SerializeObject(new Claim(storeId, invoiceId, address, 2, createdAt, monitoringExpiration));
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Id", "Value") VALUES ({addressKey}, CAST({json} AS jsonb))
                    ON CONFLICT ("Id") DO UPDATE SET "Value" = EXCLUDED."Value"
                    """);
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Id", "Value") VALUES ({invoiceKey}, CAST({json} AS jsonb))
                    """);
                await transaction.CommitAsync();
                return address;
            }
            await transaction.CommitAsync();
            return null;
        });
    }

    public async Task<HashSet<string>> GetUsedAddresses(PaymentMethodId pmi, IEnumerable<string> addresses)
    {
        var candidates = addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.ToLowerInvariant()).Distinct().ToArray();
        var keys = candidates.Select(a => AddressKey(pmi, a)).ToArray();
        await using var context = factory.CreateContext();
        var claims = await context.Settings.AsNoTracking().Where(s => keys.Contains(s.Id)).ToArrayAsync();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in claims)
        {
            var claim = JsonConvert.DeserializeObject<Claim>(row.Value)!;
            if (await IsReserved(context, claim, DateTimeOffset.UtcNow)) used.Add(claim.Address);
        }
        return used;
    }

    internal async Task<Claim[]> CurrentClaims(PaymentMethodId pmi)
    {
        await using var context = factory.CreateContext();
        var prefix = $"USDC.Address.{pmi}.";
        var rows = await context.Settings.AsNoTracking().Where(s => s.Id.StartsWith(prefix)).ToArrayAsync();
        return rows.Select(row => JsonConvert.DeserializeObject<Claim>(row.Value)!).ToArray();
    }

    internal async Task<Claim?> CurrentClaim(PaymentMethodId pmi, string address)
    {
        await using var context = factory.CreateContext();
        var key = AddressKey(pmi, address);
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        return row is null ? null : JsonConvert.DeserializeObject<Claim>(row.Value);
    }

    private static async Task<bool> IsReserved(ApplicationDbContext context, Claim claim, DateTimeOffset now)
    {
        // Never silently recycle claims created by the permanent-address prototype.
        if (claim.Version < 2) return true;
        var invoice = await context.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == claim.InvoiceId);
        if (invoice is null)
            return claim.MonitoringExpiration is null || claim.MonitoringExpiration > now;
        var entity = invoice.GetBlob();
        return ShouldReserve(entity.Status, entity.MonitoringExpiration, now);
    }

    internal static bool ShouldReserve(InvoiceStatus status, DateTimeOffset monitoringExpiration, DateTimeOffset now) =>
        status is InvoiceStatus.New or InvoiceStatus.Processing ||
        status == InvoiceStatus.Expired && monitoringExpiration > now;
}
