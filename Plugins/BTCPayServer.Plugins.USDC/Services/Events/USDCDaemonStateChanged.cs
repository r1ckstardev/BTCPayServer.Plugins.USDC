using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Services.Events;

public class USDCDaemonStateChanged
{
    public required PaymentMethodId PaymentMethodId { get; set; }
    public required USDCRpcSummary Summary { get; set; }
}