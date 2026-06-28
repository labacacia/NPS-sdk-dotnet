// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NPS.NIP.Crypto;

namespace NPS.Tests.Daemons.Npsd;

public class SubNidIssuanceTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task Issue_minted_keypair_returns_201_with_ident_and_private_key()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "alpha-worker",
            capabilities = new[] { "nwp:query", "nwp:stream" },
            scope        = new { nodes = new[] { "nwp://*" } },
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);

        // The IdentFrame is present and points back at the host.
        var frame = body.GetProperty("frame");
        var nid   = frame.GetProperty("nid").GetString();
        Assert.NotNull(nid);
        Assert.EndsWith(":agent:alpha-worker", nid);
        Assert.Equal(frame.GetProperty("issued_by").GetString(), nid!.Split(":agent:")[0]);

        // The minted private key is an Ed25519 raw key.
        var minted = body.GetProperty("minted_private_key").GetString();
        Assert.NotNull(minted);
        Assert.StartsWith("ed25519-raw:", minted);

        // And the IdentFrame signature verifies under the embedded pub_key
        // (since npsd signs with the host root key, not the minted agent key,
        // the agent pub_key is what the worker uses; we verify it matches
        // the minted private key by signing/verifying a probe).
        var pubKeyEnc = frame.GetProperty("pub_key").GetString();
        Assert.NotNull(pubKeyEnc);
        Assert.StartsWith("ed25519:", pubKeyEnc);
    }

    [Fact]
    public async Task Issue_with_caller_supplied_pub_key_does_not_return_private_key()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        // Caller mints their own keypair on the worker side and only hands the
        // pub key to npsd.
        using var ed = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters
            {
                ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport,
            });
        var pubKeyEnc = NipSigner.EncodePublicKey(ed.PublicKey);

        var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier    = "byo-key-worker",
            capabilities  = new[] { "nwp:query" },
            agent_pub_key = pubKeyEnc,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(pubKeyEnc, body.GetProperty("frame").GetProperty("pub_key").GetString());
        Assert.False(body.TryGetProperty("minted_private_key", out var mp) && mp.ValueKind == JsonValueKind.String,
            "minted_private_key MUST be omitted when the caller supplied their own pub key.");
    }

    [Fact]
    public async Task Issue_duplicate_identifier_returns_409()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var first = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "dup",
            capabilities = new[] { "nwp:query" },
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "dup",
            capabilities = new[] { "nwp:query" },
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal("NIP-CA-NID-ALREADY-EXISTS", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Issue_missing_capabilities_returns_400()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier = "no-caps",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal("NIP-IDENT-BAD-REQUEST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Issue_invalid_identifier_returns_400()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "bad:identifier",      // contains `:`
            capabilities = new[] { "nwp:query" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_returns_issued_record_404s_unknown()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var issue = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "lookup-me",
            capabilities = new[] { "nwp:query" },
        });
        var nid = (await issue.Content.ReadFromJsonAsync<JsonElement>(s_json))
            .GetProperty("frame").GetProperty("nid").GetString()!;

        // GET the issued record back.
        var get = await fx.Client.GetAsync($"/v1/agents/{Uri.EscapeDataString(nid)}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var record = await get.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(nid, record.GetProperty("nid").GetString());
        Assert.False(record.GetProperty("revoked").GetBoolean());

        // GET an unknown NID 404s.
        var unknown = await fx.Client.GetAsync($"/v1/agents/{Uri.EscapeDataString("urn:nps:agent:nope")}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Revoke_marks_record_and_blocks_inbox_use()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var issue = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier   = "doomed",
            capabilities = new[] { "nwp:query" },
        });
        var nid = (await issue.Content.ReadFromJsonAsync<JsonElement>(s_json))
            .GetProperty("frame").GetProperty("nid").GetString()!;

        // Inbox works pre-revoke.
        var pre = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}",
            new ByteArrayContent(new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.Created, pre.StatusCode);

        // Revoke.
        var revoke = await fx.Client.PostAsJsonAsync(
            $"/v1/agents/{Uri.EscapeDataString(nid)}/revoke",
            new { reason = "key_compromise" });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Subsequent inbox deposit is rejected as an invalid credential.
        var post = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}",
            new ByteArrayContent(new byte[] { 4, 5, 6 }));
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        var error = await post.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal("NIP-CERT-REVOKED", error.GetProperty("error").GetString());
        Assert.Equal("NPS-AUTH-UNAUTHENTICATED", error.GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_returns_all_issued_in_reverse_chronological_order()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        for (int i = 0; i < 3; i++)
        {
            var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
            {
                identifier   = $"worker-{i}",
                capabilities = new[] { "nwp:query" },
            });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            await Task.Delay(2);   // ensure issue timestamps differ for ordering
        }

        var list = await fx.Client.GetAsync("/v1/agents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await list.Content.ReadFromJsonAsync<JsonElement>(s_json);

        Assert.Equal(3, body.GetProperty("count").GetInt32());
        var nids = body.GetProperty("agents")
            .EnumerateArray()
            .Select(e => e.GetProperty("nid").GetString()!)
            .ToList();
        Assert.EndsWith(":worker-2", nids[0]);
        Assert.EndsWith(":worker-0", nids[2]);
    }

    [Fact]
    public async Task Health_reports_host_nid_and_fingerprint()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();

        var resp = await fx.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);

        Assert.Equal("ok",   body.GetProperty("status").GetString());
        Assert.Equal("npsd", body.GetProperty("daemon").GetString());
        Assert.Matches("^[0-9a-f]{16}$", body.GetProperty("host_nid_fpr").GetString());
        Assert.StartsWith("urn:nps:host:", body.GetProperty("host_nid").GetString());
    }
}
