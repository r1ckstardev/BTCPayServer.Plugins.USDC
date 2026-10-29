using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Plugins.USDC.Services.Payments;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

namespace BTCPayServer.Plugins.USDC;

public class USDCPlugin : BaseBTCPayServerPlugin
{
    public const string PluginNavKey = "USDCPlugin";
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }];

    public override void Execute(IServiceCollection services)
    {
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        RegisterServices(services, new USDCPluginConfiguration
        {
            EVMUSDCLikeConfigurationItems = USDCConfigurationProvider.GetEVMUSDCLikeDefaultConfigurationItems(
                bootstrap.GetRequiredService<NBXplorerNetworkProvider>(), bootstrap.GetRequiredService<IConfiguration>())
        });
        base.Execute(services);
    }

    internal static void RegisterServices(IServiceCollection services, USDCPluginConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddSingleton<ISwaggerProvider, Controllers.SwaggerProvider>();
        services.AddSingleton<USDCChainActivationService>();
        services.AddSingleton<USDCAddressAllocator>();
        services.AddSingleton<USDCChainRecovery>();
        services.AddSingleton<USDCChainStateStore>();
        services.AddSingleton<INotificationHandler, USDCReconciliationNotification.Handler>();
        services.AddHostedService<USDCPluginConfigurationBootstrapper>();
        services.AddSingleton<EVMUSDCRPCProvider>();
        services.AddHostedService<EVMUSDCLikeSummaryUpdaterHostedService>();
        services.AddHostedService<EVMUSDCListener>();
        services.AddSingleton<ISyncSummaryProvider, EVMUSDCSyncSummaryProvider>();
        services.AddCurrencyData(new CurrencyData { Code = "USDC", Name = "USDC", Symbol = "USDC", Divisibility = 6, Crypto = true });
        foreach (var config in configuration.EVMUSDCLikeConfigurationItems.Values)
        {
            var pmi = config.GetPaymentMethodId();
            services.AddSingleton(new DefaultRules(config.DefaultRateRules));
            services.AddTransactionLinkProvider(pmi, new USDCTransactionLinkProvider(config.BlockExplorerLink));
            services.AddDefaultPrettyName(pmi, config.DisplayName);
            services.AddSingleton(provider => (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(
                provider, typeof(EVMUSDCPaymentMethodHandler), config));
            services.AddSingleton(provider => (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(
                provider, typeof(EVMUSDCPaymentLinkExtension), pmi));
            services.AddSingleton(provider => (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(
                provider, typeof(USDCCheckoutModelExtension), pmi, config.CryptoImagePath, config.CurrencyDisplayName));
        }
        services.AddUIExtension("store-wallets-nav", "USDC/StoreNav");
        services.AddUIExtension("server-nav", "USDC/ServerNav");
        services.AddUIExtension("store-invoices-payments", "USDC/PaymentDetails");
        services.AddUIExtension("checkout-end", "USDC/Checkout");
    }
}
