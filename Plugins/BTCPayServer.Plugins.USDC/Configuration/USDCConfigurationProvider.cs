using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.USDC.Configuration;

public static class USDCConfigurationProvider
{
    // Native USDC, not bridged USDC.e. Source: developers.circle.com/stablecoins/usdc-contract-addresses
    public const string MainnetContract = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
    public const string SepoliaContract = "0x1c7d4b196cb0c7b01d743fbc6116a902379c7238";

    public static Dictionary<PaymentMethodId, EVMUSDCLikeConfigurationItem> GetEVMUSDCLikeDefaultConfigurationItems(
        NBXplorerNetworkProvider networkProvider, IConfiguration configuration)
    {
        return new[] { GetDefault(networkProvider.NetworkType), GetPolygonDefault(networkProvider.NetworkType) }
            .Select(config => config with
            {
                JsonRpcUri = configuration.GetOrDefault($"{config.GetSettingPrefix()}_JSONRPC_URI", config.JsonRpcUri)
            }).ToDictionary(config => config.GetPaymentMethodId());
    }

    public static EVMUSDCLikeConfigurationItem GetEVMUSDCDefaultConfigurationItem(
        PaymentMethodId paymentMethodId, NBXplorerNetworkProvider networkProvider, IConfiguration configuration)
    {
        if (GetEVMUSDCLikeDefaultConfigurationItems(networkProvider, configuration).TryGetValue(paymentMethodId, out var config))
            return config;
        throw new NotSupportedException($"Unsupported USDC payment method {paymentMethodId}");
    }

    internal static EVMUSDCLikeConfigurationItem GetDefault(ChainName network)
    {
        var mainnet = network == ChainName.Mainnet;
        if (!mainnet && network != ChainName.Testnet && network != ChainName.Regtest)
            throw new NotSupportedException($"Unsupported network {network}");
        return new(mainnet ? Constants.EthereumChainName : Constants.SepoliaChainName)
        {
            Currency = "USDC",
            CurrencyDisplayName = "USDC",
            DisplayName = mainnet ? "USDC on Ethereum" : "USDC on Sepolia (testnet)",
            Divisibility = 6,
            SmartContractAddress = mainnet ? MainnetContract : SepoliaContract,
            ChainId = mainnet ? 1 : 11155111,
            JsonRpcUri = new Uri(mainnet ? "https://ethereum.publicnode.com" : "https://sepolia.publicnode.com"),
            BlockExplorerLink = mainnet ? "https://etherscan.io/tx/{0}" : "https://sepolia.etherscan.io/tx/{0}",
            CryptoImagePath = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Ccircle cx='32' cy='32' r='32' fill='%232775ca'/%3E%3Ctext x='32' y='38' text-anchor='middle' font-family='sans-serif' font-size='17' fill='white'%3EUSDC%3C/text%3E%3C/svg%3E",
            DefaultRateRules = ["USDC_USD = 1", "USDC_BTC = USD_BTC", "USDC_X = USDC_BTC * BTC_X"]
        };
    }

    public const string PolygonContract = "0x3c499c542cef5e3811e1192ce70d8cc03d5c3359";
    public const string AmoyContract = "0x41e94eb019c0762f9bfcf9fb1e58725bfb0e7582";

    internal static EVMUSDCLikeConfigurationItem GetPolygonDefault(ChainName network)
    {
        var ethereum = GetDefault(network); // Validates the host network.
        var mainnet = network == ChainName.Mainnet;
        return new(mainnet ? Constants.PolygonChainName : Constants.AmoyChainName)
        {
            Currency = ethereum.Currency, CurrencyDisplayName = ethereum.CurrencyDisplayName,
            DisplayName = mainnet ? "USDC on Polygon" : "USDC on Amoy (testnet)",
            Divisibility = 6, SmartContractAddress = mainnet ? PolygonContract : AmoyContract,
            ChainId = mainnet ? 137 : 80002, BlockTimeSeconds = 2,
            JsonRpcUri = new Uri(mainnet ? "https://polygon.drpc.org" : "https://polygon-amoy.drpc.org"),
            BlockExplorerLink = mainnet ? "https://polygonscan.com/tx/{0}" : "https://amoy.polygonscan.com/tx/{0}",
            CryptoImagePath = ethereum.CryptoImagePath, DefaultRateRules = ethereum.DefaultRateRules
        };
    }

    public static string ServerSettingsKey(EVMUSDCLikeConfigurationItem config) => $"{config.GetSettingPrefix()}_SERVER_SETTINGS";

    public static async Task<EVMUSDCLikeConfigurationItem> OverrideWithServerSettingsAsync(
        EVMUSDCLikeConfigurationItem config, ISettingsRepository settingsRepository)
    {
        var settings = await settingsRepository.GetSettingAsync<EVMUSDCLikeServerSettings>(ServerSettingsKey(config));
        return config with { JsonRpcUri = settings?.JsonRpcUri ?? config.JsonRpcUri };
    }
}
