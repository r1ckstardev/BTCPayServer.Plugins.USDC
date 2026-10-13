using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.USDC.Services;

// The observation is written BEFORE the payment. Replaying either side of a crash
// uses this immutable invoice association and verifies the payment before a cursor
// may advance. Settings keys are independent of USDt and versioned explicitly.
public sealed class USDCChainRecovery(
    ApplicationDbContextFactory factory,
    InvoiceRepository invoices,
    PaymentMethodHandlerDictionary handlers,
    PaymentService payments,
    EventAggregator events,
    NotificationSender notifications,
    USDCAddressAllocator reservations)
{
    public sealed class Observation
    {
        public int Version { get; set; } = 2;
        public required string Id { get; init; }
        public required string InvoiceId { get; init; }
        public required string From { get; init; }
        public required string To { get; init; }
        public decimal Amount { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public long Height { get; set; }
        public required string BlockHash { get; set; }
        public bool Canonical { get; set; } = true;
        public bool Finalized { get; set; }
        public string? ReconciliationNotice { get; set; }
        public bool NoticeSent { get; set; }
    }

    private static string Prefix(PaymentMethodId pmi) => $"USDC.Transfer.v2.{pmi}.";

    // Monitoring windows are checked at the block's time, not the indexer's, so a
    // catch-up after an outage still credits transfers made inside the window.
    public async Task<InvoiceEntity?> ResolveCurrentInvoice(PaymentMethodId pmi, string address, DateTimeOffset timestamp)
    {
        var claim = await reservations.CurrentClaim(pmi, address);
        if (claim is null) return null;
        var invoice = await invoices.GetInvoice(claim.InvoiceId);
        return invoice is not null && ShouldTrack(invoice, pmi, timestamp) &&
               timestamp.ToUnixTimeSeconds() >= invoice.InvoiceTime.ToUnixTimeSeconds() ? invoice : null;
    }

    // Durable address claims decide what to watch. They survive restarts and
    // long outages, so no invoice cache or history bootstrap is needed.
    public async Task<HashSet<string>> WatchedAddresses(PaymentMethodId pmi, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = await reservations.CurrentClaims(pmi);
        if (claims.Length == 0) return watched;
        var claimed = await invoices.GetInvoices(new InvoiceQuery
        {
            InvoiceId = claims.Select(c => c.InvoiceId).Distinct().ToArray(),
            IncludeArchived = true
        }, cancellationToken);
        foreach (var invoice in claimed)
            if (ShouldTrack(invoice, pmi, timestamp) && invoice.GetPaymentPrompt(pmi)!.Activated)
                watched.Add(invoice.GetPaymentPrompt(pmi)!.Destination);
        return watched;
    }

    internal static bool ShouldTrack(InvoiceEntity invoice, PaymentMethodId pmi, DateTimeOffset now) =>
        !string.IsNullOrEmpty(invoice.GetPaymentPrompt(pmi)?.Destination) &&
        USDCAddressAllocator.ShouldReserve(invoice.Status, invoice.MonitoringExpiration, now);

    public async Task WithChainLock(PaymentMethodId pmi, Func<Task> action, CancellationToken cancellationToken)
    {
        // Hold a transaction advisory lock for the whole read/reconcile/checkpoint
        // cycle. Independent host processes cannot index the same chain concurrently.
        await using var context = factory.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var key = $"USDC.Indexer.v2.{pmi}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        await action();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Observation?> Find(PaymentMethodId pmi, string id)
    {
        await using var context = factory.CreateContext();
        var key = Prefix(pmi) + id;
        var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        return row is null ? null : JsonConvert.DeserializeObject<Observation>(row.Value);
    }

    public async Task<Observation[]> Outstanding(PaymentMethodId pmi)
    {
        await using var context = factory.CreateContext();
        var prefix = Prefix(pmi);
        // Retain finalized records for identity/association, but don't poll them.
        var rows = await context.Settings.FromSqlInterpolated($"""
            SELECT * FROM "Settings" WHERE starts_with("Id", {prefix})
              AND COALESCE(("Value"->>'Finalized')::boolean, false) = false
            """).AsNoTracking().ToArrayAsync();
        return rows.Select(s => JsonConvert.DeserializeObject<Observation>(s.Value)!).ToArray();
    }

    public async Task Save(PaymentMethodId pmi, Observation observation)
    {
        await using var context = factory.CreateContext();
        var key = Prefix(pmi) + observation.Id;
        var value = JsonConvert.SerializeObject(observation);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Settings" ("Id", "Value") VALUES ({key}, CAST({value} AS jsonb))
            ON CONFLICT ("Id") DO UPDATE SET "Value" = EXCLUDED."Value"
            """);
    }

    public async Task Reconcile(PaymentMethodId pmi, Observation observation, long indexedHeight)
    {
        var invoice = await invoices.GetInvoice(observation.InvoiceId);
        if (invoice is null)
        {
            // Deleting a store deletes its invoices. Retire the observation so it
            // cannot stall indexing for every other store on this chain.
            observation.Finalized = true;
            await Save(pmi, observation);
            return;
        }
        var handler = handlers[pmi];
        var payment = invoice.GetPayments(false).SingleOrDefault(p => p.PaymentMethodId == pmi && p.Id == observation.Id);
        var details = new EVMUSDCPaymentData
        {
            TransactionId = observation.Id, From = observation.From, To = observation.To,
            BlockHeight = observation.Height, BlockHash = observation.BlockHash,
            ReconciliationNotice = observation.ReconciliationNotice
        };
        details.UpdateConfirmations(indexedHeight);
        if (!observation.Canonical) details.ConfirmationCount = 0;
        var status = !observation.Canonical ? PaymentStatus.Unaccounted :
            details.PaymentConfirmed(invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing;

        if (payment is not null && (payment.Value != observation.Amount ||
            !Same(((USDCPaymentData)handler.ParsePaymentDetails(payment.Details)).To, observation.To) ||
            !Same(((USDCPaymentData)handler.ParsePaymentDetails(payment.Details)).From, observation.From)))
            throw new USDCFinalityConflictException($"Conflicting persisted payment {observation.Id}.");

        // Persist the notice before correcting a settled invoice: a crash cannot
        // lose the merchant-facing reconciliation requirement.
        if (invoice.Status == InvoiceStatus.Settled && status != PaymentStatus.Settled && observation.ReconciliationNotice is null)
        {
            observation.ReconciliationNotice = $"USDC reconciliation required: settled invoice {invoice.Id} has a payment removed or deconfirmed by a chain reorganization. Review delivery and payment accounting. Payment: {observation.Id}";
            await Save(pmi, observation);
            details.ReconciliationNotice = observation.ReconciliationNotice;
        }

        if (payment is null)
        {
            var data = new PaymentData
            {
                Id = observation.Id, InvoiceDataId = invoice.Id, Amount = observation.Amount,
                Currency = "USDC", Created = observation.Timestamp, Status = status
            }.Set(invoice, handler, details);
            // AddPayment can return null for ANY database update failure. Never
            // interpret null as proof of a duplicate or successful durable write.
            payment = await payments.AddPayment(data, [observation.Id]);
            if (payment is not null)
                events.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }
        else
        {
            var old = (USDCPaymentData)handler.ParsePaymentDetails(payment.Details);
            if (payment.Status != status || old.BlockHeight != details.BlockHeight ||
                old.BlockHash != details.BlockHash || old.ConfirmationCount != details.ConfirmationCount ||
                old.ReconciliationNotice != details.ReconciliationNotice)
            {
                payment.Status = status;
                payment.SetDetails(handler, details);
                await payments.UpdatePayments([payment]);
            }
        }

        var persisted = await invoices.GetInvoice(invoice.Id);
        var verified = persisted?.GetPayments(false).SingleOrDefault(p => p.PaymentMethodId == pmi && p.Id == observation.Id);
        if (verified is null || verified.Value != observation.Amount || verified.Status != status)
            throw new InvalidOperationException($"USDC payment {observation.Id} was not durably persisted; checkpoint unchanged.");
        var verifiedDetails = (USDCPaymentData)handler.ParsePaymentDetails(verified.Details);
        if (verifiedDetails.BlockHeight != observation.Height || verifiedDetails.BlockHash != observation.BlockHash ||
            verifiedDetails.ConfirmationCount != details.ConfirmationCount || !Same(verifiedDetails.To, observation.To) ||
            !Same(verifiedDetails.From, observation.From))
            throw new InvalidOperationException($"USDC payment {observation.Id} has unverified details; checkpoint unchanged.");

        // At-least-once event delivery is intentional after a process crash.
        events.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        if (observation.ReconciliationNotice is not null && !observation.NoticeSent)
        {
            await invoices.AddInvoiceEvent(invoice.Id, observation.ReconciliationNotice, InvoiceEventData.EventSeverity.Warning);
            await notifications.SendNotification(new StoreScope(invoice.StoreId), new USDCReconciliationNotification
            {
                InvoiceId = invoice.Id, StoreId = invoice.StoreId, Message = observation.ReconciliationNotice
            });
            observation.NoticeSent = true;
            await Save(pmi, observation);
        }
    }

    internal static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class USDCFinalityConflictException(string message) : Exception(message);
