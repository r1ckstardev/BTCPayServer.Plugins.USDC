using System.Numerics;
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class USDCPaymentData
{
    public int ConfirmationCount { get; set; }
    public required string TransactionId { get; init; }
    public BigInteger BlockHeight { get; init; }
    public required string To { get; init; }
    public required string From { get; init; }

    internal void UpdateConfirmations(BigInteger indexedHeight)
    {
        var difference = BigInteger.Max(0, indexedHeight - BlockHeight);
        ConfirmationCount = difference > int.MaxValue ? int.MaxValue : (int)difference;
    }

    public bool PaymentConfirmed(SpeedPolicy speedPolicy)
    {
        return speedPolicy switch
        {
            SpeedPolicy.HighSpeed => ConfirmationCount >= 2,
            SpeedPolicy.MediumSpeed => ConfirmationCount >= 6,
            SpeedPolicy.LowMediumSpeed => ConfirmationCount >= 12,
            SpeedPolicy.LowSpeed => ConfirmationCount >= 20,
            _ => false
        };
    }
}
