using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Nethereum.Contracts;
using Nethereum.Contracts.Standards.ERC20.ContractDefinition;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCListener(
    ISettingsRepository settingsRepository,
    EventAggregator eventAggregator,
    EVMUSDCRPCProvider rpcProvider,
    USDCPluginConfiguration usdtPluginConfiguration,
    USDCChainActivationService activationService,
    USDCTrackedInvoiceProvider trackedInvoiceProvider,
    ILogger<EVMUSDCListener> logger,
    PaymentMethodHandlerDictionary handlers,
    PaymentService paymentService)
    : USDCListener<EVMUSDCLikeConfigurationItem, EVMUSDCPaymentData>(
        settingsRepository,
        eventAggregator,
        rpcProvider,
        activationService,
        trackedInvoiceProvider,
        logger,
        handlers,
        paymentService)
{
    internal const int DestinationFilterBatchSize = 20;

    private readonly ILogger<EVMUSDCListener> _logger = logger;

    protected override IReadOnlyDictionary<PaymentMethodId, EVMUSDCLikeConfigurationItem> GetConfigurations()
    {
        return usdtPluginConfiguration.EVMUSDCLikeConfigurationItems
            .Where(pair => pair.Value.HasValidSmartContractAddress())
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    protected override string GetListenerStateSettingKey(EVMUSDCLikeConfigurationItem config)
    {
        return EVMUSDCRPCProvider.ListenerStateSettingKey(config);
    }

    protected override string RateLimitNodeName => "EVM";

    protected override bool UseExponentialRateLimitBackoff => true;

    protected override long GetHeadLagBlocks(EVMUSDCLikeConfigurationItem configurationItem)
    {
        return 1;
    }

    protected override IDisposable? BeginLoggingScope(PaymentMethodId paymentMethodId)
    {
        var chain = usdtPluginConfiguration.EVMUSDCLikeConfigurationItems.TryGetValue(paymentMethodId, out var configuration)
            ? configuration.Chain
            : paymentMethodId.ToString();

        return _logger.BeginScope("EVM PMI: {PaymentMethodId}, Chain: {Chain}", paymentMethodId, chain);
    }

    protected override async Task<IReadOnlyCollection<USDCTransferMatch>> GetTransfersAsync(
        PaymentMethodId paymentMethodId,
        BlockWithTransactions block,
        IReadOnlyDictionary<string, InvoiceEntity> invoicesPerAddress,
        CancellationToken stoppingToken)
    {
        var configuration = usdtPluginConfiguration.EVMUSDCLikeConfigurationItems[paymentMethodId];
        if (!configuration.HasValidSmartContractAddress())
            return [];

        var web3Client = rpcProvider.GetWeb3Client(paymentMethodId);
        await rpcProvider.ValidateNetworkAsync(paymentMethodId, web3Client);
        var transferEvent = web3Client.Eth.GetEvent<TransferEventDTO>(configuration.SmartContractAddress.ToLowerInvariant());
        List<EventLog<TransferEventDTO>> changes = [];
        var destinationBatches = BatchDestinationAddresses(invoicesPerAddress.Keys);
        var tries = 0;
        do
        {
            changes.Clear();
            foreach (var destinationBatch in destinationBatches)
            {
                var filter = transferEvent.CreateFilterInput(
                    (object[]?)null,
                    (object[])destinationBatch,
                    new BlockParameter(block.Number),
                    new BlockParameter(block.Number));
                List<EventLog<TransferEventDTO>> part;
                try
                {
                    part = await transferEvent.GetAllChangesAsync(filter);
                }
                catch (RpcResponseException e) when (IsBlockRangeBeyondCurrentHeadError(e))
                {
                    throw new USDCListenerTransientException(
                        $"eth_getLogs is not ready for {paymentMethodId} block {(long)block.Number.Value}",
                        e);
                }

                if (part != null && part.Count != 0)
                    changes.AddRange(part);
            }

            if (changes.Count != 0)
                break;

            await Task.Delay(250, stoppingToken);
        } while (tries++ < 3);

        return ToTransferMatchSnapshots(
                changes.Where(change =>
                        string.Equals(change.Log.Address, configuration.SmartContractAddress, StringComparison.OrdinalIgnoreCase) &&
                        change.Log.BlockNumber?.Value == block.Number.Value &&
                        string.Equals(change.Log.BlockHash, block.BlockHash, StringComparison.OrdinalIgnoreCase) &&
                        change.Log.LogIndex is not null && change.Log.TransactionHash is not null)
                    .Select(change => new TransferLogSnapshot(
                    change.Event.To,
                    change.Event.From,
                    change.Event.Value,
                    change.Log.TransactionHash,
                    change.Log.LogIndex?.ToString() ?? string.Empty,
                    change.Log.Removed)),
                invoicesPerAddress.Keys)
            .Select(match => new USDCTransferMatch(
                match.DestinationKey,
                match.From,
                match.To,
                match.TotalAmount,
                    $"{match.TransactionId}-USDC-{configuration.ChainId}"))
            .ToArray();
    }

    internal static IReadOnlyList<string[]> BatchDestinationAddresses(
        IEnumerable<string> destinationKeys,
        int batchSize = DestinationFilterBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return destinationKeys
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Chunk(batchSize)
            .ToArray();
    }

    internal static IReadOnlyCollection<TransferMatchSnapshot> ToTransferMatchSnapshots(
        IEnumerable<TransferLogSnapshot> changes,
        IEnumerable<string> destinationKeys)
    {
        var destinationKeySet = destinationKeys
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return changes
            .Where(change => !change.Removed && change.Value > 0)
            .Select(change => new TransferMatchSnapshot(
                change.To.ToLowerInvariant(),
                change.From,
                change.To,
                change.Value,
                $"{change.TransactionHash.Replace("0x", "")}-{change.LogIndex}"))
            .Where(match => destinationKeySet.Contains(match.DestinationKey))
            .DistinctBy(match => match.TransactionId)
            .ToArray();
    }

    internal static bool IsBlockRangeBeyondCurrentHeadError(Exception exception)
    {
        return exception.Message.Contains("block range extends beyond current head block",
                   StringComparison.OrdinalIgnoreCase) &&
               exception.Message.Contains("eth_getLogs", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record TransferLogSnapshot(
        string To,
        string From,
        BigInteger Value,
        string TransactionHash,
        string LogIndex,
        bool Removed);

    internal sealed record TransferMatchSnapshot(
        string DestinationKey,
        string From,
        string To,
        BigInteger TotalAmount,
        string TransactionId);

    protected override EVMUSDCPaymentData CreatePaymentDetails(
        string from,
        string to,
        string transactionId,
        int confirmations,
        long blockHeight)
    {
        return new EVMUSDCPaymentData
        {
            To = to,
            From = from,
            TransactionId = transactionId,
            ConfirmationCount = confirmations,
            BlockHeight = blockHeight
        };
    }
}
