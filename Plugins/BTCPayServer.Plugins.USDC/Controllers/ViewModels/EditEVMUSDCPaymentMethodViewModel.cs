using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services.Payments;

namespace BTCPayServer.Plugins.USDC.Controllers.ViewModels;

public class EditEVMUSDCPaymentMethodViewModel
{
    public required string DisplayName { get; init; }
    public required string ChainDisplayName { get; init; }
    public string AddressPlaceholder { get; init; } = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e";
    public string? Address { get; init; }
    public bool Enabled { get; init; }
    public USDCPaymentLinkFormat PaymentLinkFormat { get; init; } = USDCPaymentLinkFormat.Standard;
    public string? PaymentLinkTemplate { get; init; }
    public string TemplatePreviewDestination { get; init; } = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e";
    public string TemplatePreviewAmount { get; init; } = "12.34";
    public string TemplatePreviewAmountUnits { get; init; } = "12340000";
    public string TemplatePreviewSmartContractAddress { get; init; } = string.Empty;
    public string TemplatePreviewChainId { get; init; } = string.Empty;

    public EditEVMUSDCPaymentMethodAddressViewModel[] Addresses { get; init; } = [];

    public class EditEVMUSDCPaymentMethodAddressViewModel
    {
        public required string Value { get; init; }
        public bool Available { get; init; }
        public required string Balance { get; init; }
    }
}
