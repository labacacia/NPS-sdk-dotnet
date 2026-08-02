// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace NPS.NDP.Registry;

/// <summary>Portable NDP 0.12 Announce admission outcomes.</summary>
public enum NdpRegistryDecision
{
    /// <summary>A new or higher-sequence entry was stored.</summary>
    Accepted,
    /// <summary>An exact retransmission was ignored.</summary>
    Duplicate,
    /// <summary>Only advisory liveness fields changed.</summary>
    Refreshed,
    /// <summary>A valid offline announcement removed the live entry.</summary>
    Removed,
    /// <summary>The announcement was rejected without state mutation.</summary>
    Rejected,
}

/// <summary>Result of portable NDP 0.12 Announce admission.</summary>
public sealed record NdpRegistryAdmission(
    NdpRegistryDecision Decision,
    string? ErrorCode = null);

/// <summary>Result of deterministic cluster-Anchor resolution.</summary>
public sealed record NdpClusterSelection(
    string? Nid,
    ulong? Epoch,
    string? ErrorCode = null);

/// <summary>
/// NDP 0.12 canonical signed-body and Ed25519 verification helpers.
/// </summary>
public static class NdpAnnounceCanonicalizer
{
    private static readonly HashSet<string> Excluded =
        ["frame", "signature", "health", "last_seen"];

    /// <summary>Canonicalizes an AnnounceFrame JSON object per NPS-4 §7.4.</summary>
    public static string CanonicalJson(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("AnnounceFrame must be a JSON object.", nameof(frame));

        var sb = new StringBuilder();
        WriteObject(frame, sb, injectHeartbeatDefault: true);
        return sb.ToString();
    }

    /// <summary>Verifies an NDP Announce signature using a raw base64url Ed25519 key.</summary>
    public static bool Verify(
        JsonElement frame,
        string encodedPublicKey,
        string encodedSignature)
    {
        const string prefix = "ed25519:";
        if (!encodedPublicKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !encodedSignature.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(
                algorithm,
                DecodeBase64Url(encodedPublicKey[prefix.Length..]),
                KeyBlobFormat.RawPublicKey);
            var signature = DecodeBase64Url(encodedSignature[prefix.Length..]);
            return algorithm.Verify(
                publicKey,
                Encoding.UTF8.GetBytes(CanonicalJson(frame)),
                signature);
        }
        catch (Exception ex) when (
            ex is FormatException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    internal static string Digest(JsonElement frame) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(frame))));

    private static void WriteObject(
        JsonElement element,
        StringBuilder sb,
        bool injectHeartbeatDefault)
    {
        sb.Append('{');
        var properties = element.EnumerateObject()
            .Where(property =>
                (!injectHeartbeatDefault || !Excluded.Contains(property.Name)) &&
                property.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(
                property => property.Name,
                property => property.Value,
                StringComparer.Ordinal);
        var injectDefault = injectHeartbeatDefault &&
            !properties.ContainsKey("heartbeat_interval_ms");
        var orderedNames = properties.Keys
            .Append(injectDefault ? "heartbeat_interval_ms" : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();
        for (var index = 0; index < orderedNames.Count; index++)
        {
            if (index > 0) sb.Append(',');
            var name = orderedNames[index];
            sb.Append('"')
                .Append(JsonEncodedText.Encode(name))
                .Append("\":");
            if (injectDefault && name == "heartbeat_interval_ms")
                sb.Append("60000");
            else
                WriteValue(properties[name], sb);
        }
        sb.Append('}');
    }

    private static void WriteValue(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, sb, injectHeartbeatDefault: false);
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var items = element.EnumerateArray().ToList();
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0) sb.Append(',');
                    WriteValue(items[index], sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Transport-independent NDP 0.12 in-memory registry state machine.
/// </summary>
public sealed class NdpRegistryProfile
{
    private sealed record Entry(
        JsonElement Frame,
        string SignedDigest,
        DateTimeOffset ExpiresAt);

    private readonly Dictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _highestSequences =
        new(StringComparer.Ordinal);

    /// <summary>Registry security profile name.</summary>
    public string SecurityProfile { get; }

    /// <summary>Creates an empty portable registry.</summary>
    public NdpRegistryProfile(string securityProfile = "local-dev")
    {
        SecurityProfile = securityProfile;
    }

    /// <summary>
    /// Applies one already identity-bound AnnounceFrame at the supplied receipt time.
    /// </summary>
    public NdpRegistryAdmission ApplyAnnounce(
        JsonElement frame,
        bool signatureValid,
        DateTimeOffset receivedAt)
    {
        if (!signatureValid)
            return Reject(NdpErrorCodes.AnnounceSignatureInvalid);

        if (frame.ValueKind != JsonValueKind.Object ||
            !TryString(frame, "nid", out var nid) ||
            !TryDate(frame, "timestamp", out var timestamp))
            return Reject(NdpErrorCodes.AnnounceProfileViolation);

        var sequencePresent = frame.TryGetProperty("graph_seq", out _);
        var hasSequence = TryUInt64(frame, "graph_seq", out var sequence);
        if ((sequencePresent && !hasSequence) ||
            (!sequencePresent && SecurityProfile != "local-dev") ||
            !TryUInt64(frame, "ttl", out var ttl) ||
            ttl > uint.MaxValue)
            return Reject(NdpErrorCodes.AnnounceProfileViolation);

        if (!BridgeShapeIsValid(frame))
            return Reject(NdpErrorCodes.AnnounceProfileViolation);

        if (SecurityProfile != "local-dev" &&
            Math.Abs((receivedAt - timestamp).TotalSeconds) > 300)
            return Reject(NdpErrorCodes.AnnounceSignatureInvalid);

        var digest = NdpAnnounceCanonicalizer.Digest(frame);
        if (_highestSequences.TryGetValue(nid, out var highest))
        {
            if (sequence < highest)
                return Reject(NdpErrorCodes.GraphSeqRollback);

            if (sequence == highest)
            {
                if (!_entries.TryGetValue(nid, out var current))
                    return new NdpRegistryAdmission(NdpRegistryDecision.Duplicate);
                if (!string.Equals(
                        current.SignedDigest,
                        digest,
                        StringComparison.Ordinal))
                    return Reject(NdpErrorCodes.AnnounceConflict);

                if (SameAdvisoryLiveness(current.Frame, frame))
                    return new NdpRegistryAdmission(NdpRegistryDecision.Duplicate);

                var refreshedExpiry = FreshnessDeadline(frame);
                if (refreshedExpiry <= receivedAt)
                    return Reject(NdpErrorCodes.AnnounceStale);
                _entries[nid] = new Entry(frame.Clone(), digest, refreshedExpiry);
                return new NdpRegistryAdmission(NdpRegistryDecision.Refreshed);
            }
        }

        if (ttl == 0)
        {
            _highestSequences[nid] = sequence;
            _entries.Remove(nid);
            return new NdpRegistryAdmission(NdpRegistryDecision.Removed);
        }

        var expiresAt = FreshnessDeadline(frame);
        if (expiresAt <= receivedAt)
            return Reject(NdpErrorCodes.AnnounceStale);

        _highestSequences[nid] = sequence;
        _entries[nid] = new Entry(frame.Clone(), digest, expiresAt);
        return new NdpRegistryAdmission(NdpRegistryDecision.Accepted);
    }

    /// <summary>Returns live NIDs in deterministic ordinal order.</summary>
    public IReadOnlyList<string> LiveNids(DateTimeOffset now) =>
        _entries
            .Where(pair => pair.Value.ExpiresAt > now)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Returns a snapshot of retained per-NID replay fences.</summary>
    public IReadOnlyDictionary<string, ulong> HighestSequences =>
        new SortedDictionary<string, ulong>(
            _highestSequences,
            StringComparer.Ordinal);

    /// <summary>Reports whether any retained entry is stale at <paramref name="now"/>.</summary>
    public bool HasStaleEntry(DateTimeOffset now) =>
        _entries.Values.Any(entry => entry.ExpiresAt <= now);

    /// <summary>Resolves the unique live Anchor with the highest cluster epoch.</summary>
    public NdpClusterSelection ResolveCluster(
        string clusterAnchor,
        DateTimeOffset now)
    {
        var members = _entries
            .Where(pair =>
                pair.Value.ExpiresAt > now &&
                StringValue(pair.Value.Frame, "cluster_anchor") == clusterAnchor &&
                Roles(pair.Value.Frame).Contains("anchor", StringComparer.Ordinal))
            .Select(pair => (
                Nid: pair.Key,
                Epoch: UInt64Value(pair.Value.Frame, "cluster_epoch") ?? 1UL))
            .ToList();
        if (members.Count == 0)
            return new NdpClusterSelection(null, null);

        var top = members.Max(member => member.Epoch);
        var leaders = members
            .Where(member => member.Epoch == top)
            .OrderBy(member => member.Nid, StringComparer.Ordinal)
            .ToList();
        return leaders.Count == 1
            ? new NdpClusterSelection(leaders[0].Nid, top)
            : new NdpClusterSelection(null, null, NdpErrorCodes.ClusterSplit);
    }

    /// <summary>Returns direction-specific live Bridge candidates sorted by NID.</summary>
    public IReadOnlyList<string> DiscoverBridges(
        string direction,
        string protocol,
        DateTimeOffset now)
    {
        var field = direction switch
        {
            "inbound" => "bridge_inbound_protocols",
            "outbound" => "bridge_protocols",
            _ => throw new ArgumentException(
                "Bridge direction must be 'inbound' or 'outbound'.",
                nameof(direction)),
        };

        return _entries
            .Where(pair =>
                pair.Value.ExpiresAt > now &&
                StringValue(pair.Value.Frame, "health") != "draining" &&
                IsBridge(pair.Value.Frame) &&
                Strings(pair.Value.Frame, field)
                    .Contains(protocol, StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static NdpRegistryAdmission Reject(string errorCode) =>
        new(NdpRegistryDecision.Rejected, errorCode);

    private static bool BridgeShapeIsValid(JsonElement frame)
    {
        if (!TryProtocolList(frame, "bridge_protocols", out var outboundPresent, out var outboundCount) ||
            !TryProtocolList(frame, "bridge_inbound_protocols", out var inboundPresent, out var inboundCount))
        {
            return false;
        }

        return IsBridge(frame)
            ? outboundCount + inboundCount > 0
            : !outboundPresent && !inboundPresent;
    }

    private static bool TryProtocolList(
        JsonElement frame,
        string propertyName,
        out bool present,
        out int count)
    {
        present = frame.TryGetProperty(propertyName, out var property);
        count = 0;
        if (!present)
            return true;
        if (property.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                return false;
            }
            count++;
        }
        return true;
    }

    private static bool IsBridge(JsonElement frame) =>
        Roles(frame).Contains("bridge", StringComparer.Ordinal) ||
        StringValue(frame, "node_type") == "bridge";

    private static IReadOnlyList<string> Roles(JsonElement frame) =>
        Strings(frame, "node_roles");

    private static IReadOnlyList<string> Strings(
        JsonElement frame,
        string propertyName)
    {
        if (!frame.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
            return [];
        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static DateTimeOffset FreshnessDeadline(JsonElement frame)
    {
        var source = TryDate(frame, "last_seen", out var lastSeen)
            ? lastSeen
            : DateTimeOffset.Parse(
                frame.GetProperty("timestamp").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
        var ttl = UInt64Value(frame, "ttl") ?? 0;
        return source.AddSeconds(ttl);
    }

    private static bool SameAdvisoryLiveness(
        JsonElement left,
        JsonElement right) =>
        StringValue(left, "health") == StringValue(right, "health") &&
        StringValue(left, "last_seen") == StringValue(right, "last_seen");

    private static string? StringValue(
        JsonElement frame,
        string propertyName) =>
        frame.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ulong? UInt64Value(
        JsonElement frame,
        string propertyName) =>
        TryUInt64(frame, propertyName, out var value) ? value : null;

    private static bool TryString(
        JsonElement frame,
        string propertyName,
        out string value)
    {
        value = StringValue(frame, propertyName) ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryUInt64(
        JsonElement frame,
        string propertyName,
        out ulong value)
    {
        value = 0;
        return frame.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt64(out value);
    }

    private static bool TryDate(
        JsonElement frame,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;
        return frame.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   property.GetString(),
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal,
                   out value);
    }
}
