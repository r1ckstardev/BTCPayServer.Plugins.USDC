using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Controllers.Models;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.USDC.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldEVMUSDCLikeStoreController(
    EVMUSDCRPCProvider evmUSDCRpcProvider,
    PaymentMethodHandlerDictionary handlers,
    USDCAddressAllocator addressAllocator,
    USDCPluginConfiguration pluginConfiguration) : ControllerBase
{
    private StoreData StoreData => HttpContext.GetStoreData();

    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [HttpGet("~/api/v1/stores/{storeId}/evmUSDClike/{paymentMethodId}")]
    public async Task<IActionResult> GetUSDCLikeStoreInformation(PaymentMethodId paymentMethodId)
    {
        if (!pluginConfiguration.EVMUSDCLikeConfigurationItems.ContainsKey(paymentMethodId))
            return NotFound();

        var matchedPaymentMethodConfig =
            StoreData.GetPaymentMethodConfig<EVMUSDCPaymentMethodConfig>(paymentMethodId, handlers);
        if (matchedPaymentMethodConfig == null)
            return NotFound();

        var addresses = matchedPaymentMethodConfig.Addresses ?? [];
        var balances = await evmUSDCRpcProvider.GetBalances(paymentMethodId, addresses);
        var reservedAddresses = await addressAllocator.GetUsedAddresses(paymentMethodId, addresses);

        return Ok(new EVMUSDCPaymentMethodInformation
        {
            StoreId = StoreData.Id,
            PaymentMethodId = paymentMethodId.ToString(),
            Enabled = !StoreData.GetStoreBlob().GetExcludedPaymentMethods().Match(paymentMethodId),
            Addresses = addresses.Select(s => new EVMUSDCPaymentMethodInformation.EVMUSDCPaymentMethodAddressInformation
            {
                Available = !reservedAddresses.Contains(s),
                Balance = balances.Single(x => x.Item1 == s).Item2,
                Value = s
            }).ToArray()
        });
    }
}
