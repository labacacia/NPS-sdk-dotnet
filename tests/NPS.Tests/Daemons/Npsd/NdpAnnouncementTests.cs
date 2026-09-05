// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NPS.Daemon.Npsd;
using NPS.Daemon.Registry;
using NPS.NDP.Frames;
using NPS.NDP.Registry;

namespace NPS.Tests.Daemons.Npsd;

public sealed class NdpAnnouncementTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task Managed_agent_emits_ephemeral_announce_signed_by_its_ident_key()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var issued = await IssueAsync(fx, "announced");
        var pubKey = issued.GetProperty("frame").GetProperty("pub_key").GetString()!;
        var nid = issued.GetProperty("frame").GetProperty("nid").GetString()!;
        var capture = new CaptureHandler();
        using var client = new HttpClient(capture);

        var service = fx.App.Services.GetRequiredService<NdpAnnouncementService>();
        Assert.Equal(1, await service.PublishOnceAsync(client, 60));

        var announceJson = Assert.Single(capture.Bodies);
        using var document = JsonDocument.Parse(announceJson);
        var root = document.RootElement;
        Assert.Equal(nid, root.GetProperty("nid").GetString());
        Assert.Equal("ephemeral", root.GetProperty("activation_mode").GetString());
        Assert.Equal(60, root.GetProperty("ttl").GetInt32());
        Assert.Equal(1UL, root.GetProperty("graph_seq").GetUInt64());
        Assert.Equal("nps-native", root.GetProperty("addresses")[0].GetProperty("protocol").GetString());
        Assert.True(NdpAnnounceCanonicalizer.Verify(
            root,
            pubKey,
            root.GetProperty("signature").GetString()!));

        var frame = JsonSerializer.Deserialize<AnnounceFrame>(announceJson, s_json)!;
        var tampered = frame with { Capabilities = [.. frame.Capabilities, "tampered"] };
        var tamperedJson = JsonSerializer.SerializeToElement(tampered, s_json);
        Assert.False(NdpAnnounceCanonicalizer.Verify(tamperedJson, pubKey, frame.Signature));

        Assert.Equal(1, await service.PublishOnceAsync(client, 0));
        using var offlineDocument = JsonDocument.Parse(capture.Bodies[1]);
        var offline = offlineDocument.RootElement;
        Assert.Equal(0, offline.GetProperty("ttl").GetInt32());
        Assert.Equal("draining", offline.GetProperty("health").GetString());
        Assert.Equal(2UL, offline.GetProperty("graph_seq").GetUInt64());
        Assert.True(NdpAnnounceCanonicalizer.Verify(
            offline,
            pubKey,
            offline.GetProperty("signature").GetString()!));
    }

    [Fact]
    public async Task Liveness_sequence_and_managed_key_survive_restart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"npsd-announce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            string pubKey;
            string nid;
            await using (var first = await NpsdTestServerFixture.CreatePersistentAsync(dataDir))
            {
                var issued = await IssueAsync(first, "restartable");
                pubKey = issued.GetProperty("frame").GetProperty("pub_key").GetString()!;
                nid = issued.GetProperty("frame").GetProperty("nid").GetString()!;
                var capture = new CaptureHandler();
                using var client = new HttpClient(capture);
                var service = first.App.Services.GetRequiredService<NdpAnnouncementService>();
                Assert.Equal(1, await service.PublishOnceAsync(client, 60));
                Assert.Equal(1UL, ReadSingle(capture).GetProperty("graph_seq").GetUInt64());
            }

            await using (var second = await NpsdTestServerFixture.CreatePersistentAsync(dataDir))
            {
                var capture = new CaptureHandler();
                using var client = new HttpClient(capture);
                var service = second.App.Services.GetRequiredService<NdpAnnouncementService>();
                Assert.Equal(1, await service.PublishOnceAsync(client, 60));
                var announce = ReadSingle(capture);
                Assert.Equal(nid, announce.GetProperty("nid").GetString());
                Assert.Equal(2UL, announce.GetProperty("graph_seq").GetUInt64());
                Assert.True(NdpAnnounceCanonicalizer.Verify(
                    announce,
                    pubKey,
                    announce.GetProperty("signature").GetString()!));
            }
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* leave for ops */ }
        }
    }

    [Fact]
    public async Task Byo_key_and_revoked_agents_are_not_auto_announced()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        using var byoKey = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
        var byoPub = NPS.NIP.Crypto.NipSigner.EncodePublicKey(byoKey.PublicKey);
        var byoResponse = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier = "byo",
            capabilities = new[] { "nwp:query" },
            agent_pub_key = byoPub,
        });
        Assert.Equal(HttpStatusCode.Created, byoResponse.StatusCode);

        var managed = await IssueAsync(fx, "revoked");
        var nid = managed.GetProperty("frame").GetProperty("nid").GetString()!;
        var revoke = await fx.Client.PostAsJsonAsync(
            $"/v1/agents/{Uri.EscapeDataString(nid)}/revoke",
            new { reason = "cessation" });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var service = fx.App.Services.GetRequiredService<NdpAnnouncementService>();
        Assert.Equal(0, await service.PublishOnceAsync(client, 60));
        Assert.Empty(capture.Bodies);
    }

    [Fact]
    public async Task Signed_announce_is_accepted_and_resolved_by_local_registry()
    {
        var registryOptions = new RegistryOptions
        {
            Nid = "urn:nps:agent:registry.test:local",
            Profile = RegistryOptions.ProfileLocalDev,
            SqlitePath = null,
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        RegistryHost.WireServices(builder.Services, registryOptions);
        var registry = builder.Build();
        RegistryHost.WireRoutes(registry, registryOptions);
        await registry.StartAsync();
        using var registryClient = registry.GetTestClient();
        try
        {
            await using var fx = await NpsdTestServerFixture.CreateAsync();
            var issued = await IssueAsync(fx, "registry-roundtrip");
            var nid = issued.GetProperty("frame").GetProperty("nid").GetString()!;
            using var client = new HttpClient(new ForwardingHandler(registryClient));
            var service = fx.App.Services.GetRequiredService<NdpAnnouncementService>();

            Assert.Equal(1, await service.PublishOnceAsync(client, 60));

            var nidParts = nid.Split(':');
            var target = $"nwp://{nidParts[3]}/{nidParts[4]}";
            var response = await registryClient.GetAsync(
                $"/v1/resolve?target={Uri.EscapeDataString(target)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var resolved = await response.Content.ReadFromJsonAsync<JsonElement>(s_json);
            Assert.Equal(target, resolved.GetProperty("target").GetString());
            Assert.Equal(17433, resolved.GetProperty("resolved").GetProperty("port").GetInt32());
        }
        finally
        {
            try { await registry.StopAsync(); } catch { /* ignore */ }
            await registry.DisposeAsync();
        }
    }

    private static async Task<JsonElement> IssueAsync(NpsdTestServerFixture fx, string identifier)
    {
        var response = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier,
            capabilities = new[] { "nwp:query" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(s_json);
    }

    private static JsonElement ReadSingle(CaptureHandler handler)
    {
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        return document.RootElement.Clone();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class ForwardingHandler(HttpClient target) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(
                request.Method,
                request.RequestUri!.PathAndQuery);
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(
                    await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return await target.SendAsync(forwarded, cancellationToken);
        }
    }
}
