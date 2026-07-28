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
using NPS.Core.Frames;
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
    public void AddBridgeServer_RegistersInboundAdapters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeServer(options =>
        {
            options.PathPrefix = "/bridge";
            options.AddAction("orders.lookup", "Lookup an order.");
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<McpInboundServer>());
        Assert.NotNull(provider.GetRequiredService<A2aInboundServer>());
        Assert.NotEmpty(provider.GetRequiredService<IReadOnlyList<INwpBackend>>());
        Assert.True(provider.GetRequiredService<BridgeServerOptions>().RequireAuth);
    }

    [Fact]
    public async Task McpServerBridge_ListsToolsAndDispatchesToolCall()
    {
        ActionFrame? captured = null;
        var options = BuildInboundOptions(frame =>
        {
            captured = frame;
            return new CapsFrame
            {
                AnchorRef = "nps://orders/result/v1",
                Count = 1,
                Data = new[]
                {
                    JsonSerializer.SerializeToElement(new { order_id = "42", ok = true }),
                },
            };
        });
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var list = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("list-1"),
            Method = "tools/list",
        });

        Assert.Null(list.Error);
        Assert.True(list.Result.HasValue);
        Assert.Equal("bridge-inbound-test__orders_lookup",
            list.Result.Value.GetProperty("tools")[0].GetProperty("name").GetString());

        var call = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("call-1"),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new
            {
                name = "orders_lookup",
                arguments = new { order_id = "42" },
            }),
        });

        Assert.Null(call.Error);
        Assert.NotNull(captured);
        Assert.Equal("orders.lookup", captured.ActionId);
        Assert.Equal("42", captured.Params!.Value.GetProperty("order_id").GetString());
        Assert.True(call.Result.HasValue);
        Assert.False(call.Result.Value.GetProperty("isError").GetBoolean());
        using var returned = JsonDocument.Parse(call.Result.Value.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("nps://orders/result/v1", returned.RootElement.GetProperty("anchor_ref").GetString());
    }

    [Fact]
    public async Task McpServerBridge_StdioHandlesLineDelimitedJsonRpc()
    {
        var options = BuildInboundOptions(_ => new CapsFrame
        {
            AnchorRef = "nps://orders/result/v1",
            Count = 0,
            Data = Array.Empty<JsonElement>(),
        });
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));
        using var input = new StringReader("""
        {"jsonrpc":"2.0","id":"list-stdio","method":"tools/list"}
        """);
        using var output = new StringWriter();

        await bridge.RunStdioAsync(input, output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("list-stdio", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("bridge-inbound-test__orders_lookup", doc.RootElement.GetProperty("result")
            .GetProperty("tools")[0]
            .GetProperty("name")
            .GetString());
    }

    [Fact]
    public async Task A2aServerBridge_TasksSendDispatchesActionFrame()
    {
        ActionFrame? captured = null;
        var options = BuildInboundOptions(frame =>
        {
            captured = frame;
            return new CapsFrame
            {
                AnchorRef = "nps://orders/result/v1",
                Count = 1,
                Data = new[]
                {
                    JsonSerializer.SerializeToElement(new { ok = true }),
                },
            };
        });
        var bridge = new A2aInboundServer(options, BridgeServerBackends.Create(options));

        var response = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("task-rpc-1"),
            Method = "tasks/send",
            Params = JsonSerializer.SerializeToElement(new
            {
                id = "task-1",
                metadata = new
                {
                    skillId = "orders.lookup",
                    @params = new { order_id = "42" },
                },
                message = new
                {
                    role = "user",
                    parts = new[] { new { type = "text", text = "lookup 42" } },
                },
            }),
        });

        Assert.Null(response.Error);
        Assert.NotNull(captured);
        Assert.Equal("orders.lookup", captured.ActionId);
        Assert.Equal("42", captured.Params!.Value.GetProperty("order_id").GetString());
        Assert.True(response.Result.HasValue);
        Assert.Equal("completed", response.Result.Value.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("nps://orders/result/v1", response.Result.Value.GetProperty("artifacts")[0]
            .GetProperty("parts")[0]
            .GetProperty("data")
            .GetProperty("anchor_ref")
            .GetString());
    }

    [Fact]
    public async Task BridgeServerMiddleware_HandlesMcpHttpAndSse()
    {
        using var server = await BuildInboundBridgeServer(AllowBridgeAgent);
        using var client = server.GetTestClient();

        using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {
              "jsonrpc": "2.0",
              "id": "mcp-http-1",
              "method": "tools/call",
              "params": {
                "name": "orders_lookup",
                "arguments": { "order_id": "42" }
              }
            }
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(jsonRequest);

        var jsonResponse = await client.SendAsync(jsonRequest);
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        using (var doc = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("mcp-http-1", doc.RootElement.GetProperty("id").GetString());
            Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        using var sseRequest = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {"jsonrpc":"2.0","id":"mcp-sse-1","method":"tools/list"}
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(sseRequest);
        sseRequest.Headers.TryAddWithoutValidation("accept", "text/event-stream");

        var sseResponse = await client.SendAsync(sseRequest);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType!.MediaType);
        var sse = await sseResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: message", sse, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"mcp-sse-1\"", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeServerMiddleware_DefaultAuthRejectsMissingAgentHeader()
    {
        using var server = await BuildInboundBridgeServer();
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {"jsonrpc":"2.0","id":"auth-1","method":"tools/list"}
            """, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BridgeJsonRpcErrorCodes.InvalidRequest, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BridgeServerMiddleware_DefaultAuthRejectsInvalidAgentNid()
    {
        using var server = await BuildInboundBridgeServer();
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {"jsonrpc":"2.0","id":"auth-2","method":"tools/list"}
            """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(NwpHttpHeaders.Agent, "present-but-not-a-nid");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("valid X-NWP-Agent", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeServerMiddleware_DefaultAuthRejectsMissingVerifier()
    {
        using var server = await BuildInboundBridgeServer();
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {"jsonrpc":"2.0","id":"auth-3","method":"tools/list"}
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(request);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("verifier", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeServerMiddleware_CustomAgentVerifierCanRejectValidNid()
    {
        using var server = await BuildInboundBridgeServer(options =>
        {
            options.VerifyAgentAsync = (_, _, _) => ValueTask.FromResult(false);
        });
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {"jsonrpc":"2.0","id":"auth-3","method":"tools/list"}
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(request);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("rejected", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeServerMiddleware_RejectsRequestBodyOverConfiguredLimit()
    {
        using var server = await BuildInboundBridgeServer(options =>
        {
            AllowBridgeAgent(options);
            options.MaxRequestBodyBytes = 64;
        });
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {
              "jsonrpc": "2.0",
              "id": "too-large",
              "method": "tools/list",
              "params": { "padding": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" }
            }
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(request);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BridgeJsonRpcErrorCodes.InvalidRequest, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BridgeServerMiddleware_DispatchTimeoutReturns504()
    {
        using var server = await BuildInboundBridgeServer(options =>
        {
            AllowBridgeAgent(options);
            options.DispatchTimeoutMs = 25;
            options.DispatchAsync = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new CapsFrame
                {
                    AnchorRef = "nps://orders/result/v1",
                    Count = 0,
                    Data = Array.Empty<JsonElement>(),
                };
            };
        });
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bridge/mcp")
        {
            Content = new StringContent("""
            {
              "jsonrpc": "2.0",
              "id": "timeout-1",
              "method": "tools/call",
              "params": { "name": "orders_lookup", "arguments": { "order_id": "42" } }
            }
            """, Encoding.UTF8, "application/json"),
        };
        AddAgentHeader(request);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BridgeJsonRpcErrorCodes.UpstreamError, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BridgeServerMiddleware_ExposesA2aAgentCard()
    {
        using var server = await BuildInboundBridgeServer();
        using var client = server.GetTestClient();

        var response = await client.GetAsync("/bridge/.well-known/agent.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bridge-inbound-test", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("bridge-inbound-test__orders_lookup",
            doc.RootElement.GetProperty("skills")[0].GetProperty("id").GetString());
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
            "extras": {
              "method": "PUT",
              "headers": { "x-agent": "nps" }
            }
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
          "extras": {
            "allowed_prefixes": [ "https://trusted.example.test/" ]
          }
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
          "extras": {
            "allowed_prefixes": [ "{{allowedPrefix}}" ]
          }
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
            "extras": {
              "headers": { "authorization": "Bearer test" }
            }
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
          "extras": {
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

    private static async Task<IHost> BuildInboundBridgeServer(Action<BridgeServerOptions>? configure = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddBridgeServer(options =>
                    {
                        options.NodeId = "bridge-inbound-test";
                        options.ServerName = "bridge-inbound-test";
                        options.PathPrefix = "/bridge";
                        options.AddAction("orders.lookup", "Lookup an order.");
                        options.DispatchAsync = (frame, _) => Task.FromResult<IFrame>(new CapsFrame
                        {
                            AnchorRef = "nps://orders/result/v1",
                            Count = 1,
                            Data = new[]
                            {
                                JsonSerializer.SerializeToElement(new
                                {
                                    action_id = frame.ActionId,
                                    order_id = frame.Params?.GetProperty("order_id").GetString(),
                                }),
                            },
                        });
                        configure?.Invoke(options);
                    });
                });
                web.Configure(app => app.UseBridgeServer());
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static void AddAgentHeader(HttpRequestMessage request) =>
        request.Headers.Add(NwpHttpHeaders.Agent, "urn:nps:agent:ca.example.com:caller");

    private static void AllowBridgeAgent(BridgeServerOptions options) =>
        options.VerifyAgentAsync = (nid, _, _) =>
            ValueTask.FromResult(nid == "urn:nps:agent:ca.example.com:caller");

    // ── NPS-CR-0010: inbound profile (NWP §16.1.2) ───────────────────────────

    [Fact]
    public async Task McpInbound_ServesResourcesOverAQueryableNode()
    {
        var options = BuildInboundOptions(_ => new CapsFrame
        {
            AnchorRef = "nps://orders/v1",
            Count = 1,
            Data = new[] { JsonSerializer.SerializeToElement(new { order_id = "42" }) },
        });
        // A Complex Node is both queryable and invokable — so it projects onto MCP
        // resources *and* tools. The pre-CR-0010 Bridge could not express this at all.
        options.NodeRole = NwpNodeRole.Complex;
        options.QueryAsync = (_, _) => Task.FromResult<IFrame>(new CapsFrame
        {
            AnchorRef = "nps://orders/v1",
            Count = 1,
            Data = new[] { JsonSerializer.SerializeToElement(new { order_id = "42" }) },
        });

        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var list = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("res-1"),
            Method = "resources/list",
        });

        Assert.Null(list.Error);
        var uri = list.Result!.Value.GetProperty("resources")[0].GetProperty("uri").GetString();
        Assert.Equal("nwp://bridge-inbound-test/", uri);

        var read = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("res-2"),
            Method = "resources/read",
            Params = JsonSerializer.SerializeToElement(new { uri }),
        });

        Assert.Null(read.Error);
        var text = read.Result!.Value.GetProperty("contents")[0].GetProperty("text").GetString()!;
        using var payload = JsonDocument.Parse(text);
        Assert.Equal("nps://orders/v1", payload.RootElement.GetProperty("anchor_ref").GetString());
    }

    [Fact]
    public async Task McpInbound_ServesResourcesMethodsEvenWithNoMemoryNode()
    {
        // §16.1.2 requires the resource *methods* to be served. An action-only Bridge
        // serves them over an empty set — it is conformant, not exempt.
        var options = BuildInboundOptions(_ => new CapsFrame { AnchorRef = "nps://orders/v1", Count = 0, Data = Array.Empty<JsonElement>() });
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var init = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("i"),
            Method = "initialize",
        });
        Assert.True(init.Result!.Value.GetProperty("capabilities").TryGetProperty("resources", out _));

        var list = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("r"),
            Method = "resources/list",
        });

        Assert.Null(list.Error);
        Assert.Empty(list.Result!.Value.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public async Task McpInbound_StillResolvesUnqualifiedToolNames()
    {
        // tools/list now emits the qualified `node__action` form, but a client written
        // against the pre-CR-0010 Bridge sends the bare name. It must keep working.
        ActionFrame? captured = null;
        var options = BuildInboundOptions(frame =>
        {
            captured = frame;
            return new CapsFrame { AnchorRef = "nps://orders/v1", Count = 0, Data = Array.Empty<JsonElement>() };
        });
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var call = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("c"),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new { name = "orders_lookup" }),
        });

        Assert.Null(call.Error);
        Assert.NotNull(captured);
        Assert.Equal("orders.lookup", captured.ActionId);
    }

    [Fact]
    public async Task McpInbound_AuthFailureIsAProtocolErrorNotAnIsErrorResult()
    {
        // The §16.3 rule both pre-CR-0010 implementations broke: an NPS-AUTH-* failure
        // came back as a *successful* JSON-RPC result carrying isError:true, which lets an
        // MCP client mistake a 403 for a tool that merely returned unhappy text.
        var options = BuildInboundOptions(_ => new ErrorFrame
        {
            Status = NPS.Core.NpsStatusCodes.AuthForbidden,
            Error = "NWP-AUTH-NID-SCOPE-VIOLATION",
            Message = "scope does not cover this node",
        });
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var call = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("c"),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new { name = "orders_lookup" }),
        });

        Assert.Null(call.Result);
        Assert.NotNull(call.Error);
        Assert.Equal(BridgeJsonRpcErrorCodes.Forbidden, call.Error!.Code);
    }

    [Fact]
    public async Task McpInbound_MissingDispatcherFailsLoudlyWithARegisteredCode()
    {
        // Previously emitted the non-existent status NPS-SERVER-NOT-IMPLEMENTED.
        var options = new BridgeServerOptions { NodeId = "no-dispatcher", ServerName = "no-dispatcher" };
        options.AddAction("orders.lookup", "Lookup an order.");
        var bridge = new McpInboundServer(options, BridgeServerBackends.Create(options));

        var call = await bridge.DispatchAsync(new BridgeJsonRpcRequest
        {
            Id = JsonSerializer.SerializeToElement("c"),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToElement(new { name = "orders_lookup" }),
        });

        // A missing backend is infrastructure failure (NPS-SERVER-INTERNAL), so per F4 it is a
        // protocol-level error, not an isError tool result — the tool did not run.
        Assert.Null(call.Result);
        Assert.NotNull(call.Error);
        Assert.Equal(BridgeJsonRpcErrorCodes.InternalError, call.Error!.Code);
        var data = call.Error.Data!.Value.GetRawText();
        Assert.Contains(BridgeErrorCodes.ServerDispatcherMissing, data);
        Assert.DoesNotContain("NPS-SERVER-NOT-IMPLEMENTED", data);
    }

    private static BridgeServerOptions BuildInboundOptions(Func<ActionFrame, IFrame> dispatch)
    {
        var options = new BridgeServerOptions
        {
            NodeId = "bridge-inbound-test",
            ServerName = "bridge-inbound-test",
        };
        options.AddAction("orders.lookup", "Lookup an order.");
        options.DispatchAsync = (frame, _) => Task.FromResult(dispatch(frame));
        return options;
    }
}
