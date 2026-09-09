using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Services;

public abstract class USDCSyncSummaryProvider : ISyncSummaryProvider
{
    protected abstract IEnumerable<KeyValuePair<PaymentMethodId, USDCRpcSummary>> GetSummaries();

    public bool AllAvailable()
    {
        return GetSummaries().All(pair => pair.Value.RpcAvailable);
    }

    public abstract string Partial { get; }

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        return GetSummaries().Select(pair => new USDCSyncStatus
        {
            Summary = pair.Value,
            PaymentMethodId = pair.Key.ToString()
        });
    }
}