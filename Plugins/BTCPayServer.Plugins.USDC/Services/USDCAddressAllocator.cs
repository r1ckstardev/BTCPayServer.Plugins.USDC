using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.USDC.Services;

// Durable, immutable claims use the host Settings JSONB store and its unique key.
// No cache: all instances must observe the same allocation before exposing a URI.
public sealed class USDCAddressAllocator(ApplicationDbContextFactory factory)
{
    private static string AddressKey(PaymentMethodId pmi, string address) => $"USDC.Address.{pmi}.{address.ToLowerInvariant()}";
    private static string InvoiceKey(PaymentMethodId pmi, string invoiceId) => $"USDC.InvoiceAddress.{pmi}.{invoiceId}";
    private sealed record Claim(string StoreId, string InvoiceId, string Address);

    public async Task<string?> Allocate(PaymentMethodId pmi, string storeId, string invoiceId, IEnumerable<string> addresses)
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
            // Serializes retries for the same invoice even if candidate ordering changes.
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({invoiceKey}, 0))");
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
                var json = JsonConvert.SerializeObject(new Claim(storeId, invoiceId, address));
                var inserted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Id", "Value") VALUES ({addressKey}, CAST({json} AS jsonb))
                    ON CONFLICT ("Id") DO NOTHING
                    """);
                if (inserted == 0) continue;
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
        var candidates = addresses.Select(a => a.ToLowerInvariant()).Distinct().ToArray();
        var keys = candidates.Select(a => AddressKey(pmi, a)).ToArray();
        await using var context = factory.CreateContext();
        var used = await context.Settings.AsNoTracking().Where(s => keys.Contains(s.Id)).Select(s => s.Id).ToArrayAsync();
        return candidates.Where(a => used.Contains(AddressKey(pmi, a))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
