using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.USDC.Services;

// Chain checkpoints are coordination records, not process-cached configuration.
// Every worker must see the last committed state after acquiring the chain lock.
public sealed class USDCChainStateStore(ApplicationDbContextFactory factory)
{
    public async Task<EVMBasedListenerState?> Get(string key)
    {
        await using var context = factory.CreateContext();
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        return row is null ? null : JsonConvert.DeserializeObject<EVMBasedListenerState>(row.Value);
    }

    public async Task Save(string key, EVMBasedListenerState state)
    {
        await using var context = factory.CreateContext();
        var value = JsonConvert.SerializeObject(state);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Settings" ("Id", "Value") VALUES ({key}, CAST({value} AS jsonb))
            ON CONFLICT ("Id") DO UPDATE SET "Value" = EXCLUDED."Value"
            """);
    }
}
