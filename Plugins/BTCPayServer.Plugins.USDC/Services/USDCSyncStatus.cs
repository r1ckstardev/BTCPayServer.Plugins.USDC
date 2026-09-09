using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.USDC.Services;

public class USDCSyncStatus : SyncStatus, ISyncStatus
{
    public required USDCRpcSummary Summary { get; init; }

    public override bool Available => Summary.RpcAvailable;
}