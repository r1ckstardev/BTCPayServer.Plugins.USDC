using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Plugins.USDC.Services.Events;
using NBitcoin;
using Nethereum.Contracts.Standards.ERC20.ContractDefinition;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.StandardTokenEIP20;
using Nethereum.Web3;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCRPCProvider
{
    private readonly USDCPluginConfiguration _pluginConfiguration;
    private readonly EventAggregator _eventAggregator;
    private readonly USDCChainStateStore _chainState;
    private readonly IHttpClientFactory _httpClientFactory;
    private ImmutableDictionary<PaymentMethodId, RpcClient> _clients = ImmutableDictionary<PaymentMethodId, RpcClient>.Empty;

    public EVMUSDCRPCProvider(USDCPluginConfiguration pluginConfiguration,
        EventAggregator eventAggregator,
        USDCChainStateStore chainState,
        IHttpClientFactory httpClientFactory)
    {
        _pluginConfiguration = pluginConfiguration;
        _eventAggregator = eventAggregator;
        _chainState = chainState;
        _httpClientFactory = httpClientFactory;
        eventAggregator.Subscribe<USDCSettingsChanged>(_ => LoadClients());
        LoadClients();
    }

    public ConcurrentDictionary<PaymentMethodId, USDCRpcSummary> Summaries { get; } = new();

    private void LoadClients()
    {
        lock (this)
        {
            _clients = _pluginConfiguration.EVMUSDCLikeConfigurationItems.ToImmutableDictionary(
                pair => pair.Key,
                pair => new RpcClient(pair.Value.JsonRpcUri, _httpClientFactory.CreateClient()));
        }
    }

    public Web3 GetWeb3Client(PaymentMethodId pmi)
    {
        lock (this)
        {
            return new Web3(_clients[pmi]);
        }
    }

    public bool IsAvailable(PaymentMethodId pmi)
    {
        return Summaries.TryGetValue(pmi, out var summary) && IsAvailable(summary);
    }

    private static bool IsAvailable(USDCRpcSummary summary)
    {
        return summary is { Synced: true, RpcAvailable: true };
    }

    public async Task ValidateNetworkAsync(PaymentMethodId pmi, Web3? client = null)
    {
        var config = _pluginConfiguration.EVMUSDCLikeConfigurationItems[pmi];
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

    public async Task<(string, decimal?)[]> GetBalances(PaymentMethodId pmi, IEnumerable<string> addresses)
    {
        var configuration = _pluginConfiguration.EVMUSDCLikeConfigurationItems[pmi];
        var tokenService = new StandardTokenService(GetWeb3Client(pmi), configuration.SmartContractAddress);
        List<(string, decimal?)> results = [];
        foreach (var address in addresses)
            try
            {
                var balance = await tokenService.BalanceOfQueryAsync(address);
                results.Add((address, USDCAmounts.FromBaseUnits(balance, configuration.Divisibility)));
            }
            catch (Exception)
            {
                results.Add((address, null));
            }

        return results.ToArray();
    }

    public async Task UpdateSummary(PaymentMethodId pmi)
    {
        if (!_clients.ContainsKey(pmi))
            return;

        var configuration = _pluginConfiguration.EVMUSDCLikeConfigurationItems[pmi];
        var listenerState = await _chainState.Get(ListenerStateSettingKey(configuration));
        if (listenerState == null)
            return;

        var summary = new USDCRpcSummary();
        summary.Diagnostic = listenerState.HaltReason;
        try
        {
            var web3Client = GetWeb3Client(pmi);
            await ValidateNetworkAsync(pmi, web3Client);
            summary.LatestBlockScanned = listenerState.LastBlockHeight;
            var syncingOutput = await web3Client.Eth.Syncing.SendRequestAsync();
            if (syncingOutput.IsSyncing)
            {
                summary.LatestBlockOnNode = syncingOutput.CurrentBlock.Value;
                summary.HighestBlockOnChain = syncingOutput.HighestBlock.Value;
                summary.Syncing = true;
            }
            else
            {
                var latestBlock = await web3Client.Eth.Blocks.GetBlockNumber.SendRequestAsync();
                summary.LatestBlockOnNode = latestBlock;
                summary.HighestBlockOnChain = latestBlock;
                summary.Syncing = false;
            }

            summary.Synced = listenerState.HaltReason is null && summary.HighestBlockOnChain - listenerState.LastBlockHeight <
                             (int)(TimeSpan.FromMinutes(10).TotalSeconds / configuration.BlockTimeSeconds);

            summary.UpdatedAt = DateTime.UtcNow;
            summary.RpcAvailable = true;
        }
        catch
        {
            summary.RpcAvailable = false;
        }

        var changed = !Summaries.ContainsKey(pmi) || IsAvailable(pmi) != IsAvailable(summary);

        Summaries.AddOrReplace(pmi, summary);
        if (changed)
            _eventAggregator.Publish(new USDCDaemonStateChanged { Summary = summary, PaymentMethodId = pmi });
    }
}
