namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class USDCPaymentMethodConfig
{
    public string[] Addresses { get; set; } = [];
    public bool Activated { get; set; }

    public void MarkActivated()
    {
        Activated = true;
    }

    public void PreserveActivationFrom(USDCPaymentMethodConfig? previousConfig)
    {
        if (WasConfiguredOrActivated() || previousConfig?.WasConfiguredOrActivated() is true)
            MarkActivated();
    }

    public bool ActivatesChain(bool excluded)
    {
        return WasConfiguredOrActivated() || !excluded;
    }

    private bool WasConfiguredOrActivated()
    {
        return Activated || Addresses is { Length: > 0 };
    }

}
