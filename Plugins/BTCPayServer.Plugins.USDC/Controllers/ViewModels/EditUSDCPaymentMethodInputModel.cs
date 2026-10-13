using BTCPayServer.Plugins.USDC.Services.Payments;

namespace BTCPayServer.Plugins.USDC.Controllers.ViewModels;

// Only bind submitted settings. Display names, balances and preview values belong
// to the GET view models and must not participate in POST validation.
public class EditUSDCPaymentMethodInputModel
{
    public string? Address { get; set; }
    public bool Enabled { get; set; }
    public USDCPaymentLinkFormat PaymentLinkFormat { get; set; } = USDCPaymentLinkFormat.Standard;
    public string? PaymentLinkTemplate { get; set; }
}
