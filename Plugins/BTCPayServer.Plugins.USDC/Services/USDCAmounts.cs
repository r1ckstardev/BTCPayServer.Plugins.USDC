using System;
using System.Numerics;

namespace BTCPayServer.Plugins.USDC.Services;

internal static class USDCAmounts
{
    internal static decimal FromBaseUnits(BigInteger amount, int decimals)
    {
        if (amount < 0 || decimals is < 0 or > 28)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var divisor = BigInteger.Pow(10, decimals);
        // Divide before converting: atomic amounts can exceed decimal.MaxValue.
        return checked((decimal)(amount / divisor) + (decimal)(amount % divisor) / (decimal)divisor);
    }
}
