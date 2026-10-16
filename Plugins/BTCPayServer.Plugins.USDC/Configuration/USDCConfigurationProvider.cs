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
    private static readonly string CryptoImage = LoadCryptoImage();

    private static string LoadCryptoImage()
    {
        using var icon = typeof(USDCConfigurationProvider).Assembly.GetManifestResourceStream("BTCPayServer.Plugins.USDC.usdc.svg")
            ?? throw new InvalidOperationException("The USDC logo resource is missing.");
        using var buffer = new System.IO.MemoryStream();
        icon.CopyTo(buffer);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(buffer.ToArray());
    }

    // Native USDC, not bridged USDC.e. Source: developers.circle.com/stablecoins/usdc-contract-addresses
    public const string MainnetContract = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
    public const string SepoliaContract = "0x1c7d4b196cb0c7b01d743fbc6116a902379c7238";

    public static Dictionary<PaymentMethodId, EVMUSDCLikeConfigurationItem> GetEVMUSDCLikeDefaultConfigurationItems(
        NBXplorerNetworkProvider networkProvider, IConfiguration configuration)
    {
        return new[] { GetDefault(networkProvider.NetworkType) }
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
        return new(mainnet ? "ETHEREUM" : "SEPOLIA")
        {
            Currency = "USDC",
            CurrencyDisplayName = "USDC",
            DisplayName = mainnet ? "USDC on Ethereum" : "USDC on Sepolia (testnet)",
            Divisibility = 6,
            SmartContractAddress = mainnet ? MainnetContract : SepoliaContract,
            ChainId = mainnet ? 1 : 11155111,
            JsonRpcUri = new Uri(mainnet ? "https://ethereum.publicnode.com" : "https://sepolia.publicnode.com"),
            BlockExplorerLink = mainnet ? "https://etherscan.io/tx/{0}" : "https://sepolia.etherscan.io/tx/{0}",
            CryptoImagePath = CryptoImage,
            DefaultRateRules = ["USDC_USD = 1", "USDC_BTC = USD_BTC", "USDC_X = USDC_BTC * BTC_X"]
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
