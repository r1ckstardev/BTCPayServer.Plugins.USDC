using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services.Events;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.USDC.Services;

public class USDCChainActivationService : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly USDCPluginConfiguration _pluginConfiguration;
    private readonly IEventAggregatorSubscription[] _eventAggregatorSubscriptions;
    private readonly object _cacheLock = new();
    private HashSet<PaymentMethodId>? _activePaymentMethodIds;
    private DateTimeOffset _cacheExpiresAt;
    private long _cacheVersion;

    public USDCChainActivationService(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        USDCPluginConfiguration pluginConfiguration,
        EventAggregator eventAggregator)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _pluginConfiguration = pluginConfiguration;
        _eventAggregatorSubscriptions =
        [
            eventAggregator.Subscribe<USDCSettingsChanged>(_ => Invalidate()),
            eventAggregator.SubscribeAny<StoreEvent>(_ => Invalidate())
        ];
    }

    public async Task<bool> IsActivatedAsync(PaymentMethodId paymentMethodId, CancellationToken cancellationToken)
    {
        var activePaymentMethodIds = await GetActivePaymentMethodIds(cancellationToken);
        return activePaymentMethodIds.Contains(paymentMethodId);
    }

    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cacheVersion++;
            _activePaymentMethodIds = null;
            _cacheExpiresAt = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _eventAggregatorSubscriptions)
            subscription.Dispose();
    }

    private async Task<HashSet<PaymentMethodId>> GetActivePaymentMethodIds(CancellationToken cancellationToken)
    {
        long cacheVersion;
        lock (_cacheLock)
        {
            if (_activePaymentMethodIds is not null && _cacheExpiresAt > DateTimeOffset.UtcNow)
                return [.. _activePaymentMethodIds];

            cacheVersion = _cacheVersion;
        }

        var activePaymentMethodIds = await LoadActivePaymentMethodIds(cancellationToken);
        lock (_cacheLock)
        {
            if (cacheVersion != _cacheVersion)
                return activePaymentMethodIds;

            _activePaymentMethodIds = activePaymentMethodIds;
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            return [.. _activePaymentMethodIds];
        }
    }

    private async Task<HashSet<PaymentMethodId>> LoadActivePaymentMethodIds(CancellationToken cancellationToken)
    {
        var configuredPaymentMethodIds = GetConfiguredPaymentMethodIds().ToHashSet();
        var activePaymentMethodIds = new HashSet<PaymentMethodId>();
        if (configuredPaymentMethodIds.Count == 0)
            return activePaymentMethodIds;

        var stores = await _storeRepository.GetStores();
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var store in stores.Where(store => !store.Archived))
        {
            var excludedPaymentMethods = store.GetStoreBlob().GetExcludedPaymentMethods();
            var storeUpdated = false;
            foreach (var paymentMethodId in configuredPaymentMethodIds)
            {
                if (activePaymentMethodIds.Contains(paymentMethodId))
                    continue;

                var config = store.GetPaymentMethodConfig<USDCPaymentMethodConfig>(paymentMethodId, _handlers);
                if (!IsActivated(config, excludedPaymentMethods.Match(paymentMethodId)))
                    continue;

                activePaymentMethodIds.Add(paymentMethodId);
                if (config is { Activated: false })
                {
                    config.MarkActivated();
                    store.SetPaymentMethodConfig(_handlers[paymentMethodId], config);
                    storeUpdated = true;
                }
            }

            if (storeUpdated)
            {
                await _storeRepository.UpdateStore(store);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (activePaymentMethodIds.Count == configuredPaymentMethodIds.Count)
                break;
        }

        return activePaymentMethodIds;
    }

    private IEnumerable<PaymentMethodId> GetConfiguredPaymentMethodIds()
    {
        return _pluginConfiguration.EVMUSDCLikeConfigurationItems.Keys;
    }

    internal static bool IsActivated(USDCPaymentMethodConfig? config, bool excluded)
    {
        return config?.ActivatesChain(excluded) is true;
    }
}
