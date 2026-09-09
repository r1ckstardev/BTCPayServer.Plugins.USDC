using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCLikeSummaryUpdaterHostedService(
    EVMUSDCRPCProvider rpcProvider,
    USDCPluginConfiguration usdtPluginConfiguration,
    Logs logs,
    USDCChainActivationService activationService)
    : USDCSummaryUpdaterHostedService(logs, activationService)
{
    protected override IEnumerable<PaymentMethodId> GetPaymentMethodIds()
    {
        return usdtPluginConfiguration.EVMUSDCLikeConfigurationItems.Keys;
    }

    protected override Task UpdateSummary(PaymentMethodId paymentMethodId)
    {
        return rpcProvider.UpdateSummary(paymentMethodId);
    }

    protected override bool IsAvailable(PaymentMethodId paymentMethodId)
    {
        return rpcProvider.IsAvailable(paymentMethodId);
    }

    protected override string DaemonName => "EVM USDCLike";
}
