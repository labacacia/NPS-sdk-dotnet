// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Bridge;
using NPS.NWP.Frames;
using NPS.NWP.Http;

namespace NPS.Tests.Nwp;

public sealed class BridgeNodeTests
{
    [Fact]
    public void BridgeTargetParser_ReadsNestedTargetAndExtras()
    {
        using var doc = JsonDocument.Parse("""
        {
          "bridge_target": {
            "protocol": "http",
            "endpoint": "https://api.example.test/v1/search",
            "extras": {
              "method": "GET",
              "headers": { "x-agent": "nps" }
            }
          }
        }
        """);

        var frame = new ActionFrame
        {
            ActionId = "bridge.dispatch",
            Params = doc.RootElement.Clone()
        };

        var target = BridgeTargetParser.FromActionFrame(frame);

        Assert.Equal(BridgeProtocols.Http, target.Protocol);
        Assert.Equal("https://api.example.test/v1/search", target.Endpoint);
        Assert.Equal("GET", BridgeTargetParser.GetString(target, "method"));
        Assert.True(BridgeTargetParser.TryGetJson(target, "headers", out var headers));
        Assert.Equal("nps", headers.GetProperty("x-agent").GetString());
    }

    [Fact]
    public async Task BridgeNode_DispatchesToRegisteredProtocol()
    {
        var dispatcher = new EchoDispatcher();
        var node = new BridgeNode(new BridgeDispatcherRegistry().Register(dispatcher));
        using var doc = JsonDocument.Parse("""
        {
          "bridge_target": {
            "protocol": "echo",
            "endpoint": "echo://local"
          }
        }
        """);

        var caps = await node.DispatchAsync(new ActionFrame
        {
            ActionId = "bridge.dispatch",
            Params = doc.RootElement.Clone()
        });

        Assert.Equal("nps://bridge/echo/v1", caps.AnchorRef);
        Assert.Equal("echo://local", caps.Data[0].GetProperty("endpoint").GetString());
    }

    [Fact]
    public void BridgeDispatcherRegistry_CreateDefault_RegistersBuiltInProtocols()
    {
        var registry = BridgeDispatcherRegistry.CreateDefault(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        Assert.Equal(
            new[] { BridgeProtocols.A2a, BridgeProtocols.Grpc, BridgeProtocols.Http, BridgeProtocols.Mcp },
            registry.Protocols.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddBridgeNode_RegistersDefaultDispatchersFromNamedHttpClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeNode();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<BridgeDispatcherRegistry>();

        Assert.Equal(
            new[] { BridgeProtocols.A2a, BridgeProtocols.Grpc, BridgeProtocols.Http, BridgeProtocols.Mcp },
            registry.Protocols.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpBridgeDispatcher_ForwardsBodyHeadersAndWrapsJsonResponse()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var dispatcher = new HttpBridgeDispatcher(new HttpClient(new DelegateHandler(async request =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"accepted":true}""")
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Headers.TryAddWithoutValidation("x-upstream", "ok");
            await Task.CompletedTask;
            return response;
        })));

        using var doc = JsonDocument.Parse("""
        {
          "bridge_target": {
            "protocol": "http",
            "endpoint": "https://api.example.test/run",
            "method": "PUT",
            "headers": { "x-agent": "nps" }
          },
          "body": { "task": "sync" }
        }
        """);

        var caps = await dispatcher.DispatchAsync(
            new ActionFrame
            {
                ActionId = "bridge.dispatch",
                Params = doc.RootElement.Clone(),
                TimeoutMs = 5000
            },
            BridgeTargetParser.FromJson(doc.RootElement.GetProperty("bridge_target")));

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured.Method);
        Assert.Equal("https://api.example.test/run", captured.RequestUri!.ToString());
        Assert.True(captured.Headers.TryGetValues("x-agent", out var values));
        Assert.Equal("nps", Assert.Single(values));
        using (var sentBody = JsonDocument.Parse(capturedBody!))
        {
            Assert.Equal("sync", sentBody.RootElement.GetProperty("task").GetString());
        }

        var row = Assert.Single(caps.Data);
        Assert.Equal(HttpBridgeDispatcher.ResponseAnchorRef, caps.AnchorRef);
        Assert.Equal(1u, caps.Count);
        Assert.Equal(202, row.GetProperty("status_code").GetInt32());
        Assert.True(row.GetProperty("success").GetBoolean());
        Assert.True(row.GetProperty("body").GetProperty("accepted").GetBoolean());
        Assert.Equal("ok", row.GetProperty("headers").GetProperty("x-upstream").GetString());
    }

    [Fact]
    public async Task HttpBridgeDispatcher_RejectsNonHttpEndpoint()
    {
        var dispatcher = new HttpBridgeDispatcher(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        var target = new BridgeTarget(BridgeProtocols.Http, "file:///etc/passwd");

        var ex = await Assert.ThrowsAsync<BridgeDispatchException>(() =>
            dispatcher.DispatchAsync(new ActionFrame { ActionId = "bridge.dispatch" }, target));

        Assert.Equal(BridgeErrorCodes.EndpointInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task HttpBridgeDispatcher_RejectsPrivateEndpointByDefault()
    {
        var dispatcher = new HttpBridgeDispatcher(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        var target = new BridgeTarget(BridgeProtocols.Http, "http://127.0.0.1/internal");

        var ex = await Assert.ThrowsAsync<BridgeDispatchException>(() =>
            dispatcher.DispatchAsync(new ActionFrame { ActionId = "bridge.dispatch" }, target));

        Assert.Equal(BridgeErrorCodes.EndpointInvalid, ex.ErrorCode);
        Assert.Contains("SSRF", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpBridgeDispatcher_AllowsPrivateEndpointWhenExplicitlyDisabled()
    {
        HttpRequestMessage? captured = null;
        var dispatcher = new HttpBridgeDispatcher(new HttpClient(new DelegateHandler(request =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            });
        })));

        using var targetDoc = JsonDocument.Parse("""
        {
          "protocol": "http",
          "endpoint": "http://127.0.0.1/internal",
          "reject_private": false
        }
        """);

        await dispatcher.DispatchAsync(
            new ActionFrame { ActionId = "bridge.dispatch" },
            BridgeTargetParser.FromJson(targetDoc.RootElement));

        Assert.Equal("http://127.0.0.1/internal", captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task McpBridgeDispatcher_RejectsEndpointOutsideAllowedPrefixes()
    {
        var dispatcher = new McpBridgeDispatcher(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        using var targetDoc = JsonDocument.Parse("""
        {
          "protocol": "mcp",
          "endpoint": "https://mcp.example.test/rpc",
          "allowed_prefixes": [ "https://trusted.example.test/" ]
        }
        """);

        var ex = await Assert.ThrowsAsync<BridgeDispatchException>(() =>
            dispatcher.DispatchAsync(
                new ActionFrame { ActionId = "bridge.dispatch" },
                BridgeTargetParser.FromJson(targetDoc.RootElement)));

        Assert.Equal(BridgeErrorCodes.EndpointInvalid, ex.ErrorCode);
        Assert.Contains("allowed_prefixes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://trusted.example.test.evil/rpc", "https://trusted.example.test/")]
    [InlineData("https://trusted.example.test/v10/rpc", "https://trusted.example.test/v1")]
    public async Task McpBridgeDispatcher_RejectsAllowedPrefixBypass(
        string endpoint,
        string allowedPrefix)
    {
        var dispatcher = new McpBridgeDispatcher(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        using var targetDoc = JsonDocument.Parse($$"""
        {
          "protocol": "mcp",
          "endpoint": "{{endpoint}}",
          "allowed_prefixes": [ "{{allowedPrefix}}" ]
        }
        """);

        var ex = await Assert.ThrowsAsync<BridgeDispatchException>(() =>
            dispatcher.DispatchAsync(
                new ActionFrame { ActionId = "bridge.dispatch" },
                BridgeTargetParser.FromJson(targetDoc.RootElement)));

        Assert.Equal(BridgeErrorCodes.EndpointInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task GrpcBridgeDispatcher_PostsUnaryJsonGrpcFrame()
    {
        HttpRequestMessage? captured = null;
        byte[]? capturedBody = null;
        var dispatcher = new GrpcBridgeDispatcher(new HttpClient(new DelegateHandler(async request =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(GrpcFrame("""{"ok":true}"""))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc+json");
            response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
            return response;
        })));

        using var doc = JsonDocument.Parse("""
        {
          "bridge_target": {
            "protocol": "grpc",
            "endpoint": "https://grpc.example.test/orders.Orders/Lookup"
          },
          "grpc_message": { "order_id": "42" }
        }
        """);

        var caps = await dispatcher.DispatchAsync(
            new ActionFrame
            {
                ActionId = "bridge.dispatch",
                Params = doc.RootElement.Clone(),
                TimeoutMs = 5000
            },
            BridgeTargetParser.FromJson(doc.RootElement.GetProperty("bridge_target")));

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal(new Version(2, 0), captured.Version);
        Assert.Equal("application/grpc+json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.True(captured.Headers.TryGetValues("te", out var te));
        Assert.Equal("trailers", Assert.Single(te));

        using (var sent = JsonDocument.Parse(ReadGrpcPayload(capturedBody!)))
        {
            Assert.Equal("42", sent.RootElement.GetProperty("order_id").GetString());
        }

        var row = Assert.Single(caps.Data);
        Assert.Equal(GrpcBridgeDispatcher.ResponseAnchorRef, caps.AnchorRef);
        Assert.Equal("0", row.GetProperty("grpc_status").GetString());
        Assert.True(row.GetProperty("messages")[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task GrpcBridgeDispatcher_RejectsPrivateEndpointByDefault()
    {
        var dispatcher = new GrpcBridgeDispatcher(new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));

        var target = new BridgeTarget(BridgeProtocols.Grpc, "https://localhost/orders.Orders/Lookup");

        var ex = await Assert.ThrowsAsync<BridgeDispatchException>(() =>
            dispatcher.DispatchAsync(new ActionFrame { ActionId = "bridge.dispatch" }, target));

        Assert.Equal(BridgeErrorCodes.EndpointInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task McpBridgeDispatcher_PostsJsonRpcToolsCall()
    {
        string? capturedBody = null;
        var dispatcher = new McpBridgeDispatcher(new HttpClient(new DelegateHandler(async request =>
        {
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"jsonrpc":"2.0","id":"req-1","result":{"content":[{"type":"text","text":"ok"}],"isError":false}}
                """)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        })));

        using var doc = JsonDocument.Parse("""
        {
          "bridge_target": {
            "protocol": "mcp",
            "endpoint": "https://mcp.example.test/rpc",
            "headers": { "authorization": "Bearer test" }
          },
          "id": "req-1",
          "params": {
            "name": "orders.lookup",
            "arguments": { "order_id": "42" }
          }
        }
        """);

        var caps = await dispatcher.DispatchAsync(
            new ActionFrame
            {
                ActionId = "bridge.dispatch",
                Params = doc.RootElement.Clone(),
                TimeoutMs = 5000
            },
            BridgeTargetParser.FromJson(doc.RootElement.GetProperty("bridge_target")));

        using (var sent = JsonDocument.Parse(capturedBody!))
        {
            Assert.Equal("2.0", sent.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("req-1", sent.RootElement.GetProperty("id").GetString());
            Assert.Equal("tools/call", sent.RootElement.GetProperty("method").GetString());
            Assert.Equal("orders.lookup", sent.RootElement.GetProperty("params").GetProperty("name").GetString());
            Assert.Equal("42", sent.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("order_id").GetString());
        }

        var row = Assert.Single(caps.Data);
        Assert.Equal(McpBridgeDispatcher.ResponseAnchorRef, caps.AnchorRef);
        Assert.Equal(200, row.GetProperty("status_code").GetInt32());
        Assert.False(row.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task A2aBridgeDispatcher_AllowsExplicitMethodAndRpcParamsFromTarget()
    {
        string? capturedBody = null;
        var dispatcher = new A2aBridgeDispatcher(new HttpClient(new DelegateHandler(async request =>
        {
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"jsonrpc":"2.0","id":"task-1","result":{"id":"task-1","status":{"state":"completed"}}}
                """)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        })));

        using var targetDoc = JsonDocument.Parse("""
        {
          "protocol": "a2a",
          "endpoint": "https://agent.example.test/a2a",
          "rpc_method": "tasks/send",
          "id": "task-1",
          "rpc_params": {
            "id": "task-1",
            "message": {
              "role": "user",
              "parts": [ { "type": "text", "text": "hello" } ]
            }
          }
        }
        """);

        var caps = await dispatcher.DispatchAsync(
            new ActionFrame { ActionId = "bridge.dispatch", TimeoutMs = 5000 },
            BridgeTargetParser.FromJson(targetDoc.RootElement));

        using (var sent = JsonDocument.Parse(capturedBody!))
        {
            Assert.Equal("tasks/send", sent.RootElement.GetProperty("method").GetString());
            Assert.Equal("task-1", sent.RootElement.GetProperty("id").GetString());
            Assert.Equal("hello", sent.RootElement
                .GetProperty("params")
                .GetProperty("message")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString());
        }

        var row = Assert.Single(caps.Data);
        Assert.Equal(A2aBridgeDispatcher.ResponseAnchorRef, caps.AnchorRef);
        Assert.Equal("completed", row.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task BridgeNodeMiddleware_ExposesManifestAndActions()
    {
        using var server = await BuildBridgeServer();
        using var client = server.GetTestClient();

        var nwm = await client.GetAsync("/bridge/.nwm");
        Assert.Equal(HttpStatusCode.OK, nwm.StatusCode);
        Assert.Equal(NwpHttpHeaders.MimeManifest, nwm.Content.Headers.ContentType!.MediaType);
        using (var doc = JsonDocument.Parse(await nwm.Content.ReadAsStringAsync()))
        {
            Assert.Equal("bridge", doc.RootElement.GetProperty("node_type").GetString());
            Assert.Contains("echo", doc.RootElement.GetProperty("bridge_protocols").EnumerateArray()
                .Select(p => p.GetString()));
        }

        var actions = await client.GetAsync("/bridge/actions");
        Assert.Equal(HttpStatusCode.OK, actions.StatusCode);
        Assert.Contains("bridge.dispatch", await actions.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BridgeNodeMiddleware_InvokeDispatchesActionFrame()
    {
        using var server = await BuildBridgeServer();
        using var client = server.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/invoke")
        {
            Content = new StringContent("""
            {
              "action_id": "bridge.dispatch",
              "params": {
                "bridge_target": {
                  "protocol": "echo",
                  "endpoint": "echo://middleware"
                }
              }
            }
            """, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("nps://bridge/echo/v1", doc.RootElement.GetProperty("anchor_ref").GetString());
        Assert.Equal("echo://middleware", doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("endpoint")
            .GetString());
    }

    private sealed class EchoDispatcher : IBridgeDispatcher
    {
        public string Protocol => "echo";

        public Task<CapsFrame> DispatchAsync(
            ActionFrame frame,
            BridgeTarget target,
            CancellationToken cancellationToken = default)
        {
            var element = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["endpoint"] = target.Endpoint
            });
            return Task.FromResult(new CapsFrame
            {
                AnchorRef = "nps://bridge/echo/v1",
                Count = 1,
                Data = new[] { element }
            });
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request);
    }

    private static byte[] GrpcFrame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var wire = new byte[payload.Length + 5];
        wire[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(wire.AsSpan(5));
        return wire;
    }

    private static byte[] ReadGrpcPayload(byte[] wire)
    {
        Assert.True(wire.Length >= 5);
        Assert.Equal(0, wire[0]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(1, 4));
        Assert.Equal(length, (uint)(wire.Length - 5));
        return wire[5..];
    }

    private static async Task<IHost> BuildBridgeServer()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(new BridgeNodeOptions
                    {
                        NodeId = "bridge-test",
                        PathPrefix = "/bridge",
                    });
                    services.AddSingleton(new BridgeDispatcherRegistry()
                        .Register(new EchoDispatcher()));
                    services.AddSingleton<BridgeNode>();
                });
                web.Configure(app => app.UseBridgeNode());
            })
            .Build();

        await host.StartAsync();
        return host;
    }
}
