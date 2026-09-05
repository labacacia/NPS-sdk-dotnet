// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Registry;
using NPS.Daemon.Npsd.Inbox;
using NPS.NWP.Frames;
using NPS.NWP.Registry;

namespace NPS.Tests.Daemons.Npsd;

public class InboxTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static NpsFrameCodec CreateNwpCodec() => new(
        new Tier1JsonCodec(),
        new Tier2MsgPackCodec(),
        new FrameRegistryBuilder().AddNcp().AddNwp().Build());

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
        var msgId = ulong.Parse(postBody.GetProperty("message_id").GetString()!);
        Assert.True(msgId > 0);

        var get = await fx.Client.GetAsync($"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>(s_json);
        Assert.Equal(1, body.GetProperty("count").GetInt32());

        var first = body.GetProperty("messages").EnumerateArray().First();
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
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

    [Fact]
    public async Task Undelivered_ActionFrame_survives_host_restart_bit_identical()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"npsd-inbox-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        try
        {
            var wire = CreateNwpCodec().Encode(
                new ActionFrame
                {
                    ActionId = "orders.persist",
                    IdempotencyKey = "restart-proof",
                },
                EncodingTier.Json);
            string nid;
            ulong messageId;

            await using (var first = await NpsdTestServerFixture.CreatePersistentAsync(dataDir))
            {
                nid = await IssueAgentAsync(first, "durable-consumer");
                var content = new ByteArrayContent(wire);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/nps-frame");
                var deposited = await first.Client.PostAsync(
                    $"/v1/inbox/{Uri.EscapeDataString(nid)}",
                    content);
                Assert.Equal(HttpStatusCode.Created, deposited.StatusCode);
                messageId = ulong.Parse((await deposited.Content.ReadFromJsonAsync<JsonElement>(s_json))
                    .GetProperty("message_id").GetString()!);
            }

            Assert.True(File.Exists(Path.Combine(dataDir, "inbox.sqlite")));

            await using (var second = await NpsdTestServerFixture.CreatePersistentAsync(dataDir))
            {
                var pulled = await second.Client.GetFromJsonAsync<JsonElement>(
                    $"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0&batch=1",
                    s_json);
                var message = Assert.Single(pulled.GetProperty("messages").EnumerateArray());
                Assert.Equal(messageId, ulong.Parse(message.GetProperty("message_id").GetString()!));
                Assert.Equal(wire, Convert.FromBase64String(message.GetProperty("payload_b64").GetString()!));

                var ack = await second.Client.DeleteAsync(
                    $"/v1/inbox/{Uri.EscapeDataString(nid)}/{messageId}");
                Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
            }

            await using (var third = await NpsdTestServerFixture.CreatePersistentAsync(dataDir))
            {
                var afterAck = await third.Client.GetFromJsonAsync<JsonElement>(
                    $"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0",
                    s_json);
                Assert.Equal(0, afterAck.GetProperty("count").GetInt32());
            }
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* leave for diagnostics */ }
        }
    }

    [Fact]
    public async Task Durable_store_preserves_priority_and_expires_by_absolute_deadline()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"npsd-inbox-store-{Guid.NewGuid():N}");
        var sqlitePath = Path.Combine(dataDir, "inbox.sqlite");
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        try
        {
            ulong low;
            ulong high;
            using (var first = InboxStore.CreateFileForTests(sqlitePath, () => now))
            {
                low = first.Enqueue("urn:nps:test", [0x01], "application/octet-stream", 0,
                    TimeSpan.FromMinutes(10), 10);
                high = first.Enqueue("urn:nps:test", [0x02], "application/octet-stream", 5,
                    TimeSpan.FromSeconds(30), 10);
            }

            using (var second = InboxStore.CreateFileForTests(sqlitePath, () => now))
            {
                var ordered = await second.PeekAsync(
                    "urn:nps:test", 10, TimeSpan.Zero, CancellationToken.None);
                Assert.Equal(new[] { high, low }, ordered.Select(message => message.MessageId).ToArray());
            }

            now = now.AddMinutes(1);
            using (var third = InboxStore.CreateFileForTests(sqlitePath, () => now))
            {
                var remaining = await third.PeekAsync(
                    "urn:nps:test", 10, TimeSpan.Zero, CancellationToken.None);
                Assert.Equal(low, Assert.Single(remaining).MessageId);
                Assert.True(third.Ack("urn:nps:test", low));
            }

            using var fourth = InboxStore.CreateFileForTests(sqlitePath, () => now);
            Assert.Equal(0, fourth.Depth("urn:nps:test"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* leave for diagnostics */ }
        }
    }

    [Fact]
    public async Task Pull_ack_sequence_drains_fifo_and_empty_pull_is_immediate()
    {
        await using var fixture = await NpsdTestServerFixture.CreateAsync();
        var nid = await IssueAgentAsync(fixture, "fifo-consumer");

        for (byte value = 1; value <= 3; value++)
        {
            var response = await fixture.Client.PostAsync(
                $"/v1/inbox/{Uri.EscapeDataString(nid)}",
                new ByteArrayContent([value]));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        for (byte expected = 1; expected <= 3; expected++)
        {
            var pulled = await fixture.Client.GetFromJsonAsync<JsonElement>(
                $"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0&batch=1",
                s_json);
            var message = Assert.Single(pulled.GetProperty("messages").EnumerateArray());
            Assert.Equal(expected, Convert.FromBase64String(
                message.GetProperty("payload_b64").GetString()!)[0]);
            var messageId = message.GetProperty("message_id").GetString();
            var ack = await fixture.Client.DeleteAsync(
                $"/v1/inbox/{Uri.EscapeDataString(nid)}/{messageId}");
            Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var empty = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/inbox/{Uri.EscapeDataString(nid)}?wait=0&batch=1",
            s_json);
        stopwatch.Stop();
        Assert.Equal(0, empty.GetProperty("count").GetInt32());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Health_advertises_durable_ephemeral_pull_and_declines_resident_push()
    {
        await using var fixture = await NpsdTestServerFixture.CreateAsync();
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>("/health", s_json);
        var delivery = health.GetProperty("inbox_delivery");

        Assert.Equal("sqlite", delivery.GetProperty("storage").GetString());
        Assert.True(delivery.GetProperty("durable_undelivered").GetBoolean());
        Assert.Equal("ephemeral", Assert.Single(
            delivery.GetProperty("supported_activation_modes").EnumerateArray()).GetString());
        Assert.Equal("http-pull-with-explicit-ack", delivery.GetProperty("delivery").GetString());
        Assert.False(delivery.GetProperty("resident_push").GetBoolean());
    }
}
