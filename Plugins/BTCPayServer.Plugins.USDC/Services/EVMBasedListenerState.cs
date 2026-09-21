using System.Numerics;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMBasedListenerState
{
    public int Version { get; set; } = 2;
    public BigInteger LastBlockHeight { get; set; }
    public long FinalizedHeight { get; set; } = -1;
    public string? FinalizedHash { get; set; }
    public SortedDictionary<long, USDCBlockRecord> Blocks { get; set; } = [];
    public string? HaltReason { get; set; }
}

public sealed record USDCBlockRecord(string Hash, string ParentHash);
