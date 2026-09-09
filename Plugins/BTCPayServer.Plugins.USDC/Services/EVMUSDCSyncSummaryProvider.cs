using System.Collections.Generic;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCSyncSummaryProvider(EVMUSDCRPCProvider rpcProvider) : USDCSyncSummaryProvider
{
    public override string Partial => "EVMUSDCLike/EVMUSDCSyncSummary";

    protected override IEnumerable<KeyValuePair<PaymentMethodId, USDCRpcSummary>> GetSummaries()
    {
        return rpcProvider.Summaries;
    }
}