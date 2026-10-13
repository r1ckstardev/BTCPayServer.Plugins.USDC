using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Plugins.USDC.Controllers.ViewModels;
using BTCPayServer.Plugins.USDC.Services.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NBXplorer;

namespace BTCPayServer.Plugins.USDC.Controllers;

[Route("server/evmUSDClike/{paymentMethodId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIEVMUSDCLikeServerController(
    IConfiguration configuration,
    ISettingsRepository settingsRepository,
    NBXplorerNetworkProvider nbXplorerNetworkProvider,
    USDCPluginConfiguration usdtPluginConfiguration,
    EventAggregator eventAggregator) : Controller
{
    [HttpGet]
    public async Task<IActionResult> GetServerConfig(PaymentMethodId paymentMethodId)
    {
        if (!usdtPluginConfiguration.EVMUSDCLikeConfigurationItems.TryGetValue(paymentMethodId, out var evmConfiguration))
            return NotFound();

        var defaultConfiguration = USDCConfigurationProvider.GetEVMUSDCDefaultConfigurationItem(paymentMethodId, nbXplorerNetworkProvider, configuration);
        var serverSettings = await settingsRepository.GetSettingAsync<EVMUSDCLikeServerSettings>(USDCConfigurationProvider.ServerSettingsKey(evmConfiguration));

        var viewModel = new EVMUSDCLikeServerConfigViewModel
        {
            DisplayName = evmConfiguration.DisplayName,
            ChainDisplayName = evmConfiguration.Chain,
            DefaultSmartContractAddress = defaultConfiguration.SmartContractAddress,
            DefaultJsonRpcUri = defaultConfiguration.JsonRpcUri,
            JsonRpcUri = serverSettings?.JsonRpcUri?.AbsoluteUri
        };
        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GetServerConfig(EVMUSDCLikeServerConfigViewModel viewModel, PaymentMethodId paymentMethodId)
    {
        if (!usdtPluginConfiguration.EVMUSDCLikeConfigurationItems.TryGetValue(paymentMethodId, out var currentConfiguration))
            return NotFound();

        var defaultConfiguration = USDCConfigurationProvider.GetEVMUSDCDefaultConfigurationItem(paymentMethodId, nbXplorerNetworkProvider, configuration);
        if (!ModelState.IsValid)
        {
            viewModel.DisplayName = currentConfiguration.DisplayName;
            viewModel.ChainDisplayName = currentConfiguration.Chain;
            viewModel.DefaultSmartContractAddress = defaultConfiguration.SmartContractAddress;
            viewModel.DefaultJsonRpcUri = defaultConfiguration.JsonRpcUri;
            return View(viewModel);
        }

        var serverSettings = new EVMUSDCLikeServerSettings
        {
            JsonRpcUri = string.IsNullOrWhiteSpace(viewModel.JsonRpcUri) ? null : new Uri(viewModel.JsonRpcUri)
        };

        await settingsRepository.UpdateSetting(serverSettings, USDCConfigurationProvider.ServerSettingsKey(currentConfiguration));

        usdtPluginConfiguration.EVMUSDCLikeConfigurationItems[paymentMethodId] =
            await USDCConfigurationProvider.OverrideWithServerSettingsAsync(defaultConfiguration, settingsRepository);

        eventAggregator.Publish(new USDCSettingsChanged());

        return RedirectToAction(nameof(GetServerConfig), new { paymentMethodId });
    }
}
