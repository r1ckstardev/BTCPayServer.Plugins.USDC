using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.USDC.Services;

public sealed class USDCTrackedInvoiceProvider
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IUSDCInvoiceSource _invoiceSource;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<PaymentMethodId, TrackingState> _states = new();

    public USDCTrackedInvoiceProvider(InvoiceRepository invoiceRepository, USDCAddressAllocator reservations)
        : this(new InvoiceRepositoryUSDCInvoiceSource(invoiceRepository, reservations), TimeProvider.System)
    {
    }

    internal USDCTrackedInvoiceProvider(IUSDCInvoiceSource invoiceSource, TimeProvider timeProvider)
    {
        _invoiceSource = invoiceSource;
        _timeProvider = timeProvider;
    }

    public async Task<InvoiceEntity[]> GetTrackedInvoices(
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        var state = _states.GetOrAdd(paymentMethodId, _ => new TrackingState());
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();

            if (!state.Bootstrapped)
            {
                var expiredInvoices = await _invoiceSource.GetExpiredInvoicesSince(
                    DateTimeOffset.UnixEpoch,
                    cancellationToken);
                AddMatchingInvoices(state.Invoices, paymentMethodId, expiredInvoices);
                state.Bootstrapped = true;
                state.LastRefresh = now;
            }
            else if (now - state.LastRefresh >= RefreshInterval)
            {
                await RefreshKnownInvoices(state, cancellationToken);
                state.LastRefresh = now;
            }

            var monitoredInvoices = await _invoiceSource.GetMonitoredInvoices(paymentMethodId, cancellationToken);
            AddMatchingInvoices(state.Invoices, paymentMethodId, monitoredInvoices);

            foreach (var (invoiceId, invoice) in state.Invoices.ToArray())
            {
                if (!ShouldTrack(invoice, paymentMethodId, now))
                    state.Invoices.Remove(invoiceId);
            }

            return state.Invoices.Values.ToArray();
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal static bool ShouldTrack(
        InvoiceEntity invoice,
        PaymentMethodId paymentMethodId,
        DateTimeOffset now)
    {
        var prompt = invoice.GetPaymentPrompt(paymentMethodId);
        if (string.IsNullOrEmpty(prompt?.Destination))
            return false;

        if (USDCListenerShared.StatusToTrack.Contains(invoice.Status))
            return true;

        return invoice.Status == InvoiceStatus.Expired && invoice.MonitoringExpiration > now;
    }

    private async Task RefreshKnownInvoices(TrackingState state, CancellationToken cancellationToken)
    {
        var invoiceIds = state.Invoices.Keys.ToArray();
        if (invoiceIds.Length == 0)
            return;

        var refreshedInvoices = await _invoiceSource.GetInvoices(invoiceIds, cancellationToken);
        var refreshedById = refreshedInvoices.ToDictionary(invoice => invoice.Id);
        foreach (var invoiceId in invoiceIds)
        {
            if (refreshedById.TryGetValue(invoiceId, out var invoice))
                state.Invoices[invoiceId] = invoice;
            else
                state.Invoices.Remove(invoiceId);
        }
    }

    private static void AddMatchingInvoices(
        IDictionary<string, InvoiceEntity> destination,
        PaymentMethodId paymentMethodId,
        IEnumerable<InvoiceEntity> invoices)
    {
        foreach (var invoice in invoices)
        {
            if (!string.IsNullOrEmpty(invoice.GetPaymentPrompt(paymentMethodId)?.Destination))
                destination[invoice.Id] = invoice;
        }
    }

    private sealed class TrackingState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, InvoiceEntity> Invoices { get; } = new();
        public bool Bootstrapped { get; set; }
        public DateTimeOffset LastRefresh { get; set; }
    }
}

internal interface IUSDCInvoiceSource
{
    Task<InvoiceEntity[]> GetMonitoredInvoices(
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken);

    Task<InvoiceEntity[]> GetExpiredInvoicesSince(
        DateTimeOffset startDate,
        CancellationToken cancellationToken);

    Task<InvoiceEntity[]> GetInvoices(
        string[] invoiceIds,
        CancellationToken cancellationToken);
}

internal sealed class InvoiceRepositoryUSDCInvoiceSource(InvoiceRepository invoiceRepository, USDCAddressAllocator reservations) : IUSDCInvoiceSource
{
    public async Task<InvoiceEntity[]> GetMonitoredInvoices(
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken)
    {
        var claims = await reservations.CurrentClaims(paymentMethodId);
        if (claims.Length == 0) return [];
        return await invoiceRepository.GetInvoices(new InvoiceQuery
        {
            InvoiceId = claims.Select(c => c.InvoiceId).Distinct().ToArray(),
            IncludeArchived = true
        }, cancellationToken);
    }

    public Task<InvoiceEntity[]> GetExpiredInvoicesSince(
        DateTimeOffset startDate,
        CancellationToken cancellationToken)
    {
        // Current durable claims already include expired monitoring deadlines,
        // even after long outages. No time-limited historical cache bootstrap.
        return Task.FromResult(Array.Empty<InvoiceEntity>());
    }

    public Task<InvoiceEntity[]> GetInvoices(
        string[] invoiceIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return invoiceRepository.GetInvoices(invoiceIds);
    }
}
