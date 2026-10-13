using Nethereum.Util;

namespace BTCPayServer.Plugins.USDC.Services;

public static class EVMAddressHelper
{
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        // quick format check
        if (!AddressUtil.Current.IsValidEthereumAddressHexFormat(address)) return false;
        return !string.Equals(address, "0x0000000000000000000000000000000000000000", System.StringComparison.OrdinalIgnoreCase);
    }
}
