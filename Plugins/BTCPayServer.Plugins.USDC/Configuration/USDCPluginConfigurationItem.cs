using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.USDC.Configuration;

public abstract record USDCPluginConfigurationItem
{
    public abstract string Chain { get;  }
    public abstract string Currency { get; init; }

    public ChainRef ChainRef => Chain;
    public CurrencyRef CurrencyRef => Currency;

    public PaymentMethodId GetPaymentMethodId() => new($"{CurrencyRef}-{ChainRef}");
    public string GetSettingPrefix() => $"{CurrencyRef}_{ChainRef}";
}