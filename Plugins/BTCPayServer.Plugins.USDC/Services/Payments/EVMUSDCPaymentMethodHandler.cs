using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using BTCPayServer.Services.Rates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.USDC.Services.Payments;

public class EVMUSDCPaymentMethodHandler(
    EVMUSDCLikeConfigurationItem configurationItem,
    EVMUSDCRPCProvider rpcProvider,
    CurrencyNameTable currencyNameTable,
    USDCAddressAllocator addressAllocator) : IPaymentMethodHandler
{
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = configurationItem.Currency;
        context.Prompt.Divisibility = configurationItem.Divisibility;
        context.Prompt.RateDivisibility = null;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (!rpcProvider.IsAvailable(configurationItem.GetPaymentMethodId()))
            throw new PaymentMethodUnavailableException("Node or wallet not available");

        if (!configurationItem.HasValidSmartContractAddress())
            throw new PaymentMethodUnavailableException(
                $"{configurationItem.DisplayName} is not configured with a smart contract address yet");

        var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
        var details = CreatePaymentPromptDetails(config);
        var availableAddress = await addressAllocator.Allocate(context.PaymentMethodId,
                                   context.Store.Id, context.InvoiceEntity.Id, config.Addresses,
                                   context.InvoiceEntity.InvoiceTime, context.InvoiceEntity.MonitoringExpiration) ??
                               throw new PaymentMethodUnavailableException(
                                   $"No available {configurationItem.Chain} receiving addresses remain. Add addresses or wait for an existing reservation to end.");
        context.Prompt.Destination = availableAddress;
        // BTCPay's AddressInvoices registry has a permanent unique address key.
        // Reusable USDC monitoring is recovered from our durable reservations.
        context.Prompt.PaymentMethodFee = 0;
        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    internal static EVMUSDCLikeOnChainPaymentMethodDetails CreatePaymentPromptDetails(EVMUSDCPaymentMethodConfig config)
    {
        var paymentLinkFormat = USDCPaymentLinkFormats.ResolveEvm(
            config.PaymentLinkFormat,
            config.PaymentLinkTemplate);
        return new EVMUSDCLikeOnChainPaymentMethodDetails
        {
            PaymentLinkFormat = paymentLinkFormat,
            PaymentLinkTemplate = paymentLinkFormat == USDCPaymentLinkFormat.Custom
                ? config.PaymentLinkTemplate
                : null
        };
    }

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfig(config);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details)!;
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    private EVMUSDCPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<EVMUSDCPaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(EVMUSDCPaymentMethodHandler)}");
    }

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        var config = ParsePaymentMethodConfig(validationContext.Config);
        if (!USDCAddressPool.TryNormalize(config.Addresses, configurationItem.SmartContractAddress,
                out var normalized, out var addressError))
            validationContext.ModelState.AddModelError(nameof(config.Addresses), addressError!);
        else
            config.Addresses = normalized;
        var previousConfig = validationContext.PreviousConfig is null
            ? null
            : ParsePaymentMethodConfig(validationContext.PreviousConfig);
        var templateValues = USDCPaymentLinkFormats.CreateTemplateValues(
            "0x742d35cc6634c0532925a3b844bc454e4438f44e",
            12.34m,
            configurationItem.Divisibility,
            configurationItem.SmartContractAddress.ToLowerInvariant(),
            configurationItem.ChainId);
        if (config.PaymentLinkFormat is { } format)
        {
            var error = USDCPaymentLinkFormats.ValidateSelection(
                format,
                config.PaymentLinkTemplate,
                true,
                templateValues);
            if (error is not null)
                validationContext.ModelState.AddModelError(nameof(config.PaymentLinkFormat), error);
        }
        else if (!string.IsNullOrWhiteSpace(config.PaymentLinkTemplate))
        {
            var error = USDCPaymentLinkFormats.ValidateSelection(
                USDCPaymentLinkFormat.Custom,
                config.PaymentLinkTemplate,
                true,
                templateValues);
            if (error is not null)
                validationContext.ModelState.AddModelError(nameof(config.PaymentLinkTemplate), error);
        }
        config.PreserveActivationFrom(previousConfig);
        validationContext.Config = JToken.FromObject(config, Serializer);
        return Task.CompletedTask;
    }

    public EVMUSDCLikeOnChainPaymentMethodDetails? ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<EVMUSDCLikeOnChainPaymentMethodDetails>(Serializer);
    }

    public EVMUSDCPaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<EVMUSDCPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(EVMUSDCPaymentMethodHandler)}");
    }
}
