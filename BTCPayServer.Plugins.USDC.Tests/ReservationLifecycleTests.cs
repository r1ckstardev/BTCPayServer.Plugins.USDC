using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Xunit;
using InvoiceData = BTCPayServer.Data.InvoiceData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.USDC.Tests;

[Collection("USDC PostgreSQL fixtures")]
public class ReservationLifecycleTests
{
    [Theory]
    [InlineData(InvoiceStatus.New, -1, true)]
    [InlineData(InvoiceStatus.Processing, -1, true)]
    [InlineData(InvoiceStatus.Expired, 1, true)]
    [InlineData(InvoiceStatus.Expired, 0, false)]
    [InlineData(InvoiceStatus.Expired, -1, false)]
    [InlineData(InvoiceStatus.Settled, 1, false)]
    [InlineData(InvoiceStatus.Invalid, 1, false)]
    public void LifecycleBoundaries(InvoiceStatus status, int seconds, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, USDCAddressAllocator.ShouldReserve(status, now.AddSeconds(seconds), now));
    }

    [DatabaseFact]
    public async Task SavedDeadlinesSurviveRestartAndReleasedAssignmentHistoryCannotBeBypassed()
    {
        var factory = DatabaseTests.Factory();
        await using (var context = factory.CreateContext()) await context.Database.MigrateAsync();
        var pmi = new PaymentMethodId("USDC-AMOY");
        var storeId = Guid.NewGuid().ToString("N");
        var invoiceId = Guid.NewGuid().ToString("N");
        var address = "0x" + Guid.NewGuid().ToString("N") + "abcdefab";
        var created = DateTimeOffset.UtcNow.AddDays(-60);
        var savedDeadline = DateTimeOffset.UtcNow.AddHours(1);
        await using (var context = factory.CreateContext())
        {
            context.Stores.Add(new StoreData { Id = storeId, StoreName = "Reservation fixture" });
            var invoice = new InvoiceData { Id = invoiceId, StoreDataId = storeId, Status = "Expired" };
            invoice.SetBlob(new InvoiceEntity { Id = invoiceId, StoreId = storeId, Currency = "USDC", Price = 1,
                InvoiceTime = created, ExpirationTime = created.AddMinutes(15), MonitoringExpiration = savedDeadline });
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync();
        }
        var allocator = new USDCAddressAllocator(factory);
        Assert.Equal(address, await allocator.Allocate(pmi, storeId, invoiceId, [address], created, savedDeadline));
        var restarted = new USDCAddressAllocator(DatabaseTests.Factory());
        // Removing/re-adding the pool (including case variants) cannot bypass a
        // live reservation. A retry with different current settings stays stable.
        Assert.Null(await restarted.Allocate(pmi, "another-store", Guid.NewGuid().ToString("N"), [address.ToUpperInvariant().Replace("0X", "0x")]));
        Assert.Equal(address, await restarted.Allocate(pmi, storeId, invoiceId, [], created, created.AddMinutes(1)));
        Assert.Single(await restarted.GetUsedAddresses(pmi, [address]));
        await using (var context = factory.CreateContext())
        {
            var row = await context.Invoices.SingleAsync(i => i.Id == invoiceId);
            var blob = row.GetBlob();
            Assert.Equal(savedDeadline.ToUnixTimeSeconds(), blob.MonitoringExpiration.ToUnixTimeSeconds());
            blob.MonitoringExpiration = DateTimeOffset.UtcNow.AddSeconds(-1);
            row.SetBlob(blob);
            await context.SaveChangesAsync();
        }
        Assert.Empty(await restarted.GetUsedAddresses(pmi, [address]));
        var nextId = Guid.NewGuid().ToString("N");
        Assert.Equal(address, await restarted.Allocate(pmi, "another-store", nextId, [address]));
        Assert.Equal(address, await restarted.Allocate(pmi, storeId, invoiceId, []));
        Assert.Equal(nextId, (await restarted.CurrentClaim(pmi, address))!.InvoiceId);
        await using (var context = factory.CreateContext())
            Assert.True(await context.Settings.AnyAsync(s => s.Id == $"USDC.InvoiceAddress.{pmi}.{invoiceId}"));
    }
}
