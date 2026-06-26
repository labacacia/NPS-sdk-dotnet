// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NPS.Tests.Daemons.Npsd;

public class InboxTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static async Task<string> IssueAgentAsync(NpsdTestServerFixture fx, string identifier)
    {
        var resp = await fx.Client.PostAsJsonAsync("/v1/agents", new
        {
            identifier,
            capabilities = new[] { "nwp:query" },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        return body.GetProperty("frame").GetProperty("nid").GetString()!;
    }

    [Fact]
    public async Task Deposit_then_long_poll_returns_the_message()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer");

        var payload = Encoding.UTF8.GetBytes("hello inbox");
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/test+plain");

        var post = await fx.Client.PostAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}", content);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var postBody = await post.Content.ReadFromJsonAsync<JsonElement>(s_json);
        var msgId    = ulong.Parse(postBody.GetProperty("message_id").GetString()!);
        Assert.True(msgId > 0);

        var get = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(1, body.GetProperty("count").GetInt32());

        var first  = body.GetProperty("messages").EnumerateArray().First();
        Assert.Equal(msgId, ulong.Parse(first.GetProperty("message_id").GetString()!));
        Assert.Equal("application/test+plain", first.GetProperty("content_type").GetString());
        var b64 = first.GetProperty("payload_b64").GetString()!;
        Assert.Equal(payload, Convert.FromBase64String(b64));
    }

    [Fact]
    public async Task Long_poll_with_wait_returns_when_a_message_arrives()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer-wait");

        // Start the long-poll first; it should block.
        var pollTask = fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=10&batch=8");

        // Give the poll a moment to land before we deposit.
        await Task.Delay(50);

        var post = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}",
            new ByteArrayContent(Encoding.UTF8.GetBytes("woke me up")));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var pollResp = await pollTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, pollResp.StatusCode);
        var body = await pollResp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Long_poll_with_no_messages_returns_empty_after_timeout()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer-empty");

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var resp = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=1");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        // The wait was at least ~750 ms (allow some slack against cancellation).
        Assert.InRange(sw.ElapsedMilliseconds, 700, 5_000);
    }

    [Fact]
    public async Task Ack_removes_message_so_subsequent_peek_does_not_return_it()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer-ack");

        var post = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}",
            new ByteArrayContent(new byte[] { 1 }));
        var msgId = ulong.Parse((await post.Content.ReadFromJsonAsync<JsonElement>(s_json))
            .GetProperty("message_id").GetString()!);

        var del = await fx.Client.DeleteAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}/{msgId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(0, body.GetProperty("count").GetInt32());

        // Idempotent — second ack 404s.
        var del2 = await fx.Client.DeleteAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}/{msgId}");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);
    }

    [Fact]
    public async Task Depth_reports_pending_count()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer-depth");

        for (int i = 0; i < 4; i++)
            await fx.Client.PostAsync(
                $"/v1/inbox/{Uri.EscapeDataString(nid)}",
                new ByteArrayContent(new byte[] { (byte)i }));

        var depthResp = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}/depth");
        Assert.Equal(HttpStatusCode.OK, depthResp.StatusCode);
        var body = await depthResp.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(4, body.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Deposit_to_unknown_nid_returns_404()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var resp = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString("urn:nps:agent:nope")}",
            new ByteArrayContent(new byte[] { 1 }));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Priority_orders_drain()
    {
        await using var fx = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fx, "consumer-priority");

        async Task Post(byte tag, int priority)
        {
            var content = new ByteArrayContent(new byte[] { tag });
            content.Headers.Add("X-Nps-Inbox-Priority", priority.ToString());
            var resp = await fx.Client.PostAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}", content);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        await Post(0xAA, 0);
        await Post(0xBB, 5);
        await Post(0xCC, 3);

        var get = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0&batch=10");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>(s_json);
        var tags = body.GetProperty("messages").EnumerateArray()
            .Select(m => Convert.FromBase64String(m.GetProperty("payload_b64").GetString()!)[0])
            .ToList();

        // Highest priority first.
        Assert.Equal((byte)0xBB, tags[0]);
        Assert.Equal((byte)0xCC, tags[1]);
        Assert.Equal((byte)0xAA, tags[2]);
    }

    [Fact]
    public async Task Oversize_payload_returns_413()
    {
        await using var fx = await NpsdTestServerFixture.CreateWithOptionsAsync(
            opts => opts with { MaxInboxMessageBytes = 1024 });

        var nid = await IssueAgentAsync(fx, "consumer-big");

        var resp = await fx.Client.PostAsync(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}",
            new ByteArrayContent(new byte[2048]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }
}
