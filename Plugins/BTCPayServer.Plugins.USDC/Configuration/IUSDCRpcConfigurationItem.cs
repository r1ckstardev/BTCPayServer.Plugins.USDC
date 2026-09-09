using System;

namespace BTCPayServer.Plugins.USDC.Configuration;

public interface IUSDCRpcConfigurationItem
{
    Uri JsonRpcUri { get; }
    string SmartContractAddress { get; }
    int Divisibility { get; }
    double BlockTimeSeconds { get; }
}