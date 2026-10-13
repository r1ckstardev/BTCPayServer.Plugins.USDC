namespace BTCPayServer.Plugins.USDC.Controllers.Models;

public class EVMUSDCPaymentMethodInformation
{
    public required string StoreId { get; init; }

    public required string PaymentMethodId { get; init; }
    public required bool Enabled { get; init; }

    public EVMUSDCPaymentMethodAddressInformation[] Addresses { get; init; } =
        [];

    public class EVMUSDCPaymentMethodAddressInformation
    {
        public required string Value { get; init; }
        public bool Available { get; init; }
        public required decimal? Balance { get; init; }
    }
}
