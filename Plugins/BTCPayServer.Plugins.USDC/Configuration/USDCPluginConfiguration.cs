using System.Collections.Generic;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration.EVM;

namespace BTCPayServer.Plugins.USDC.Configuration;

public class USDCPluginConfiguration
{
    public Dictionary<PaymentMethodId, EVMUSDCLikeConfigurationItem> EVMUSDCLikeConfigurationItems { get; init; } = new();
}
