using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethereum.Contracts;
using Nethereum.Contracts.Standards.ERC20.ContractDefinition;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCListener(
    USDCChainStateStore chainState,
    EVMUSDCRPCProvider rpcProvider,
    USDCPluginConfiguration pluginConfiguration,
    USDCChainActivationService activationService,
    USDCChainRecovery recovery,
    ILogger<EVMUSDCListener> logger) : IHostedService
{
    internal const int DestinationFilterBatchSize = 20;
    // Index one block behind the RPC head, so eth_getLogs can serve the block.
    private const long HeadLagBlocks = 1;
    private const int InitialRateLimitBackoffMs = 5_000;
    private const int MaximumRateLimitBackoffMs = 60_000;
    private CancellationTokenSource? _cts;
    private Task _workers = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = Task.WhenAll(pluginConfiguration.EVMUSDCLikeConfigurationItems.Values.Select(config => LoopIndex(config, _cts.Token)));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { await _workers.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally { if (_workers.IsCompleted) { _cts?.Dispose(); _cts = null; } }
    }

    private async Task LoopIndex(EVMUSDCLikeConfigurationItem config, CancellationToken token)
    {
        var pmi = config.GetPaymentMethodId();
        var stateKey = EVMUSDCRPCProvider.ListenerStateSettingKey(config);
        var backoff = InitialRateLimitBackoffMs;
        while (!token.IsCancellationRequested)
        {
            var delay = 1000;
            try
            {
                using var scope = logger.BeginScope("EVM PMI: {PaymentMethodId}, Chain: {Chain}", pmi, config.Chain);
                if (!await activationService.IsActivatedAsync(pmi, token) &&
                    await chainState.Get(stateKey) is null) delay = 30000;
                else
                {
                    var advanced = false;
                    await recovery.WithChainLock(pmi, async () =>
                    {
                        // Reload INSIDE the database lock: another host may have
                        // advanced the cursor since this worker's previous turn.
                        var state = await chainState.Get(stateKey);
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
                backoff = InitialRateLimitBackoffMs;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (RpcClientUnknownException e) when (IsRateLimitException(e))
            {
                delay = (int)Math.Min(MaximumRateLimitBackoffMs, backoff * (1 + Random.Shared.NextDouble() * 0.2));
                backoff = Math.Min(backoff * 2, MaximumRateLimitBackoffMs);
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

    private async Task<bool> IndexOne(EVMUSDCLikeConfigurationItem config, EVMBasedListenerState? state, CancellationToken token)
    {
        var pmi = config.GetPaymentMethodId();
        var client = rpcProvider.GetWeb3Client(pmi);
        await rpcProvider.ValidateNetworkAsync(pmi, client);
        var latest = (long)(await client.Eth.Blocks.GetBlockNumber.SendRequestAsync()).Value;
        var safeHead = Math.Max(0, latest - HeadLagBlocks);
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
            throw new InvalidOperationException("RPC ancestry changed during indexing; replay required.");
        var watched = await recovery.WatchedAddresses(pmi, token);
        watched.UnionWith(outstanding.Select(o => o.To.ToLowerInvariant()));
        var matches = watched.Count == 0 ? [] : await GetTransfersAsync(pmi, next, watched, token);
        if (!TryGetBlockTimestamp(next.Timestamp, out var timestamp))
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

    private async Task AdvanceFinality(EVMUSDCLikeConfigurationItem config, EVMBasedListenerState state,
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

    protected async Task<IReadOnlyCollection<USDCTransferMatch>> GetTransfersAsync(
        PaymentMethodId paymentMethodId,
        Block block,
        IReadOnlySet<string> addresses,
        CancellationToken stoppingToken)
    {
        var configuration = pluginConfiguration.EVMUSDCLikeConfigurationItems[paymentMethodId];
        var web3Client = rpcProvider.GetWeb3Client(paymentMethodId);
        await rpcProvider.ValidateNetworkAsync(paymentMethodId, web3Client);
        var transferEvent = web3Client.Eth.GetEvent<TransferEventDTO>(configuration.SmartContractAddress.ToLowerInvariant());
        List<EventLog<TransferEventDTO>> changes = [];
        var destinationBatches = BatchDestinationAddresses(addresses);
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
                    throw new InvalidOperationException(
                        $"eth_getLogs is not ready for {paymentMethodId} block {(long)block.Number.Value}", e);
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
                if (change.Event.Value <= 0 || !addresses.Contains(change.Event.To.ToLowerInvariant())) continue;
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

    internal static bool IsBlockRangeBeyondCurrentHeadError(Exception exception)
    {
        return exception.Message.Contains("block range extends beyond current head block",
                   StringComparison.OrdinalIgnoreCase) &&
               exception.Message.Contains("eth_getLogs", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRateLimitException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
                return true;

            var isForbidden = message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                              message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
            var describesRateLimit = message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                                     message.Contains("frequency limit", StringComparison.OrdinalIgnoreCase) ||
                                     message.Contains("quota", StringComparison.OrdinalIgnoreCase);
            if (isForbidden && describesRateLimit)
                return true;
        }

        return false;
    }

    private static bool TryGetBlockTimestamp(HexBigInteger? blockTimestamp, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (blockTimestamp?.Value is not { } value || value <= 0 || value > long.MaxValue)
            return false;

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds((long)value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
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
    private static USDCBlockRecord Record(Block block) => new(block.BlockHash, block.ParentHash);
    private static async Task<BlockWithTransactionHashes> GetBlock(Web3 client, long height)
    {
        // Only header fields are used; full transaction bodies are megabytes on mainnet.
        var block = await client.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(new BlockParameter(new HexBigInteger(height)));
        if (block?.Number?.Value != height || string.IsNullOrWhiteSpace(block.BlockHash) || string.IsNullOrWhiteSpace(block.ParentHash))
            throw new InvalidOperationException($"RPC did not return complete block {height}.");
        return block;
    }
    private void CheckClient(PaymentMethodId pmi, Web3 client)
    {
        if (!ReferenceEquals(client.Client, rpcProvider.GetWeb3Client(pmi).Client))
            throw new InvalidOperationException("RPC settings changed during indexing; replay using the new provider.");
    }
    private Task SaveState(EVMUSDCLikeConfigurationItem config, EVMBasedListenerState state) =>
        chainState.Save(EVMUSDCRPCProvider.ListenerStateSettingKey(config), state);

    protected sealed record USDCTransferMatch(string DestinationKey, string From, string To, BigInteger TotalAmount, string TransactionId);
}
