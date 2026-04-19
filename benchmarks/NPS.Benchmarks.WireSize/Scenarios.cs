// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;
using NPS.NWP.Frames;

namespace NPS.Benchmarks.WireSize;

/// <summary>
/// Representative frame fixtures for the Tier-1 JSON vs Tier-2 MsgPack wire-size
/// comparison. Each scenario produces the same conceptual payload as a real
/// production frame — the only thing that changes between scenarios is the shape
/// (row list, nested request/response, graph capsule).
///
/// <para>Fixtures are deterministic: no timestamps derived from <c>DateTime.Now</c>,
/// no random IDs. Re-runs produce byte-identical output.</para>
/// </summary>
public static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        BuildAnchor(),
        BuildMemoryCapsule(),
        BuildActionInvoke(),
        BuildActionResponse(),
        BuildGraphCapsule(),
    };

    // ── S1: AnchorFrame — product schema bundle ──────────────────────────────

    private static Scenario BuildAnchor()
    {
        var anchor = new AnchorFrame
        {
            AnchorId = "sha256:" + new string('4', 64),
            Ttl      = 3600,
            Schema   = new FrameSchema
            {
                Fields =
                [
                    new("id",          "string",    "entity.id"),
                    new("sku",         "string",    "commerce.sku"),
                    new("name",        "string",    "entity.label"),
                    new("description", "string",    "entity.description", Nullable: true),
                    new("price",       "decimal",   "commerce.price.usd"),
                    new("currency",    "string",    "commerce.currency"),
                    new("stock_level", "uint64",    "commerce.stock"),
                    new("category_id", "string",    "entity.ref"),
                    new("tags",        "string",    "entity.tags", Nullable: true),
                    new("created_at",  "timestamp", "temporal.created"),
                    new("updated_at",  "timestamp", "temporal.updated"),
                ],
            },
        };
        return new Scenario(
            Name:        "S1 — AnchorFrame (11-field product schema)",
            Frame:       anchor,
            Description: "Schema anchor carrying 11 field descriptors with semantic annotations. " +
                         "Transmitted once per session; subsequent frames reference by anchor_id only.");
    }

    // ── S2: CapsFrame — 10-row product list, positional data ─────────────────

    private static Scenario BuildMemoryCapsule()
    {
        // Rows are positional arrays — matches NWP convention of dropping repeated
        // field names once an AnchorFrame has pinned the schema.
        const string rowsJson = """
        [
          ["prod_001","WDG-001","Widget A","Standard widget.",19.99,"USD",142,"cat_1",["featured","new"],"2026-04-01T09:12:00Z","2026-04-18T14:30:00Z"],
          ["prod_002","WDG-002","Widget B","Deluxe widget.",29.99,"USD",87,"cat_1",["featured"],"2026-04-02T10:00:00Z","2026-04-17T12:00:00Z"],
          ["prod_003","GIZ-001","Gizmo Pro","Professional gizmo.",49.99,"USD",23,"cat_2",["pro"],"2026-04-03T08:30:00Z","2026-04-16T11:00:00Z"],
          ["prod_004","GIZ-002","Gizmo Lite","Entry-level gizmo.",24.99,"USD",56,"cat_2",["budget"],"2026-04-04T14:00:00Z","2026-04-15T09:30:00Z"],
          ["prod_005","SPR-001","Sprocket","High-quality sprocket.",12.50,"USD",201,"cat_3",[],"2026-04-05T11:15:00Z","2026-04-14T16:45:00Z"],
          ["prod_006","SPR-002","Sprocket XL","Extra-large sprocket.",18.75,"USD",98,"cat_3",["heavy"],"2026-04-06T09:00:00Z","2026-04-13T15:00:00Z"],
          ["prod_007","COG-001","Cog","Precision cog.",8.25,"USD",312,"cat_3",["bulk"],"2026-04-07T13:45:00Z","2026-04-12T14:20:00Z"],
          ["prod_008","COG-002","Cog XL","Extra-strength cog.",14.50,"USD",74,"cat_3",[],"2026-04-08T10:30:00Z","2026-04-11T13:15:00Z"],
          ["prod_009","BRK-001","Bracket","Mounting bracket.",6.99,"USD",450,"cat_4",["bulk"],"2026-04-09T12:00:00Z","2026-04-10T12:30:00Z"],
          ["prod_010","BRK-002","Bracket HD","Heavy-duty bracket.",12.99,"USD",188,"cat_4",["heavy","featured"],"2026-04-10T15:30:00Z","2026-04-10T15:30:00Z"]
        ]
        """;

        var rows = JsonDocument.Parse(rowsJson).RootElement
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();

        var caps = new CapsFrame
        {
            AnchorRef     = "sha256:" + new string('4', 64),
            Count         = 10,
            Data          = rows,
            TokenEst      = 180,
            TokenizerUsed = "nps-fallback",
        };

        return new Scenario(
            Name:        "S2 — CapsFrame (10 product rows, positional)",
            Frame:       caps,
            Description: "Memory Node capsule: 10 product rows encoded as positional arrays, " +
                         "referencing the anchor from S1. Representative of the steady-state " +
                         "response shape once the schema has been pinned.");
    }

    // ── S3: ActionFrame — create-order invocation ────────────────────────────

    private static Scenario BuildActionInvoke()
    {
        const string paramsJson = """
        {
          "customer_id":"cust_42",
          "items":[
            ["WDG-001",2,19.99],
            ["GIZ-001",1,49.99],
            ["BRK-002",3,12.99]
          ],
          "ship_to":["100 Market St","Suite 4","San Francisco","CA","94105","US"],
          "payment":["card","visa","4242"],
          "metadata":{"source":"agent","channel":"api"}
        }
        """;

        var action = new ActionFrame
        {
            ActionId       = "orders.create",
            Params         = JsonDocument.Parse(paramsJson).RootElement,
            IdempotencyKey = "7b8c6a6a-7f1b-4d2e-9c3a-0e5a4f2a8b11",
            TimeoutMs      = 30_000,
            RequestId      = "req_00000",
            Priority       = "normal",
        };

        return new Scenario(
            Name:        "S3 — ActionFrame (orders.create with 3 line items)",
            Frame:       action,
            Description: "Action Node request for a compact order invocation. Positional item " +
                         "tuples + address/payment arrays against a bundled params schema.");
    }

    // ── S4: CapsFrame — ActionFrame response ─────────────────────────────────

    private static Scenario BuildActionResponse()
    {
        const string rowsJson = """
        [
          ["ord_7777","confirmed",
            [["WDG-001","Widget A",2,19.99,39.98],
             ["GIZ-001","Gizmo Pro",1,49.99,49.99],
             ["BRK-002","Bracket HD",3,12.99,38.97]],
            [128.94,11.60,5.00,145.54],
            ["100 Market St","Suite 4","San Francisco","CA","94105","US"],
            "2026-04-19T12:00:00Z"]
        ]
        """;

        var rows = JsonDocument.Parse(rowsJson).RootElement
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();

        var caps = new CapsFrame
        {
            AnchorRef = "sha256:orders.create.v1" + new string('0', 46),
            Count     = 1,
            Data      = rows,
            TokenEst  = 72,
        };

        return new Scenario(
            Name:        "S4 — CapsFrame (orders.create response)",
            Frame:       caps,
            Description: "Action Node response capsule: confirmed order with line totals, " +
                         "cost breakdown, and shipping address as positional arrays.");
    }

    // ── S5: CapsFrame — Complex Node graph traversal ─────────────────────────

    private static Scenario BuildGraphCapsule()
    {
        const string rowsJson = """
        [{
          "order":["ord_7777","confirmed","cust_42",[["WDG-001",2,19.99],["GIZ-001",1,49.99]],[89.97,8.10,5.00,103.07],"2026-04-19T12:00:00Z"],
          "customer":["cust_42","alice@example.com","Alice Example","gold"],
          "products":{
            "WDG-001":["WDG-001","Widget A",19.99,142],
            "GIZ-001":["GIZ-001","Gizmo Pro",49.99,23]
          }
        }]
        """;

        var rows = JsonDocument.Parse(rowsJson).RootElement
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();

        var caps = new CapsFrame
        {
            AnchorRef = "sha256:graph.order.v1" + new string('0', 48),
            Count     = 1,
            Data      = rows,
            TokenEst  = 140,
        };

        return new Scenario(
            Name:        "S5 — CapsFrame (order→customer+products graph)",
            Frame:       caps,
            Description: "Complex Node capsule returning a joined order/customer/product graph " +
                         "as nested positional arrays + a keyed product dictionary.");
    }
}

/// <summary>
/// A wire-size scenario: a single concrete frame that will be encoded with both
/// Tier-1 JSON and Tier-2 MsgPack, and the payload byte counts compared.
/// </summary>
public sealed record Scenario(string Name, IFrame Frame, string Description);
