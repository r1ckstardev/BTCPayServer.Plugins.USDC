using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.USDC.Services;

public sealed class USDCReconciliationNotification : BaseNotification
{
    public override string Identifier => "usdc_reconciliation";
    public override string NotificationType => "usdc_reconciliation";
    public string InvoiceId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string Message { get; set; } = "";

    public sealed class Handler : NotificationHandler<USDCReconciliationNotification>
    {
        public override string NotificationType => "usdc_reconciliation";
        public override (string identifier, string name)[] Meta => [(NotificationType, "USDC payment reconciliation")];
        protected override void FillViewModel(USDCReconciliationNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = notification.Message;
            vm.ActionLink = $"/invoices/{System.Uri.EscapeDataString(notification.InvoiceId)}";
        }
    }
}
