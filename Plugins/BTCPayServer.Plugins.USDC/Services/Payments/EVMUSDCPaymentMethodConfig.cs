namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class EVMUSDCPaymentMethodConfig : USDCPaymentMethodConfig
{
    public USDCPaymentLinkFormat? PaymentLinkFormat { get; set; }
    public string? PaymentLinkTemplate { get; set; }
}
