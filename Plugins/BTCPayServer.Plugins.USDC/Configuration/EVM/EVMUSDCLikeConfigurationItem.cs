using System;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Configuration.EVM;

public record EVMUSDCLikeConfigurationItem(string Chain)
{
    public required Uri JsonRpcUri { get; init; }
    public required string SmartContractAddress { get; init; }
    public required string Currency { get; init; }
    public required string DisplayName { get; init; }
    public required int Divisibility { get; init; }
    public required string CryptoImagePath { get; init; }
    public required string BlockExplorerLink { get; init; }
    public required string[] DefaultRateRules { get; init; }
    public required string CurrencyDisplayName { get; init; }

    /// <summary>
    /// Average block time in seconds. Used to compute the sync lag threshold.
    /// Ethereum mainnet averages approximately 12 seconds.
    /// </summary>
    public double BlockTimeSeconds { get; init; } = 12.0;

    /// <summary>
    /// EIP-155 chain ID. Used in EIP-681 payment links so wallets select the correct network.
    /// Ethereum mainnet = 1, Sepolia = 11155111.
    /// </summary>
    public int ChainId { get; init; } = 1;

    public PaymentMethodId GetPaymentMethodId() => new($"{Currency}-{Chain}".ToUpperInvariant());
    public string GetSettingPrefix() => $"{Currency}_{Chain}".ToUpperInvariant();
}
