using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace BTCPayServer.Plugins.USDC.Services;

public abstract class USDCListener<TConfigurationItem, TPaymentData>(
    ISettingsRepository settingsRepository,
    EventAggregator eventAggregator,
    USDCRPCProvider<TConfigurationItem> rpcProvider,
    USDCChainActivationService activationService,
    USDCTrackedInvoiceProvider trackedInvoiceProvider,
    ILogger logger,
    PaymentMethodHandlerDictionary handlers,
    PaymentService paymentService) : IHostedService
    where TConfigurationItem : USDCPluginConfigurationItem, IUSDCRpcConfigurationItem
    where TPaymentData : USDCPaymentData
{
    private CancellationTokenSource? _cts;
    private Task _workers = Task.CompletedTask;

    protected abstract IReadOnlyDictionary<PaymentMethodId, TConfigurationItem> GetConfigurations();
    protected abstract string GetListenerStateSettingKey(TConfigurationItem config);
    protected abstract Task<IReadOnlyCollection<USDCTransferMatch>> GetTransfersAsync(
        PaymentMethodId paymentMethodId,
        BlockWithTransactions block,
        IReadOnlyDictionary<string, InvoiceEntity> invoicesPerAddress,
        CancellationToken stoppingToken);
    protected abstract TPaymentData CreatePaymentDetails(
        string from,
        string to,
        string transactionId,
        int confirmations,
        long blockHeight);

    protected abstract string RateLimitNodeName { get; }

    protected virtual bool UseExponentialRateLimitBackoff => false;
    protected virtual long CreateInitialLastBlockHeight(HexBigInteger latestBlockNumber) => (long)latestBlockNumber.Value - 1;
    protected virtual long GetHeadLagBlocks(TConfigurationItem configurationItem) => 0;
    protected virtual TimeSpan GetHeadPollingDelay(TConfigurationItem configurationItem) => TimeSpan.FromSeconds(1);
    protected virtual LogLevel EmptyQueueBlockAdvanceLogLevel => LogLevel.Information;
    protected virtual IDisposable? BeginLoggingScope(PaymentMethodId paymentMethodId) => null;
    protected virtual string NormalizeDestinationKey(string destination) => destination.ToLowerInvariant();
    protected virtual string GetLogContext(TConfigurationItem configurationItem)
    {
        return $"{configurationItem.GetPaymentMethodId()} ({configurationItem.Chain})";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configurations = GetConfigurations();
        if (configurations.Count == 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = Task.WhenAll(configurations.Values.Select(configuration => LoopIndex(configuration, _cts.Token)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { await _workers.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (_workers.IsCompleted)
            {
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private async Task LoopIndex(TConfigurationItem configurationItem, CancellationToken stoppingToken)
    {
        var paymentMethodId = configurationItem.GetPaymentMethodId();
        var logContext = GetLogContext(configurationItem);
        var rateLimitBackoffMs = USDCListenerShared.InitialRateLimitBackoffMs;
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                using var _ = BeginLoggingScope(paymentMethodId);
                if (!await activationService.IsActivatedAsync(paymentMethodId, stoppingToken))
                {
                    logger.LogDebug(
                        "Skipping indexing for {Listener}; no store has activated this payment method yet",
                        logContext);
                    await Task.Delay(30_000, stoppingToken);
                    continue;
                }

                // Do not initialize or advance a durable cursor from a different chain.
                var web3Client = rpcProvider.GetWeb3Client(paymentMethodId);
                await rpcProvider.ValidateNetworkAsync(paymentMethodId, web3Client);
                var listenerState = await LoadTrackingState(configurationItem);
                var latestBlockNumber = await EVMUSDCRPCProvider.GetFinalizedHeight(web3Client);
                if (listenerState == null)
                {
                    logger.LogInformation("No tracking state found for {Listener}, new blockchain",
                        logContext);

                    listenerState = new EVMBasedListenerState
                    {
                        LastBlockHeight = CreateInitialLastBlockHeight(latestBlockNumber)
                    };
                    await SetTrackingState(configurationItem, listenerState);
                }
                else
                {
                    logger.LogInformation(
                        "Tracking state for {Listener}, current={CurrentBlockNumber}, latest={LatestBlockNumber}",
                        logContext, listenerState.LastBlockHeight, latestBlockNumber);
                }

                var safeHeadLagBlocks = GetHeadLagBlocks(configurationItem);
                var latestSafeBlockHeight = Math.Max(-1, (long)latestBlockNumber.Value - safeHeadLagBlocks);
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Server configuration can replace the RPC client without restarting BTCPay.
                    if (!ReferenceEquals(web3Client.Client, rpcProvider.GetWeb3Client(paymentMethodId).Client))
                        break;
                    var invoices = (await trackedInvoiceProvider.GetTrackedInvoices(paymentMethodId, stoppingToken))
                        .Where(invoice => invoice.GetPaymentPrompt(paymentMethodId)?.Activated is true)
                        .ToArray();
                    if (invoices.Length == 0)
                    {
                        var lastBlockNumber = await EVMUSDCRPCProvider.GetFinalizedHeight(web3Client);
                        // The head request may overlap invoice creation. Recheck after observing the
                        // head so a newly exposed destination is scanned before advancing past it.
                        if ((await trackedInvoiceProvider.GetTrackedInvoices(paymentMethodId, stoppingToken))
                            .Any(invoice => invoice.GetPaymentPrompt(paymentMethodId)?.Activated is true))
                            continue;
                        if (lastBlockNumber.Value > listenerState.LastBlockHeight)
                        {
                            logger.Log(EmptyQueueBlockAdvanceLogLevel,
                                "New block avoid for {PaymentMethodId} from {BlockNumber} to {NewBlockNumber}",
                                paymentMethodId, listenerState.LastBlockHeight, lastBlockNumber.Value);
                            listenerState.LastBlockHeight = (long)lastBlockNumber.Value;
                        }

                        await Task.Delay(30_000, stoppingToken);
                    }
                    else
                    {
                        var nextBlockHeight = listenerState.LastBlockHeight + 1;
                        if (nextBlockHeight > latestSafeBlockHeight)
                        {
                            latestBlockNumber = await EVMUSDCRPCProvider.GetFinalizedHeight(web3Client);
                            latestSafeBlockHeight = Math.Max(-1, (long)latestBlockNumber.Value - safeHeadLagBlocks);
                        }

                        if (nextBlockHeight > latestSafeBlockHeight)
                        {
                            logger.LogDebug(
                                "Waiting for a safe indexing head on {Listener}, next={NextBlockNumber}, latest={LatestBlockNumber}, lag={HeadLagBlocks}",
                                logContext, nextBlockHeight, latestBlockNumber.Value, safeHeadLagBlocks);
                            await Task.Delay(GetHeadPollingDelay(configurationItem), stoppingToken);
                            continue;
                        }

                        var block =
                            await web3Client.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(
                                new BlockParameter(new HexBigInteger(nextBlockHeight)));

                        if (block != null)
                        {
                            await OnNewBlockToIndex(paymentMethodId, invoices, block, stoppingToken);
                            // If settings changed during the request, replay this block using the new
                            // connection. Per-log payment IDs make already persisted transfers idempotent.
                            if (!ReferenceEquals(web3Client.Client, rpcProvider.GetWeb3Client(paymentMethodId).Client))
                                break;
                            logger.LogInformation("New block indexed for {Listener} {BlockNumber}",
                                logContext, block.Number);

                            listenerState.LastBlockHeight = (long)block.Number.Value;
                        }
                        else
                        {
                            logger.LogInformation(
                                "Block not present on node yet for {Listener} {BlockNumber}",
                                logContext, listenerState.LastBlockHeight);
                            await Task.Delay(GetHeadPollingDelay(configurationItem), stoppingToken);
                        }
                    }

                    await SetTrackingState(configurationItem, listenerState);
                    rateLimitBackoffMs = USDCListenerShared.InitialRateLimitBackoffMs;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (USDCListenerTransientException e)
            {
                logger.LogInformation(e,
                    "Temporary indexing delay for {Listener}. Retrying in {DelayMs} ms",
                    logContext, e.RetryDelayMs);
                await Task.Delay(e.RetryDelayMs, stoppingToken);
            }
            catch (RpcClientTimeoutException)
            {
                logger.LogWarning(
                    "Timeout while indexing {Listener}, is the node running? Retrying in 10 seconds",
                    logContext);
                await Task.Delay(5_000, stoppingToken);
            }
            catch (RpcClientUnknownException e) when (USDCListenerShared.IsRateLimitException(e))
            {
                if (UseExponentialRateLimitBackoff)
                {
                    var retryDelayMs = USDCListenerShared.CalculateRateLimitDelayMs(
                        rateLimitBackoffMs,
                        Random.Shared.NextDouble());
                    logger.LogWarning(
                        "Rate limit exceeded while indexing {Listener}, use a {NodeName} node with higher limits if possible. Retrying in {DelayMs} ms",
                        logContext, RateLimitNodeName, retryDelayMs);
                    await Task.Delay(retryDelayMs, stoppingToken);
                    rateLimitBackoffMs = USDCListenerShared.GetNextRateLimitBackoffMs(rateLimitBackoffMs);
                }
                else
                {
                    logger.LogWarning(
                        "Rate limit exceeded while indexing {Listener}, use a {NodeName} node with higher limits if possible. Retrying in 10 seconds",
                        logContext, RateLimitNodeName);
                    await Task.Delay(10_000, stoppingToken);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "An error occurred while indexing {Listener}", logContext);
                await Task.Delay(10_000, stoppingToken);
            }
    }

    private async Task SetTrackingState(TConfigurationItem config, EVMBasedListenerState trackingState)
    {
        await settingsRepository.UpdateSetting(trackingState, GetListenerStateSettingKey(config));
    }

    private async Task<EVMBasedListenerState?> LoadTrackingState(TConfigurationItem config)
    {
        return await settingsRepository.GetSettingAsync<EVMBasedListenerState>(GetListenerStateSettingKey(config));
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation(
            "Invoice {InvoiceId} received payment {PaymentValue} {PaymentCurrency} {PaymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }

    private TConfigurationItem GetConfig(PaymentMethodId paymentMethodId)
    {
        return GetConfigurations()[paymentMethodId];
    }

    private async Task UpdatePaymentStates(
        PaymentMethodId paymentMethodId,
        InvoiceEntity[] invoices,
        BlockWithTransactions block,
        CancellationToken stoppingToken)
    {
        if (invoices.Length == 0)
            return;

        var invoicesPerAddress = invoices
            .Select(invoice => (Invoice: invoice, Destination: invoice.GetPaymentPrompt(paymentMethodId)?.Destination))
            .Where(tuple => !string.IsNullOrEmpty(tuple.Destination))
            .GroupBy(tuple => NormalizeDestinationKey(tuple.Destination!))
            .ToDictionary(group => group.Key, group => group.First().Invoice);

        var matches = await GetTransfersAsync(paymentMethodId, block, invoicesPerAddress, stoppingToken);
        var paymentTimestamp = GetPaymentTimestamp(block);
        foreach (var match in matches)
        {
            if (!invoicesPerAddress.TryGetValue(match.DestinationKey, out var invoice))
                continue;

            // Backlog scanning must not turn an address's older deposits into a new invoice payment.
            if (paymentTimestamp.ToUnixTimeSeconds() < invoice.InvoiceTime.ToUnixTimeSeconds())
                continue;

            await HandlePaymentData(
                paymentMethodId,
                match.From,
                match.To,
                match.TotalAmount,
                match.TransactionId,
                0,
                (long)block.Number.Value,
                paymentTimestamp,
                invoice);
        }

        await UpdatePendingConfirmations(paymentMethodId, invoices, block);
    }

    private async Task UpdatePendingConfirmations(
        PaymentMethodId paymentMethodId,
        InvoiceEntity[] invoices,
        BlockWithTransactions block)
    {
        var handler = handlers[paymentMethodId];
        List<(PaymentEntity Payment, InvoiceEntity Invoice)> updatedPaymentEntities = [];

        foreach (var invoice in invoices)
        foreach (var payment in GetPendingPayments(invoice, paymentMethodId))
        {
            var paymentData = (USDCPaymentData)handler.ParsePaymentDetails(payment.Details);
            paymentData.UpdateConfirmations(block.Number.Value);

            payment.Status = paymentData.PaymentConfirmed(invoice.SpeedPolicy)
                ? PaymentStatus.Settled
                : PaymentStatus.Processing;
            payment.SetDetails(handler, paymentData);

            updatedPaymentEntities.Add((payment, invoice));
        }

        await paymentService.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Payment).ToList());
        foreach (var group in updatedPaymentEntities.GroupBy(entity => entity.Invoice))
            if (group.Any())
                eventAggregator.Publish(new InvoiceNeedUpdateEvent(group.Key.Id));
    }

    private async Task OnNewBlockToIndex(
        PaymentMethodId paymentMethodId,
        InvoiceEntity[] invoices,
        BlockWithTransactions block,
        CancellationToken stoppingToken)
    {
        await UpdatePaymentStates(paymentMethodId, invoices, block, stoppingToken);
    }

    private async Task HandlePaymentData(
        PaymentMethodId paymentMethodId,
        string from,
        string to,
        BigInteger totalAmount,
        string txId,
        int confirmations,
        long blockHeight,
        DateTimeOffset paymentTimestamp,
        InvoiceEntity invoice)
    {
        var config = GetConfig(paymentMethodId);
        var handler = handlers[paymentMethodId];
        var totalAmountBigDecimal = USDCAmounts.FromBaseUnits(totalAmount, config.Divisibility);

        var details = CreatePaymentDetails(from, to, txId, confirmations, blockHeight);

        var paymentData = new PaymentData
        {
            Status = details.PaymentConfirmed(invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing,
            Amount = totalAmountBigDecimal,
            Created = paymentTimestamp,
            Id = txId,
            Currency = config.Currency,
            InvoiceDataId = invoice.Id
        }.Set(invoice, handler, details);

        var payment = await paymentService.AddPayment(paymentData, [txId]);
        if (payment != null)
            await ReceivedPayment(invoice, payment);
    }

    private DateTimeOffset GetPaymentTimestamp(BlockWithTransactions block)
    {
        if (USDCListenerShared.TryGetBlockTimestamp(block.Timestamp, out var timestamp))
            return timestamp;
        throw new InvalidOperationException($"Block {block.Number} did not contain a valid Unix timestamp.");
    }

    private static IEnumerable<PaymentEntity> GetPendingPayments(InvoiceEntity invoice, PaymentMethodId paymentMethodId)
    {
        return invoice.GetPayments(false)
            .Where(p => p.PaymentMethodId == paymentMethodId)
            .Where(p => p.Status == PaymentStatus.Processing);
    }

    protected sealed record USDCTransferMatch(
        string DestinationKey,
        string From,
        string To,
        BigInteger TotalAmount,
        string TransactionId);
}

internal sealed class USDCListenerTransientException(string message, Exception innerException, int retryDelayMs = 1_000)
    : Exception(message, innerException)
{
    public int RetryDelayMs { get; } = retryDelayMs;
}
