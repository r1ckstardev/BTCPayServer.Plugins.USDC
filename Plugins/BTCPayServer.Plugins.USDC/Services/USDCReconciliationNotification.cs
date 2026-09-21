using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Controllers;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.USDC.Services;

public sealed class USDCReconciliationNotification : BaseNotification
{
    public override string Identifier => "usdc_reconciliation";
    public override string NotificationType => "usdc_reconciliation";
    public string InvoiceId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string Message { get; set; } = "";

    public sealed class Handler(LinkGenerator links, BTCPayServerOptions options) : NotificationHandler<USDCReconciliationNotification>
    {
        public override string NotificationType => "usdc_reconciliation";
        public override (string identifier, string name)[] Meta => [(NotificationType, "USDC payment reconciliation")];
        protected override void FillViewModel(USDCReconciliationNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = notification.Message;
            vm.ActionLink = links.GetPathByAction(nameof(UIInvoiceController.Invoice),
                "UIInvoice", new { invoiceId = notification.InvoiceId }, options.RootPath);
        }
    }
}
