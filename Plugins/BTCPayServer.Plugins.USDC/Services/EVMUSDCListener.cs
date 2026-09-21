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
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCListener(
    USDCChainStateStore chainState,
    EventAggregator eventAggregator,
    EVMUSDCRPCProvider rpcProvider,
    USDCPluginConfiguration usdtPluginConfiguration,
    USDCChainActivationService activationService,
    USDCTrackedInvoiceProvider trackedInvoiceProvider,
    ILogger<EVMUSDCListener> logger,
    PaymentMethodHandlerDictionary handlers,
    PaymentService paymentService,
    USDCChainRecovery recovery = null!)
    : USDCListener<EVMUSDCLikeConfigurationItem, EVMUSDCPaymentData>(
        chainState,
        eventAggregator,
        rpcProvider,
        activationService,
        trackedInvoiceProvider,
        logger,
        handlers,
        paymentService,
        recovery)
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
        return configurationItem.ChainId is 137 or 80002 ? 2 : 1;
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

        Dictionary<string, USDCTransferMatch> matches = new(StringComparer.OrdinalIgnoreCase);
        foreach (var transaction in changes.GroupBy(c => c.Log.TransactionHash, StringComparer.OrdinalIgnoreCase))
        {
            var receipt = await web3Client.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(transaction.Key);
            if (receipt?.Status?.Value != 1 || receipt.BlockNumber?.Value != block.Number.Value ||
                !USDCChainRecovery.Same(receipt.BlockHash, block.BlockHash) ||
                !USDCChainRecovery.Same(receipt.TransactionHash, transaction.Key))
                throw new InvalidOperationException("RPC receipt conflicts with the indexed block.");
            var receiptLogs = receipt.Logs ?? [];
            if (receiptLogs.Any(l => l.LogIndex is null) || receiptLogs.Select(l => l.LogIndex.Value).Distinct().Count() != receiptLogs.Length)
                throw new InvalidOperationException("Receipt contains missing or duplicate log positions.");
            if (receiptLogs.Any(l => l.Removed || l.BlockNumber?.Value != block.Number.Value ||
                    !USDCChainRecovery.Same(l.BlockHash, block.BlockHash) || !USDCChainRecovery.Same(l.TransactionHash, transaction.Key)) ||
                !receiptLogs.Select(l => l.LogIndex.Value).SequenceEqual(receiptLogs.Select(l => l.LogIndex.Value).Order()))
                throw new InvalidOperationException("Receipt log ancestry or ordering is inconsistent.");
            foreach (var change in transaction)
            {
                var log = change.Log;
                if (log.Removed || log.BlockNumber?.Value != block.Number.Value ||
                    !USDCChainRecovery.Same(log.BlockHash, block.BlockHash) ||
                    !USDCChainRecovery.Same(log.Address, configuration.SmartContractAddress) || log.LogIndex is null)
                    throw new InvalidOperationException("RPC returned a conflicting transfer log.");
                var position = Array.FindIndex(receiptLogs, l => l.LogIndex.Value == log.LogIndex.Value);
                if (position < 0 || !SameLog(receiptLogs[position], log))
                    throw new InvalidOperationException("Transfer log is not present verbatim in its transaction receipt.");
                if (change.Event.Value <= 0 || !invoicesPerAddress.ContainsKey(change.Event.To.ToLowerInvariant())) continue;
                // Receipt-relative ordinal is stable if the transaction is
                // re-included elsewhere, unlike block-global logIndex.
                var id = $"{transaction.Key.ToLowerInvariant().Replace("0x", "")}-{position}-USDC-{configuration.ChainId}-{configuration.SmartContractAddress.ToLowerInvariant()}";
                var match = new USDCTransferMatch(change.Event.To.ToLowerInvariant(), change.Event.From.ToLowerInvariant(),
                    change.Event.To.ToLowerInvariant(), change.Event.Value, id);
                if (matches.TryGetValue(id, out var previous) && previous != match)
                    throw new USDCFinalityConflictException($"Conflicting duplicate transfer {id}.");
                matches[id] = match;
            }
        }
        return matches.Values.ToArray();
    }

    internal static bool SameLog(FilterLog left, FilterLog right) =>
        !left.Removed && left.BlockNumber?.Value == right.BlockNumber?.Value &&
        left.TransactionIndex?.Value == right.TransactionIndex?.Value &&
        USDCChainRecovery.Same(left.Address, right.Address) &&
        USDCChainRecovery.Same(left.BlockHash, right.BlockHash) &&
        USDCChainRecovery.Same(left.TransactionHash, right.TransactionHash) &&
        USDCChainRecovery.Same(left.Data, right.Data) && left.Topics.Length == right.Topics.Length &&
        left.Topics.Zip(right.Topics).All(pair => USDCChainRecovery.Same(pair.First?.ToString(), pair.Second?.ToString()));

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

        var matches = changes
            .Where(change => !change.Removed && change.Value > 0)
            .Select(change => new TransferMatchSnapshot(
                change.To.ToLowerInvariant(),
                change.From,
                change.To,
                change.Value,
                $"{change.TransactionHash.Replace("0x", "")}-{change.LogIndex}"))
            .Where(match => destinationKeySet.Contains(match.DestinationKey))
            .ToArray();
        foreach (var group in matches.GroupBy(m => m.TransactionId))
            if (group.Distinct().Count() != 1) throw new InvalidOperationException("Conflicting duplicate transfer log.");
        return matches.DistinctBy(match => match.TransactionId).ToArray();
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
