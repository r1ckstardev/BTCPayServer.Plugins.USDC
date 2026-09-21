using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.USDC.Tests;

// Set USDC_TEST_POSTGRES to a disposable PostgreSQL database, never a real store DB.
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1) : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USDC_TEST_POSTGRES")))
            Skip = "Set USDC_TEST_POSTGRES to run PostgreSQL integration tests.";
    }
}

public class DatabaseTests
{
    [DatabaseFact]
    public async Task CheckpointsAndHaltsRemainFreshAcrossIndependentWorkers()
    {
        var factory = Factory();
        await using (var context = factory.CreateContext()) await context.Database.MigrateAsync();
        var first = new USDCChainStateStore(factory);
        var second = new USDCChainStateStore(Factory());
        var key = "USDC.Test.Checkpoint." + Guid.NewGuid().ToString("N");
        Assert.Null(await second.Get(key));
        await first.Save(key, new EVMBasedListenerState { LastBlockHeight = 100 });
        Assert.Equal(100, (long)(await second.Get(key))!.LastBlockHeight);
        await first.Save(key, new EVMBasedListenerState { LastBlockHeight = 101, HaltReason = "Finalized history conflict" });
        Assert.Equal(101, (long)(await second.Get(key))!.LastBlockHeight);
        Assert.Equal("Finalized history conflict", (await second.Get(key))!.HaltReason);
        await second.Save(key, new EVMBasedListenerState { LastBlockHeight = 102 });
        Assert.Equal(102, (long)(await first.Get(key))!.LastBlockHeight);
    }

    internal static ApplicationDbContextFactory Factory() => new(
        Options.Create(new DatabaseOptions { ConnectionString = Environment.GetEnvironmentVariable("USDC_TEST_POSTGRES")! }),
        NullLoggerFactory.Instance);

    [DatabaseFact]
    public async Task ConcurrentClaimsAreAtomicAcrossInstancesStoresAndRestarts()
    {
        var factory = Factory();
        await using (var context = factory.CreateContext()) await context.Database.MigrateAsync();
        var pmi = new PaymentMethodId("USDC-SEPOLIA");
        var seed = Guid.NewGuid().ToString("N")[..24];
        var addresses = Enumerable.Range(1, 16).Select(i => "0x" + seed + i.ToString("x16")).ToArray();
        var requests = Enumerable.Range(0, 32).Select(i => new USDCAddressAllocator(factory).Allocate(
            pmi, "store-" + (i % 2), Guid.NewGuid().ToString("N"), addresses));
        var results = await Task.WhenAll(requests);
        var assigned = results.Where(a => a is not null).ToArray();
        Assert.Equal(addresses.Length, assigned.Length);
        Assert.Equal(assigned.Length, assigned.Distinct().Count());

        // A new instance and store cannot reclaim a retired address, including case variants.
        var restarted = new USDCAddressAllocator(Factory());
        var uppercase = addresses.Select(a => "0x" + a[2..].ToUpperInvariant()).ToArray();
        Assert.All(uppercase, a => Assert.True(EVMAddressHelper.IsValid(a)));
        Assert.Null(await restarted.Allocate(pmi, "third-store", Guid.NewGuid().ToString("N"), uppercase));
        Assert.Equal(addresses.Length, (await restarted.GetUsedAddresses(pmi, addresses)).Count);
    }

    [DatabaseFact]
    public async Task SameInvoiceRetryIsIdempotentAndCannotCrossStores()
    {
        var factory = Factory();
        await using (var context = factory.CreateContext()) await context.Database.MigrateAsync();
        var pmi = new PaymentMethodId("USDC-SEPOLIA");
        var invoice = Guid.NewGuid().ToString("N");
        var addresses = Enumerable.Range(1, 4).Select(i => "0x" + invoice + i.ToString("x8")).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => new USDCAddressAllocator(factory)
            .Allocate(pmi, "store-a", invoice, i % 2 == 0 ? addresses : addresses.Reverse())));
        Assert.NotNull(results[0]);
        Assert.Single(results.Distinct());
        var allocator = new USDCAddressAllocator(Factory());
        Assert.Single(await allocator.GetUsedAddresses(pmi, addresses));
        Assert.Equal(results[0], await allocator.Allocate(pmi, "store-a", invoice, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => allocator.Allocate(pmi, "store-b", invoice, addresses));
    }
}
