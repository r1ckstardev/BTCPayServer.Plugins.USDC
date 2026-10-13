using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.USDC.Services;

public class EVMUSDCLikeSummaryUpdaterHostedService(
    EVMUSDCRPCProvider rpcProvider,
    USDCPluginConfiguration pluginConfiguration,
    Logs logs,
    USDCChainActivationService activationService) : IHostedService
{
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var paymentMethodId in pluginConfiguration.EVMUSDCLikeConfigurationItems.Keys)
            _ = StartLoop(_cts.Token, paymentMethodId);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task StartLoop(CancellationToken cancellation, PaymentMethodId paymentMethodId)
    {
        logs.PayServer.LogInformation("Starting listening EVM USDCLike daemons ({PaymentMethodId})", paymentMethodId);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (!await activationService.IsActivatedAsync(paymentMethodId, cancellation))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellation);
                        continue;
                    }

                    await rpcProvider.UpdateSummary(paymentMethodId);
                    await Task.Delay(rpcProvider.IsAvailable(paymentMethodId) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30),
                        cancellation);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                {
                    logs.PayServer.LogError(ex, "Unhandled exception in Summary updater ({PaymentMethodId})",
                        paymentMethodId);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
                }
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
    }
}
