using System;
using BTCPayServer.Validation;

namespace BTCPayServer.Plugins.USDC.Controllers.ViewModels;

public class EVMUSDCLikeServerConfigViewModel
{
    public required string ChainDisplayName { get; set; }
    public string? DefaultSmartContractAddress { get; set; }

    [Uri]
    public string? JsonRpcUri { get; init; }
    public Uri? DefaultJsonRpcUri { get; set; }
    public required string DisplayName { get; set; }
}
