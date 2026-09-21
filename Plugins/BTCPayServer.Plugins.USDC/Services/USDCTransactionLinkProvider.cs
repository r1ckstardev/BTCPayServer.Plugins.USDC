using System.Globalization;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.USDC.Services;

internal class USDCTransactionLinkProvider(string blockExplorerLink)
    : DefaultTransactionLinkProvider(blockExplorerLink)
{
    public override string? GetTransactionLink(string paymentId)
    {
        if (string.IsNullOrEmpty(BlockExplorerLink))
            return null;

        var hash = paymentId.Split('-')[0];
        if (!hash.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) hash = "0x" + hash;
        return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, hash);
    }
}
