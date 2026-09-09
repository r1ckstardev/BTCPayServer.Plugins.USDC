using System.Collections.Generic;
using System.Net.Http;
using System;
using System.Threading.Tasks;
using Nethereum.Web3;
using Nethereum.Contracts;
using Nethereum.Contracts.Standards.ERC20.ContractDefinition;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCRPCProvider : USDCRPCProvider<EVMUSDCLikeConfigurationItem>
{
    private readonly USDCPluginConfiguration _usdtPluginConfiguration;

    public EVMUSDCRPCProvider(USDCPluginConfiguration usdtPluginConfiguration,
        EventAggregator eventAggregator,
        SettingsRepository settingsRepository,
        IHttpClientFactory httpClientFactory)
        : base(eventAggregator, settingsRepository, httpClientFactory)
    {
        _usdtPluginConfiguration = usdtPluginConfiguration;
        Initialize();
    }

    protected override IReadOnlyDictionary<PaymentMethodId, EVMUSDCLikeConfigurationItem> GetConfigurations()
    {
        return _usdtPluginConfiguration.EVMUSDCLikeConfigurationItems;
    }

    public override async Task ValidateNetworkAsync(PaymentMethodId pmi, Web3? client = null)
    {
        var config = GetConfigurations()[pmi];
        var web3 = client ?? GetWeb3Client(pmi);
        var chainId = await web3.Eth.ChainId.SendRequestAsync();
        if (chainId.Value != config.ChainId)
            throw new InvalidOperationException("The RPC endpoint is connected to the wrong USDC network.");
        var decimals = await web3.Eth.GetContractQueryHandler<DecimalsFunction>()
            .QueryAsync<byte>(config.SmartContractAddress, new DecimalsFunction());
        if (decimals != config.Divisibility)
            throw new InvalidOperationException("The USDC contract returned unexpected token decimals.");
    }

    internal static async Task<HexBigInteger> GetFinalizedHeight(Web3 web3)
    {
        var finalized = new BlockParameter();
        finalized.SetValue(BlockParameter.BlockParameterType.finalized);
        var block = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(finalized);
        return block?.Number ?? throw new InvalidOperationException("The RPC endpoint did not return a finalized block.");
    }

    public static string ListenerStateSettingKey(EVMUSDCLikeConfigurationItem config)
    {
        return $"{config.GetSettingPrefix()}_LISTENER_STATE";
    }

    protected override string GetListenerStateSettingKey(EVMUSDCLikeConfigurationItem config)
    {
        return ListenerStateSettingKey(config);
    }
}
