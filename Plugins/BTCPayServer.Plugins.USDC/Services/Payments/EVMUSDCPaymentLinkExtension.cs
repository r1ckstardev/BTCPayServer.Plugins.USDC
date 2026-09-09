using System;
using System.Globalization;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class EVMUSDCPaymentLinkExtension(PaymentMethodId paymentMethodId, USDCPluginConfiguration pluginConfiguration)
    : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var configuration = pluginConfiguration.EVMUSDCLikeConfigurationItems[paymentMethodId];
        var template = prompt.Details?.Value<string?>("paymentLinkTemplate");
        var format = USDCPaymentLinkFormats.ResolveEvm(
            USDCPaymentLinkFormats.ReadFormat(prompt.Details, true),
            template);
        return BuildPaymentLink(
            prompt.Destination,
            configuration.SmartContractAddress,
            configuration.ChainId,
            configuration.Divisibility,
            prompt.Calculate().Due,
            format,
            template);
    }

    internal static string? BuildPaymentLink(
        string? destination,
        string smartContractAddress,
        int chainId,
        int divisibility,
        decimal due)
    {
        return BuildPaymentLink(
            destination,
            smartContractAddress,
            chainId,
            divisibility,
            due,
            USDCPaymentLinkFormat.Standard,
            null);
    }

    internal static string? BuildPaymentLink(
        string? destination,
        string smartContractAddress,
        int chainId,
        int divisibility,
        decimal due,
        USDCPaymentLinkFormat format,
        string? template)
    {
        if (string.IsNullOrEmpty(destination) ||
            string.IsNullOrWhiteSpace(smartContractAddress) ||
            string.Equals(smartContractAddress, EVMUSDCLikeConfigurationItem.UnconfiguredSmartContractAddress,
                StringComparison.OrdinalIgnoreCase))
            return null;

        var to = destination.ToLowerInvariant();
        var contract = smartContractAddress.ToLowerInvariant();

        if (format == USDCPaymentLinkFormat.AddressOnly)
            return to;

        if (format == USDCPaymentLinkFormat.Custom)
        {
            var values = USDCPaymentLinkFormats.CreateTemplateValues(
                to,
                due,
                divisibility,
                contract,
                chainId);
            if (USDCPaymentLinkFormats.TryRenderTemplate(template, true, values, out var rendered, out _))
                return rendered;
        }

        var amountUnits = USDCPaymentLinkFormats.ToBaseUnits(due, divisibility);

        // EIP-681 ERC-20 transfer link: ethereum:{contract}@{chainId}/transfer?address={to}&uint256={amount}
        return $"ethereum:{contract}@{chainId.ToString(CultureInfo.InvariantCulture)}/transfer?address={to}&uint256={amountUnits.ToString(CultureInfo.InvariantCulture)}";
    }
}
