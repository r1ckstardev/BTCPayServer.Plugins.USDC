using System;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Text;
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
        if (pluginConfiguration.EVMUSDCLikeConfigurationItems.ContainsKey(paymentMethodId) == false)
            return NotFound();

        var excludeFilters = StoreData.GetStoreBlob().GetExcludedPaymentMethods();
        var matchedPaymentMethodConfig =
            StoreData.GetPaymentMethodConfig<EVMUSDCPaymentMethodConfig>(paymentMethodId, handlers);

        if (matchedPaymentMethodConfig == null)
            return NotFound();

        var addresses = (matchedPaymentMethodConfig.Addresses ?? [])
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var balances = await evmUSDCRpcProvider.GetBalances(paymentMethodId, addresses);
        var reservedAddresses =
            await addressAllocator.GetUsedAddresses(paymentMethodId, addresses);

        var data = new EVMUSDCPaymentMethodInformation
        {
            StoreId = StoreData.Id,
            PaymentMethodId = paymentMethodId.ToString(),
            Enabled = !excludeFilters.Match(paymentMethodId),
            Addresses = addresses.Select(s =>
                new EVMUSDCPaymentMethodInformation.EVMUSDCPaymentMethodAddressInformation()
                {
                    Available = reservedAddresses.Contains(s) == false,
                    Balance = balances.Single(x => x.Item1 == s).Item2 == null
                        ? null
                        : balances.Single(x => x.Item1 == s).Item2!.Value,
                    Value = s
                }).ToArray()
        };

        return Ok(data);
    }
}
