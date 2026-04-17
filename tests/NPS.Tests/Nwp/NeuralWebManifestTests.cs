// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.NWP.Http;
using NPS.NWP.Nwm;

namespace NPS.Tests.Nwp;

public sealed class NeuralWebManifestTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false,
    };

    private static NeuralWebManifest MakeMemoryNode() => new()
    {
        Nwp             = "0.2",
        NodeId          = "urn:nps:node:api.example.com:products",
        NodeType        = "memory",
        DisplayName     = "Products",
        WireFormats     = ["ncp-capsule", "msgpack", "json"],
        PreferredFormat = "msgpack",
        SchemaAnchors   = new Dictionary<string, string> { ["product"] = "sha256:" + new string('a', 64) },
        TokenizerSupport = ["cl100k_base", "claude"],
        Capabilities    = new NodeCapabilities
        {
            Query          = true,
            Stream         = true,
            Subscribe      = false,
            VectorSearch   = true,
            TokenBudgetHint = true,
            ExtFrame       = false,
        },
        Auth = new NodeAuth
        {
            Required      = true,
            IdentityType  = "nip-cert",
            TrustedIssuers = ["https://ca.example.com"],
            RequiredCapabilities = ["nwp:query"],
            ScopeCheck    = "prefix",
        },
        Endpoints = new NodeEndpoints
        {
            Query  = "nwp://api.example.com/products/query",
            Stream = "nwp://api.example.com/products/stream",
            Schema = "nwp://api.example.com/products/.schema",
        },
    };

    // ── Serialization ────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ProducesSnakeCaseKeys()
    {
        var manifest = MakeMemoryNode();
        var json     = JsonSerializer.Serialize(manifest, JsonOpts);

        Assert.Contains("\"node_id\"",         json);
        Assert.Contains("\"node_type\"",        json);
        Assert.Contains("\"wire_formats\"",     json);
        Assert.Contains("\"preferred_format\"", json);
        Assert.Contains("\"schema_anchors\"",   json);
        Assert.Contains("\"tokenizer_support\"",json);
    }

    [Fact]
    public void Serialize_NullFieldsOmitted()
    {
        var manifest = MakeMemoryNode();
        var json     = JsonSerializer.Serialize(manifest, JsonOpts);

        // Graph is null — must be absent
        Assert.DoesNotContain("\"graph\"", json);
    }

    [Fact]
    public void Deserialize_RoundTrip_PreservesAllFields()
    {
        var original = MakeMemoryNode();
        var json     = JsonSerializer.Serialize(original, JsonOpts);
        var result   = JsonSerializer.Deserialize<NeuralWebManifest>(json, JsonOpts)!;

        Assert.Equal(original.Nwp,             result.Nwp);
        Assert.Equal(original.NodeId,          result.NodeId);
        Assert.Equal(original.NodeType,        result.NodeType);
        Assert.Equal(original.DisplayName,     result.DisplayName);
        Assert.Equal(original.PreferredFormat, result.PreferredFormat);
        Assert.Equal(2,                        result.TokenizerSupport!.Count);
        Assert.Equal("cl100k_base",            result.TokenizerSupport[0]);
    }

    // ── NodeCapabilities ─────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_ExtFrame_SerializesCorrectly()
    {
        var caps = new NodeCapabilities { ExtFrame = true, Query = true };
        var json = JsonSerializer.Serialize(caps, JsonOpts);

        Assert.Contains("\"ext_frame\":true",        json);
        Assert.Contains("\"token_budget_hint\":false",json);
        Assert.Contains("\"vector_search\":false",   json);
    }

    // ── NodeAuth ─────────────────────────────────────────────────────────────

    [Fact]
    public void Auth_RequiredFalse_TrustedIssuersOmitted()
    {
        var auth = new NodeAuth { Required = false };
        var json = JsonSerializer.Serialize(auth, JsonOpts);

        Assert.DoesNotContain("trusted_issuers", json);
        Assert.DoesNotContain("required_capabilities", json);
        Assert.DoesNotContain("ocsp_url", json);
    }

    // ── NodeGraph ────────────────────────────────────────────────────────────

    [Fact]
    public void ComplexNode_WithGraph_RoundTrip()
    {
        var manifest = MakeMemoryNode() with
        {
            NodeType = "complex",
            Graph = new NodeGraph
            {
                Refs = [new NodeGraphRef("user", "nwp://api.example.com/users")],
                MaxDepth = 3,
            },
        };

        var json   = JsonSerializer.Serialize(manifest, JsonOpts);
        var result = JsonSerializer.Deserialize<NeuralWebManifest>(json, JsonOpts)!;

        Assert.NotNull(result.Graph);
        Assert.Single(result.Graph.Refs);
        Assert.Equal("user",                         result.Graph.Refs[0].Rel);
        Assert.Equal("nwp://api.example.com/users",  result.Graph.Refs[0].Node);
        Assert.Equal(3u,                             result.Graph.MaxDepth);
    }

    // ── MIME type constants ──────────────────────────────────────────────────

    [Fact]
    public void NwpHttpHeaders_MimeManifest_MatchesSpec()
    {
        Assert.Equal("application/nwp-manifest+json", NwpHttpHeaders.MimeManifest);
    }
}
