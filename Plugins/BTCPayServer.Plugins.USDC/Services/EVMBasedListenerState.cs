using System.Numerics;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMBasedListenerState
{
    public BigInteger LastBlockHeight { get; set; }
}