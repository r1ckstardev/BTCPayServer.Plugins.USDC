using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services.Events;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.USDC.Services;

public class USDCPluginConfigurationBootstrapper(
    ISettingsRepository settingsRepository,
    USDCPluginConfiguration pluginConfiguration,
    EventAggregator eventAggregator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {

        foreach (var pair in pluginConfiguration.EVMUSDCLikeConfigurationItems)
        {
            pluginConfiguration.EVMUSDCLikeConfigurationItems[pair.Key] =
                await USDCConfigurationProvider.OverrideWithServerSettingsAsync(pair.Value, settingsRepository);
        }

        eventAggregator.Publish(new USDCSettingsChanged());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}