using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Controllers.ViewModels;

public class ViewUSDCStoreOptionItemViewModel
{
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public required PaymentMethodId PaymentMethodId { get; init; }
    public required string[] Addresses { get; init; }
}