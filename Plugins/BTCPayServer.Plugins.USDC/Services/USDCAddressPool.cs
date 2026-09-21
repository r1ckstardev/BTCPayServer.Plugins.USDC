using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.USDC.Services;

internal static class USDCAddressPool
{
    internal static bool TryNormalize(IEnumerable<string?>? addresses, string contract,
        out string[] normalized, out string? error)
    {
        normalized = [];
        error = null;
        if (addresses is null) { error = "Addresses must be an array."; return false; }
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in addresses)
        {
            var address = value?.Trim();
            if (!EVMAddressHelper.IsValid(address) || string.Equals(address, contract, StringComparison.OrdinalIgnoreCase))
            { error = $"Address {result.Count + 1} must be a valid nonzero receiving address, different from the USDC contract."; return false; }
            if (!seen.Add(address!)) { error = $"Duplicate address: {address}."; return false; }
            result.Add(address!.ToLowerInvariant());
        }
        normalized = result.ToArray();
        return true;
    }
}
