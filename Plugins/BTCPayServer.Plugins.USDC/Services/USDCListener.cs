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
    PaymentService paymentService,
    USDCChainRecovery recovery) : IHostedService
    where TConfigurationItem : USDCPluginConfigurationItem, IUSDCRpcConfigurationItem
    where TPaymentData : USDCPaymentData
{
    private CancellationTokenSource? _cts;
    private Task _workers = Task.CompletedTask;
    protected abstract IReadOnlyDictionary<PaymentMethodId, TConfigurationItem> GetConfigurations();
    protected abstract string GetListenerStateSettingKey(TConfigurationItem config);
    protected abstract Task<IReadOnlyCollection<USDCTransferMatch>> GetTransfersAsync(
        PaymentMethodId paymentMethodId, BlockWithTransactions block,
        IReadOnlyDictionary<string, InvoiceEntity> invoicesPerAddress, CancellationToken stoppingToken);
    protected abstract TPaymentData CreatePaymentDetails(string from, string to, string transactionId, int confirmations, long blockHeight);
    protected abstract string RateLimitNodeName { get; }
    protected virtual bool UseExponentialRateLimitBackoff => false;
    protected virtual long GetHeadLagBlocks(TConfigurationItem configurationItem) => 0;
    protected virtual IDisposable? BeginLoggingScope(PaymentMethodId paymentMethodId) => null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = Task.WhenAll(GetConfigurations().Values.Select(config => LoopIndex(config, _cts.Token)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { await _workers.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally { if (_workers.IsCompleted) { _cts?.Dispose(); _cts = null; } }
    }

    private async Task LoopIndex(TConfigurationItem config, CancellationToken token)
    {
        var pmi = config.GetPaymentMethodId();
        var backoff = USDCListenerShared.InitialRateLimitBackoffMs;
        while (!token.IsCancellationRequested)
        {
            var delay = 1000;
            try
            {
                using var scope = BeginLoggingScope(pmi);
                if (!await activationService.IsActivatedAsync(pmi, token) &&
                    await settingsRepository.GetSettingAsync<EVMBasedListenerState>(GetListenerStateSettingKey(config)) is null) delay = 30000;
                else
                {
                    var advanced = false;
                    await recovery.WithChainLock(pmi, async () =>
                    {
                        // Reload INSIDE the database lock: another host may have
                        // advanced the cursor since this worker's previous turn.
                        var state = await settingsRepository.GetSettingAsync<EVMBasedListenerState>(GetListenerStateSettingKey(config));
                        if (state?.HaltReason is not null) { delay = 30000; return; }
                        try { advanced = await IndexOne(config, state, token); }
                        catch (USDCFinalityConflictException e)
                        {
                            state ??= new EVMBasedListenerState();
                            state.HaltReason = e.Message;
                            await SaveState(config, state);
                            logger.LogCritical(e, "USDC indexing halted for {PaymentMethodId}: {Reason}", pmi, e.Message);
                            delay = 30000;
                        }
                    }, token);
                    if (advanced) delay = 0;
                }
                backoff = USDCListenerShared.InitialRateLimitBackoffMs;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (RpcClientUnknownException e) when (USDCListenerShared.IsRateLimitException(e))
            {
                delay = USDCListenerShared.CalculateRateLimitDelayMs(backoff, Random.Shared.NextDouble());
                backoff = USDCListenerShared.GetNextRateLimitBackoffMs(backoff);
                logger.LogWarning(e, "USDC {PaymentMethodId} RPC throttled; retrying in {Delay} ms", pmi, delay);
            }
            catch (Exception e)
            {
                delay = 5000;
                logger.LogError(e, "USDC {PaymentMethodId} indexing interrupted; durable checkpoint retained", pmi);
            }
            try { if (delay > 0) await Task.Delay(delay, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task<bool> IndexOne(TConfigurationItem config, EVMBasedListenerState? state, CancellationToken token)
    {
        var pmi = config.GetPaymentMethodId();
        var client = rpcProvider.GetWeb3Client(pmi);
        await rpcProvider.ValidateNetworkAsync(pmi, client);
        var latest = (long)(await client.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var safeHead = Math.Max(0, latest - GetHeadLagBlocks(config));
        var finalized = (long)(await EVMUSDCRPCProvider.GetFinalizedHeight(client)).Value;
        if (finalized > latest) throw new InvalidOperationException("RPC finalized head exceeds latest head.");
        if (state is null)
        {
            var initialHeight = Math.Max(0, safeHead - 1);
            var initial = await GetBlock(client, initialHeight);
            var anchorHeight = Math.Min(initialHeight, finalized);
            var anchor = await GetBlock(client, anchorHeight);
            state = new EVMBasedListenerState
            {
                LastBlockHeight = initialHeight, FinalizedHeight = anchorHeight, FinalizedHash = anchor.BlockHash,
                Blocks = { [initialHeight] = Record(initial), [anchorHeight] = Record(anchor) }
            };
            // Fill ancestry when finality trails the initial scan point. This is
            // needed to recover a reorg immediately after activation.
            for (var height = anchorHeight + 1; height < initialHeight; height++)
                state.Blocks[height] = Record(await GetBlock(client, height));
            ValidateAncestry(state);
            CheckClient(pmi, client);
            await SaveState(config, state);
        }
        if (state.Version != 2 || state.FinalizedHash is null || state.Blocks.Count == 0)
            throw new USDCFinalityConflictException("Prototype scan state has no recovery history. Use a fresh staging database; automatic migration/reset is disabled.");

        var anchorNow = await GetBlock(client, state.FinalizedHeight);
        if (!USDCChainRecovery.Same(anchorNow.BlockHash, state.FinalizedHash))
            throw new USDCFinalityConflictException($"Canonical history conflicts with finalized checkpoint {state.FinalizedHeight} ({state.FinalizedHash}). Manual reconciliation required.");
        var outstanding = await recovery.Outstanding(pmi);
        var cursor = (long)state.LastBlockHeight;
        var cursorBlock = await GetBlock(client, cursor);
        if (!state.Blocks.TryGetValue(cursor, out var cursorRecord) || !USDCChainRecovery.Same(cursorBlock.BlockHash, cursorRecord.Hash))
        {
            var ancestor = state.FinalizedHeight;
            foreach (var pair in state.Blocks.Reverse())
            {
                var canonical = await GetBlock(client, pair.Key);
                if (USDCChainRecovery.Same(pair.Value.Hash, canonical.BlockHash)) { ancestor = pair.Key; break; }
            }
            foreach (var observation in outstanding.Where(o => o.Height > ancestor && o.Canonical))
            {
                observation.Canonical = false;
                await recovery.Save(pmi, observation);
                await recovery.Reconcile(pmi, observation, ancestor);
            }
            foreach (var height in state.Blocks.Keys.Where(h => h > ancestor).ToArray()) state.Blocks.Remove(height);
            state.LastBlockHeight = cursor = ancestor;
            CheckClient(pmi, client);
            await SaveState(config, state);
        }
        // A crash can leave an observation beyond the saved cursor. Check it
        // before replay, including when the branch changed while we were down.
        foreach (var observation in outstanding)
        {
            if (observation.Canonical)
            {
                var block = await GetBlock(client, observation.Height);
                if (!USDCChainRecovery.Same(block.BlockHash, observation.BlockHash))
                {
                    observation.Canonical = false;
                    await recovery.Save(pmi, observation);
                }
            }
            await recovery.Reconcile(pmi, observation, cursor);
        }

        if (cursor >= safeHead) { await AdvanceFinality(config, state, outstanding, finalized, client); return false; }
        var next = await GetBlock(client, cursor + 1);
        if (!USDCChainRecovery.Same(next.ParentHash, state.Blocks[cursor].Hash))
            throw new InvalidOperationException("RPC ancestry changed during indexing; retrying from durable state.");
        var tracked = (await trackedInvoiceProvider.GetTrackedInvoices(pmi, token))
            .Where(i => i.GetPaymentPrompt(pmi)?.Activated is true).ToArray();
        var byAddress = tracked.GroupBy(i => i.GetPaymentPrompt(pmi)!.Destination.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.InvoiceTime).First());
        foreach (var observation in outstanding)
            byAddress.TryAdd(observation.To.ToLowerInvariant(), null!);
        var matches = byAddress.Count == 0 ? [] : await GetTransfersAsync(pmi, next, byAddress, token);
        if (!USDCListenerShared.TryGetBlockTimestamp(next.Timestamp, out var timestamp))
            throw new InvalidOperationException($"Block {next.Number} has an invalid timestamp.");
        foreach (var match in matches)
        {
            var observation = await recovery.Find(pmi, match.TransactionId);
            var amount = USDCAmounts.FromBaseUnits(match.TotalAmount, config.Divisibility);
            if (observation is null)
            {
                var invoice = await recovery.ResolveCurrentInvoice(pmi, match.DestinationKey, timestamp);
                if (invoice is null) continue;
                observation = new USDCChainRecovery.Observation
                {
                    Id = match.TransactionId, InvoiceId = invoice.Id, From = match.From, To = match.To,
                    Amount = amount, Timestamp = timestamp, Height = cursor + 1, BlockHash = next.BlockHash
                };
            }
            else
            {
                if (observation.Amount != amount || !USDCChainRecovery.Same(observation.To, match.To) ||
                    !USDCChainRecovery.Same(observation.From, match.From))
                    throw new USDCFinalityConflictException($"Conflicting transfer identity {match.TransactionId}.");
                if (observation.Finalized && (!USDCChainRecovery.Same(observation.BlockHash, next.BlockHash) || observation.Height != cursor + 1))
                    throw new USDCFinalityConflictException($"Finalized transfer {match.TransactionId} moved blocks.");
                observation.Height = cursor + 1;
                observation.BlockHash = next.BlockHash;
                observation.Canonical = true;
            }
            CheckClient(pmi, client);
            await recovery.Save(pmi, observation);
            await recovery.Reconcile(pmi, observation, cursor + 1);
        }
        // Update even settled invoices until finality, independently of the
        // invoice monitor cache or reservation release.
        outstanding = await recovery.Outstanding(pmi);
        foreach (var observation in outstanding) await recovery.Reconcile(pmi, observation, cursor + 1);
        var verifiedBlock = await GetBlock(client, cursor + 1);
        if (!USDCChainRecovery.Same(verifiedBlock.BlockHash, next.BlockHash))
            throw new InvalidOperationException("Block reorganized during payment writes; replay required.");
        CheckClient(pmi, client);
        state.Blocks[cursor + 1] = Record(next);
        state.LastBlockHeight = cursor + 1;
        await AdvanceFinality(config, state, outstanding, finalized, client);
        await SaveState(config, state);
        return true;
    }

    private async Task AdvanceFinality(TConfigurationItem config, EVMBasedListenerState state,
        USDCChainRecovery.Observation[] outstanding, long finalized, Web3 client)
    {
        var pmi = config.GetPaymentMethodId();
        var height = Math.Min(finalized, (long)state.LastBlockHeight);
        if (height > state.FinalizedHeight)
        {
            var canonical = await GetBlock(client, height);
            if (!state.Blocks.TryGetValue(height, out var record) || !USDCChainRecovery.Same(record.Hash, canonical.BlockHash))
                throw new InvalidOperationException("Finality advanced onto a different branch; replay required.");
            CheckClient(pmi, client);
            state.FinalizedHeight = height;
            state.FinalizedHash = canonical.BlockHash;
            foreach (var old in state.Blocks.Keys.Where(h => h < height).ToArray()) state.Blocks.Remove(old);
            // Save checkpoint before retiring observations, so a crash never
            // removes recovery coverage without a durable finality anchor.
            await SaveState(config, state);
        }
        foreach (var observation in outstanding.Where(o => o.Canonical && o.Height <= state.FinalizedHeight && state.LastBlockHeight - o.Height >= 20))
        {
            observation.Finalized = true;
            await recovery.Save(pmi, observation);
        }
    }

    private static void ValidateAncestry(EVMBasedListenerState state)
    {
        USDCBlockRecord? previous = null;
        foreach (var block in state.Blocks.Values)
        {
            if (previous is not null && !USDCChainRecovery.Same(block.ParentHash, previous.Hash))
                throw new InvalidOperationException("RPC returned conflicting initial ancestry.");
            previous = block;
        }
    }
    private static USDCBlockRecord Record(BlockWithTransactions block) => new(block.BlockHash, block.ParentHash);
    private static async Task<BlockWithTransactions> GetBlock(Web3 client, long height)
    {
        var block = await client.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(height)));
        if (block?.Number?.Value != height || string.IsNullOrWhiteSpace(block.BlockHash) || string.IsNullOrWhiteSpace(block.ParentHash))
            throw new InvalidOperationException($"RPC did not return complete block {height}.");
        return block;
    }
    private void CheckClient(PaymentMethodId pmi, Web3 client)
    {
        if (!ReferenceEquals(client.Client, rpcProvider.GetWeb3Client(pmi).Client))
            throw new InvalidOperationException("RPC settings changed during indexing; replay using the new provider.");
    }
    private Task SaveState(TConfigurationItem config, EVMBasedListenerState state) =>
        settingsRepository.UpdateSetting(state, GetListenerStateSettingKey(config));

    protected sealed record USDCTransferMatch(string DestinationKey, string From, string To, BigInteger TotalAmount, string TransactionId);
}

internal sealed class USDCListenerTransientException(string message, Exception innerException, int retryDelayMs = 1_000)
    : Exception(message, innerException)
{
    public int RetryDelayMs { get; } = retryDelayMs;
}
