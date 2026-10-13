using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCSyncSummaryProvider(EVMUSDCRPCProvider rpcProvider) : ISyncSummaryProvider
{
    public string Partial => "EVMUSDCLike/EVMUSDCSyncSummary";

    public bool AllAvailable()
    {
        return rpcProvider.Summaries.All(pair => pair.Value.RpcAvailable);
    }

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        return rpcProvider.Summaries.Select(pair => new USDCSyncStatus
        {
            Summary = pair.Value,
            PaymentMethodId = pair.Key.ToString()
        });
    }
}
