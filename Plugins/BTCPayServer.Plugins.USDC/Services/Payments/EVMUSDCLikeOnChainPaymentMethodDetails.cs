namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class EVMUSDCLikeOnChainPaymentMethodDetails
{
    public USDCPaymentLinkFormat? PaymentLinkFormat { get; set; }
    public string? PaymentLinkTemplate { get; set; }
}
