using System.Globalization;
using System.Numerics;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Services;
using BTCPayServer.Plugins.USDC.Services.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.USDC.Tests;

public class PaymentTests
{
    [Fact]
    public void StandalonePluginRegistersCheckoutAndRatesWithoutUsdtServices()
    {
        var config = USDCConfigurationProvider.GetDefault(ChainName.Testnet);
        var services = new ServiceCollection();
        USDCPlugin.RegisterServices(services, new USDCPluginConfiguration
        {
            EVMUSDCLikeConfigurationItems = new() { [config.GetPaymentMethodId()] = config }
        });
        using var provider = services.BuildServiceProvider();
        var link = Assert.Single(provider.GetServices<IPaymentLinkExtension>());
        var checkout = Assert.Single(provider.GetServices<ICheckoutModelExtension>());
        Assert.Equal(config.GetPaymentMethodId(), link.PaymentMethodId);
        Assert.Equal(config.GetPaymentMethodId(), checkout.PaymentMethodId);
        Assert.Single(provider.GetServices<DefaultRules>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.FullName?.Contains("Plugins.USDt") is true);
    }

    [Theory]
    [InlineData("Mainnet", "USDC-ETHEREUM", 1, USDCConfigurationProvider.MainnetContract)]
    [InlineData("Testnet", "USDC-SEPOLIA", 11155111, USDCConfigurationProvider.SepoliaContract)]
    [InlineData("Regtest", "USDC-SEPOLIA", 11155111, USDCConfigurationProvider.SepoliaContract)]
    [InlineData("Mainnet", "USDC-POLYGON", 137, USDCConfigurationProvider.PolygonContract)]
    [InlineData("Testnet", "USDC-AMOY", 80002, USDCConfigurationProvider.AmoyContract)]
    [InlineData("Regtest", "USDC-AMOY", 80002, USDCConfigurationProvider.AmoyContract)]
    public void NativeUsdcNetworksFollowHostNetwork(string network, string pmi, int chainId, string contract)
    {
        var configs = USDCConfigurationProvider.GetEVMUSDCLikeDefaultConfigurationItems(
            new NBXplorerNetworkProvider(new ChainName(network)), new ConfigurationBuilder().Build());
        Assert.Equal(2, configs.Count);
        var config = configs[new PaymentMethodId(pmi)];
        Assert.Equal(pmi, config.GetPaymentMethodId().ToString());
        Assert.Equal(chainId, config.ChainId);
        Assert.Equal(contract, config.SmartContractAddress);
        Assert.Equal(6, config.Divisibility);
        Assert.Contains("USDC_BTC = USD_BTC", config.DefaultRateRules);
        Assert.StartsWith("USDC_", EVMUSDCRPCProvider.ListenerStateSettingKey(config));
        Assert.StartsWith("USDC_", USDCConfigurationProvider.ServerSettingsKey(config));
    }

    [Theory]
    [InlineData("1", "0.000001")]
    [InlineData("1234567", "1.234567")]
    [InlineData("100000000000000000000000000000", "100000000000000000000000")]
    public void AmountsRoundTripWithoutFloatingPointOrCultureLoss(string atomic, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var amount = USDCAmounts.FromBaseUnits(BigInteger.Parse(atomic), 6);
            Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), amount);
            Assert.Equal(BigInteger.Parse(atomic), USDCPaymentLinkFormats.ToBaseUnits(amount, 6));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(SpeedPolicy.HighSpeed, 2)]
    [InlineData(SpeedPolicy.MediumSpeed, 6)]
    [InlineData(SpeedPolicy.LowMediumSpeed, 12)]
    [InlineData(SpeedPolicy.LowSpeed, 20)]
    public void ConfirmationThresholdsUseSubsequentIndexedBlocks(SpeedPolicy speed, int threshold)
    {
        var payment = new EVMUSDCPaymentData { From = "from", To = "to", TransactionId = "tx", BlockHeight = 100 };
        payment.UpdateConfirmations(99);
        Assert.Equal(0, payment.ConfirmationCount);
        payment.UpdateConfirmations(100 + threshold - 1);
        Assert.False(payment.PaymentConfirmed(speed));
        payment.UpdateConfirmations(100 + threshold);
        Assert.True(payment.PaymentConfirmed(speed));
        payment.UpdateConfirmations(BigInteger.Pow(2, 100));
        Assert.Equal(int.MaxValue, payment.ConfirmationCount);
    }

    [Fact]
    public void CheckoutEncodesContractChainAndAtomicAmount()
    {
        var config = USDCConfigurationProvider.GetDefault(ChainName.Testnet);
        var uri = EVMUSDCPaymentLinkExtension.BuildPaymentLink("0x1111111111111111111111111111111111111111",
            config.SmartContractAddress, config.ChainId, config.Divisibility, 1.234567m);
        Assert.Equal($"ethereum:{USDCConfigurationProvider.SepoliaContract}@11155111/transfer?address=0x1111111111111111111111111111111111111111&uint256=1234567", uri);
    }

    [Fact]
    public void ExpiredInvoiceRemainsReservedOnlyDuringMonitoringWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var pmi = new PaymentMethodId("USDC-SEPOLIA");
        var invoice = new InvoiceEntity { Id = "invoice", Currency = "USD", Price = 1, Status = InvoiceStatus.Expired, MonitoringExpiration = now.AddMinutes(1) };
        invoice.SetPaymentPrompt(pmi, new PaymentPrompt { Currency = "USDC", Destination = "0x1111111111111111111111111111111111111111", Divisibility = 6 });
        Assert.True(USDCTrackedInvoiceProvider.ShouldTrack(invoice, pmi, now));
        Assert.False(USDCTrackedInvoiceProvider.ShouldTrack(invoice, pmi, now.AddMinutes(1)));
        Assert.False(USDCTrackedInvoiceProvider.ShouldTrack(invoice, new PaymentMethodId("USDT-SEPOLIA"), now));
    }
}
