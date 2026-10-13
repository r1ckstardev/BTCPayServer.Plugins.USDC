using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using BTCPayServer.Plugins.USDC.Configuration.EVM;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.USDC.Services.Payments;

public enum USDCPaymentLinkFormat
{
    Standard = 0,
    AddressOnly = 2,
    Custom = 3
}

internal static class USDCPaymentLinkFormats
{
    internal const int MaxTemplateLength = 2048;
    internal const int MaxRenderedLength = 4096;
    internal const string SampleDestination = "0x742d35cc6634c0532925a3b844bc454e4438f44e";

    internal static readonly string[] Placeholders =
        ["{to}", "{amount}", "{amountUnits}", "{smartContractAddress}", "{chainId}"];

    private static readonly Regex PlaceholderPattern = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant);

    internal static USDCPaymentLinkFormat Resolve(USDCPaymentLinkFormat? format, string? template)
    {
        if (format is { } explicitFormat && Enum.IsDefined(explicitFormat))
            return explicitFormat;

        if (format is null && !string.IsNullOrWhiteSpace(template))
            return USDCPaymentLinkFormat.Custom;

        return USDCPaymentLinkFormat.Standard;
    }

    internal static USDCPaymentLinkFormat? ReadFormat(JToken? details)
    {
        if (details is null)
            return null;

        if (details is not JObject detailsObject)
            return USDCPaymentLinkFormat.Standard;

        var token = detailsObject["paymentLinkFormat"];
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
            return null;

        try
        {
            var format = token.ToObject<USDCPaymentLinkFormat?>();
            return format is { } explicitFormat && Enum.IsDefined(explicitFormat)
                ? explicitFormat
                : USDCPaymentLinkFormat.Standard;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or
                                          InvalidCastException or OverflowException)
        {
            return USDCPaymentLinkFormat.Standard;
        }
    }

    internal static string? ValidateSelection(
        USDCPaymentLinkFormat format,
        string? template,
        IReadOnlyDictionary<string, string> templateValues)
    {
        if (!Enum.IsDefined(format))
            return "The selected payment link format is not supported.";

        if (format != USDCPaymentLinkFormat.Custom)
            return null;

        TryRenderTemplate(template, templateValues, out _, out var error);
        return error;
    }

    internal static string? ValidateTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "A payment link template is required when Custom format is selected.";

        if (template.Length > MaxTemplateLength)
            return $"The payment link template cannot exceed {MaxTemplateLength.ToString(CultureInfo.InvariantCulture)} characters.";

        if (template.Any(char.IsControl))
            return "The payment link template cannot contain control characters.";

        foreach (Match match in PlaceholderPattern.Matches(template))
        {
            if (!Placeholders.Contains(match.Value, StringComparer.Ordinal))
                return $"Unknown or unsupported payment link placeholder: {match.Value}.";
        }

        var staticText = PlaceholderPattern.Replace(template, string.Empty);
        if (staticText.Contains('{') || staticText.Contains('}'))
            return "The payment link template contains an invalid placeholder expression.";

        if (!template.Contains("{to}", StringComparison.Ordinal))
            return "The payment link template must contain the {to} placeholder.";

        return null;
    }

    internal static bool TryRenderTemplate(
        string? template,
        IReadOnlyDictionary<string, string> values,
        out string? rendered,
        out string? error)
    {
        error = ValidateTemplate(template);
        if (error is not null)
        {
            rendered = null;
            return false;
        }

        rendered = PlaceholderPattern.Replace(template!, match => values[match.Value]);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            error = "The rendered payment link cannot be empty.";
            rendered = null;
            return false;
        }

        if (rendered.Length > MaxRenderedLength)
        {
            error = $"The rendered payment link cannot exceed {MaxRenderedLength.ToString(CultureInfo.InvariantCulture)} characters.";
            rendered = null;
            return false;
        }

        return true;
    }

    internal static BigInteger ToBaseUnits(decimal due, int divisibility)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(divisibility);
        if (due <= 0) return BigInteger.Zero;
        var bits = decimal.GetBits(due);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        var decimalScale = (bits[3] >> 16) & 0xff;
        return coefficient * BigInteger.Pow(10, divisibility) / BigInteger.Pow(10, decimalScale);
    }

    internal static IReadOnlyDictionary<string, string> CreateTemplateValues(
        string destination,
        decimal due,
        int divisibility,
        string smartContractAddress,
        int chainId)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{to}"] = destination,
            ["{amount}"] = due.ToString(CultureInfo.InvariantCulture),
            ["{amountUnits}"] = ToBaseUnits(due, divisibility).ToString(CultureInfo.InvariantCulture),
            ["{smartContractAddress}"] = smartContractAddress,
            ["{chainId}"] = chainId.ToString(CultureInfo.InvariantCulture)
        };
    }

    // Values used to validate and preview a custom template before any invoice exists.
    internal static IReadOnlyDictionary<string, string> SampleTemplateValues(EVMUSDCLikeConfigurationItem config) =>
        CreateTemplateValues(SampleDestination, 12.34m, config.Divisibility,
            config.SmartContractAddress.ToLowerInvariant(), config.ChainId);
}
