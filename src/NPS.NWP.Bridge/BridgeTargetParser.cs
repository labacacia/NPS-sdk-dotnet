// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using NPS.NWP.Frames;

namespace NPS.NWP.Bridge;

/// <summary>
/// Parser and accessors for the <c>bridge_target</c> action parameter.
/// </summary>
public static class BridgeTargetParser
{
    /// <summary>Parse <c>params.bridge_target</c> from an action frame.</summary>
    public static BridgeTarget FromActionFrame(ActionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Params is not { } parameters)
            throw new BridgeDispatchException(BridgeErrorCodes.TargetInvalid, "params.bridge_target is required.");

        var targetElement = parameters;
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("bridge_target", out var nested))
        {
            targetElement = nested;
        }

        return FromJson(targetElement);
    }

    /// <summary>Parse a bridge target JSON object.</summary>
    public static BridgeTarget FromJson(JsonElement targetElement)
    {
        if (targetElement.ValueKind != JsonValueKind.Object)
            throw new BridgeDispatchException(BridgeErrorCodes.TargetInvalid, "bridge_target must be an object.");

        var protocol = ReadRequiredString(targetElement, "protocol");
        var endpoint = ReadRequiredString(targetElement, "endpoint");
        var extras = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in targetElement.EnumerateObject())
        {
            if (property.NameEquals("protocol") || property.NameEquals("endpoint"))
                continue;

            if (property.NameEquals("extras") && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var extra in property.Value.EnumerateObject())
                    extras[extra.Name] = extra.Value.Clone();
                continue;
            }

            extras[property.Name] = property.Value.Clone();
        }

        return new BridgeTarget(protocol, endpoint, extras.Count == 0 ? null : extras);
    }

    /// <summary>Read a string extra from a target.</summary>
    public static string? GetString(BridgeTarget target, string name, string? defaultValue = null)
    {
        if (!TryGetExtra(target, name, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } json => json.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } => bool.TrueString,
            JsonElement { ValueKind: JsonValueKind.False } => bool.FalseString,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    /// <summary>Try to read a JSON extra from a target.</summary>
    public static bool TryGetJson(BridgeTarget target, string name, out JsonElement value)
    {
        if (!TryGetExtra(target, name, out var raw) || raw is null)
        {
            value = default;
            return false;
        }

        if (raw is JsonElement json)
        {
            value = json;
            return true;
        }

        value = JsonSerializer.SerializeToElement(raw);
        return true;
    }

    private static bool TryGetExtra(BridgeTarget target, string name, out object? value)
    {
        value = null;
        return target.Extras is not null && target.Extras.TryGetValue(name, out value);
    }

    private static string ReadRequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BridgeDispatchException(BridgeErrorCodes.TargetInvalid, $"bridge_target.{name} is required.");
        }

        return value.GetString()!;
    }
}
