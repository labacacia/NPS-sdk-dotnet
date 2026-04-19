// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace NPS.Benchmarks.TokenSavings;

/// <summary>
/// Three representative Agent-Node interactions. Each scenario produces the full
/// byte-for-byte traffic for both protocols across an N-call session, using
/// hand-written fixtures that mirror real production payloads.
///
/// <para>The fixtures are intentionally small enough to eyeball but carry the
/// structural signals that matter: field names, schema keys, type annotations,
/// pagination envelopes, and graph traversal joins.</para>
/// </summary>
public static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        BuildMemoryRead(),
        BuildActionInvoke(),
        BuildGraphTraversal(),
    };

    // ── S1: Memory Node — list top 10 products, 10 repeated calls ────────────

    private static Scenario BuildMemoryRead()
    {
        const int calls = 10;

        // REST: each call re-sends full objects with all field names.
        string restRequest  = "GET /api/products?limit=10&sort=-created_at HTTP/1.1\r\n" +
                              "Host: api.example.com\r\n" +
                              "Accept: application/json\r\n" +
                              "Authorization: Bearer eyJhbGci...\r\n" +
                              "User-Agent: AcmeAgent/1.0\r\n\r\n";

        string restResponse = """
        HTTP/1.1 200 OK
        Content-Type: application/json; charset=utf-8
        X-Total-Count: 10

        {
          "data": [
            {"id":"prod_001","sku":"WDG-001","name":"Widget A","description":"Standard widget.","price":19.99,"currency":"USD","stockLevel":142,"categoryId":"cat_1","tags":["featured","new"],"createdAt":"2026-04-01T09:12:00Z","updatedAt":"2026-04-18T14:30:00Z"},
            {"id":"prod_002","sku":"WDG-002","name":"Widget B","description":"Deluxe widget.","price":29.99,"currency":"USD","stockLevel":87,"categoryId":"cat_1","tags":["featured"],"createdAt":"2026-04-02T10:00:00Z","updatedAt":"2026-04-17T12:00:00Z"},
            {"id":"prod_003","sku":"GIZ-001","name":"Gizmo Pro","description":"Professional gizmo.","price":49.99,"currency":"USD","stockLevel":23,"categoryId":"cat_2","tags":["pro"],"createdAt":"2026-04-03T08:30:00Z","updatedAt":"2026-04-16T11:00:00Z"},
            {"id":"prod_004","sku":"GIZ-002","name":"Gizmo Lite","description":"Entry-level gizmo.","price":24.99,"currency":"USD","stockLevel":56,"categoryId":"cat_2","tags":["budget"],"createdAt":"2026-04-04T14:00:00Z","updatedAt":"2026-04-15T09:30:00Z"},
            {"id":"prod_005","sku":"SPR-001","name":"Sprocket","description":"High-quality sprocket.","price":12.50,"currency":"USD","stockLevel":201,"categoryId":"cat_3","tags":[],"createdAt":"2026-04-05T11:15:00Z","updatedAt":"2026-04-14T16:45:00Z"},
            {"id":"prod_006","sku":"SPR-002","name":"Sprocket XL","description":"Extra-large sprocket.","price":18.75,"currency":"USD","stockLevel":98,"categoryId":"cat_3","tags":["heavy"],"createdAt":"2026-04-06T09:00:00Z","updatedAt":"2026-04-13T15:00:00Z"},
            {"id":"prod_007","sku":"COG-001","name":"Cog","description":"Precision cog.","price":8.25,"currency":"USD","stockLevel":312,"categoryId":"cat_3","tags":["bulk"],"createdAt":"2026-04-07T13:45:00Z","updatedAt":"2026-04-12T14:20:00Z"},
            {"id":"prod_008","sku":"COG-002","name":"Cog XL","description":"Extra-strength cog.","price":14.50,"currency":"USD","stockLevel":74,"categoryId":"cat_3","tags":[],"createdAt":"2026-04-08T10:30:00Z","updatedAt":"2026-04-11T13:15:00Z"},
            {"id":"prod_009","sku":"BRK-001","name":"Bracket","description":"Mounting bracket.","price":6.99,"currency":"USD","stockLevel":450,"categoryId":"cat_4","tags":["bulk"],"createdAt":"2026-04-09T12:00:00Z","updatedAt":"2026-04-10T12:30:00Z"},
            {"id":"prod_010","sku":"BRK-002","name":"Bracket HD","description":"Heavy-duty bracket.","price":12.99,"currency":"USD","stockLevel":188,"categoryId":"cat_4","tags":["heavy","featured"],"createdAt":"2026-04-10T15:30:00Z","updatedAt":"2026-04-10T15:30:00Z"}
          ],
          "pagination": {"limit":10,"offset":0,"total":10,"hasMore":false}
        }
        """;

        // NWP: AnchorFrame fetched once per session. QueryFrame is compact; CapsFrame
        // carries rows by position (no repeated field names) and an anchor_ref.
        string anchorFrame = """
        {"nps":"0.4","type":"anchor","anchor_ref":"sha256:4f6a3c81e2","ttl":3600,
         "schema":{"kind":"row","fields":["id","sku","name","description","price","currency","stockLevel","categoryId","tags","createdAt","updatedAt"],
         "types":["str","str","str","str","f64","str","u32","str","arr<str>","iso8601","iso8601"]}}
        """;

        string nwpRequest = """
        {"nps":"0.4","type":"query","anchor_ref":"sha256:4f6a3c81e2","limit":10,"sort":"-createdAt"}
        """;

        // CapsFrame rows: positional arrays, no repeated schema.
        string nwpResponse = """
        {"nps":"0.4","type":"caps","anchor_ref":"sha256:4f6a3c81e2","count":10,"token_est":180,
         "data":[
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
         ]}
        """;

        return new Scenario(
            "S1 — Memory Node: list top 10 products",
            calls,
            Rest:new[] { (restRequest, restResponse) }
                .SelectMany(p => Repeat(p, calls))
                .ToArray(),
            NwpOneShot:new[] { anchorFrame }, // paid once per session
            Nwp:new[] { (nwpRequest, nwpResponse) }
                .SelectMany(p => Repeat(p, calls))
                .ToArray(),
            Description:"Ten sequential reads of the same 10-row product list. REST " +
                         "re-sends JSON keys and schema-carrying envelopes every time; NWP " +
                         "amortises a single AnchorFrame across the session and returns rows " +
                         "as positional arrays referenced by anchor_ref."
        );
    }

    // ── S2: Action Node — create order with 3 line items, 10 repeated calls ──

    private static Scenario BuildActionInvoke()
    {
        const int calls = 10;

        string restRequest = """
        POST /api/orders HTTP/1.1
        Host: api.example.com
        Content-Type: application/json
        Authorization: Bearer eyJhbGci...
        Idempotency-Key: ord_req_00000

        {
          "customerId":"cust_42",
          "items":[
            {"sku":"WDG-001","quantity":2,"unitPrice":19.99,"currency":"USD"},
            {"sku":"GIZ-001","quantity":1,"unitPrice":49.99,"currency":"USD"},
            {"sku":"BRK-002","quantity":3,"unitPrice":12.99,"currency":"USD"}
          ],
          "shippingAddress":{
            "line1":"100 Market St","line2":"Suite 4",
            "city":"San Francisco","region":"CA","postalCode":"94105","country":"US"
          },
          "paymentMethod":{"type":"card","brand":"visa","last4":"4242"},
          "metadata":{"source":"agent","channel":"api"}
        }
        """;

        string restResponse = """
        HTTP/1.1 201 Created
        Content-Type: application/json
        Location: /api/orders/ord_7777

        {
          "id":"ord_7777",
          "status":"confirmed",
          "customerId":"cust_42",
          "items":[
            {"sku":"WDG-001","name":"Widget A","quantity":2,"unitPrice":19.99,"currency":"USD","lineTotal":39.98},
            {"sku":"GIZ-001","name":"Gizmo Pro","quantity":1,"unitPrice":49.99,"currency":"USD","lineTotal":49.99},
            {"sku":"BRK-002","name":"Bracket HD","quantity":3,"unitPrice":12.99,"currency":"USD","lineTotal":38.97}
          ],
          "totals":{"subtotal":128.94,"tax":11.60,"shipping":5.00,"grandTotal":145.54,"currency":"USD"},
          "shippingAddress":{"line1":"100 Market St","line2":"Suite 4","city":"San Francisco","region":"CA","postalCode":"94105","country":"US"},
          "createdAt":"2026-04-19T12:00:00Z",
          "updatedAt":"2026-04-19T12:00:00Z"
        }
        """;

        string anchorFrame = """
        {"nps":"0.4","type":"anchor","anchor_ref":"sha256:orders.create.v1","ttl":3600,
         "schema":{
          "params":{"fields":["customer_id","items","ship_to","payment","metadata"],
                    "items.fields":["sku","qty","unit_price"],
                    "ship_to.fields":["line1","line2","city","region","postal","country"],
                    "payment.fields":["type","brand","last4"]},
          "result":{"fields":["id","status","items","totals","ship_to","created_at"],
                    "items.fields":["sku","name","qty","unit_price","line_total"],
                    "totals.fields":["subtotal","tax","shipping","total"]}}}
        """;

        string nwpRequest = """
        {"nps":"0.4","type":"action","anchor_ref":"sha256:orders.create.v1",
         "action_id":"orders.create","request_id":"r00000",
         "params":{"customer_id":"cust_42",
          "items":[["WDG-001",2,19.99],["GIZ-001",1,49.99],["BRK-002",3,12.99]],
          "ship_to":["100 Market St","Suite 4","San Francisco","CA","94105","US"],
          "payment":["card","visa","4242"],
          "metadata":{"source":"agent","channel":"api"}}}
        """;

        string nwpResponse = """
        {"nps":"0.4","type":"caps","anchor_ref":"sha256:orders.create.v1","count":1,"token_est":72,
         "data":[["ord_7777","confirmed",
          [["WDG-001","Widget A",2,19.99,39.98],
           ["GIZ-001","Gizmo Pro",1,49.99,49.99],
           ["BRK-002","Bracket HD",3,12.99,38.97]],
          [128.94,11.60,5.00,145.54],
          ["100 Market St","Suite 4","San Francisco","CA","94105","US"],
          "2026-04-19T12:00:00Z"]]}
        """;

        return new Scenario(
            "S2 — Action Node: create order (3 items)",
            calls,
            Rest:Repeat((restRequest, restResponse), calls).ToArray(),
            NwpOneShot:new[] { anchorFrame },
            Nwp: Repeat((nwpRequest,  nwpResponse),  calls).ToArray(),
            Description:"Ten order-creation invocations. REST uses verbose field names " +
                         "on both request and response; NWP ActionFrame sends positional " +
                         "arrays against an amortised params+result schema AnchorFrame."
        );
    }

    // ── S3: Complex Node — order → customer → products graph, 5 traversals ───

    private static Scenario BuildGraphTraversal()
    {
        const int calls = 5;

        // REST: three separate calls per trace — order, customer, then product
        // details for two SKUs. Each response is a full entity document.
        string restOrder = """
        GET /api/orders/ord_7777 HTTP/1.1
        Host: api.example.com
        Authorization: Bearer eyJhbGci...

        """;
        string restOrderResp = """
        HTTP/1.1 200 OK
        Content-Type: application/json

        {
          "id":"ord_7777","status":"confirmed","customerId":"cust_42",
          "items":[
            {"sku":"WDG-001","quantity":2,"unitPrice":19.99,"currency":"USD"},
            {"sku":"GIZ-001","quantity":1,"unitPrice":49.99,"currency":"USD"}
          ],
          "totals":{"subtotal":89.97,"tax":8.10,"shipping":5.00,"grandTotal":103.07,"currency":"USD"},
          "createdAt":"2026-04-19T12:00:00Z"
        }
        """;

        string restCustomer = """
        GET /api/customers/cust_42 HTTP/1.1
        Host: api.example.com
        Authorization: Bearer eyJhbGci...

        """;
        string restCustomerResp = """
        HTTP/1.1 200 OK
        Content-Type: application/json

        {
          "id":"cust_42",
          "email":"alice@example.com",
          "name":"Alice Example",
          "tier":"gold",
          "address":{"line1":"100 Market St","city":"San Francisco","region":"CA","postalCode":"94105","country":"US"},
          "createdAt":"2024-01-10T09:00:00Z"
        }
        """;

        string restProduct1 = """
        GET /api/products/WDG-001 HTTP/1.1
        Host: api.example.com
        Authorization: Bearer eyJhbGci...

        """;
        string restProduct1Resp = """
        HTTP/1.1 200 OK
        Content-Type: application/json

        {"id":"prod_001","sku":"WDG-001","name":"Widget A","description":"Standard widget.","price":19.99,"currency":"USD","stockLevel":142,"categoryId":"cat_1","tags":["featured","new"]}
        """;

        string restProduct2 = """
        GET /api/products/GIZ-001 HTTP/1.1
        Host: api.example.com
        Authorization: Bearer eyJhbGci...

        """;
        string restProduct2Resp = """
        HTTP/1.1 200 OK
        Content-Type: application/json

        {"id":"prod_003","sku":"GIZ-001","name":"Gizmo Pro","description":"Professional gizmo.","price":49.99,"currency":"USD","stockLevel":23,"categoryId":"cat_2","tags":["pro"]}
        """;

        // Each REST call-pair (request + response) is one "call" in the session,
        // so a single traversal costs four entity fetches.
        var perTraversalRest = new[]
        {
            (restOrder,    restOrderResp),
            (restCustomer, restCustomerResp),
            (restProduct1, restProduct1Resp),
            (restProduct2, restProduct2Resp),
        };

        // NWP: one Complex Node query walks the graph in one hop. A single
        // bundled AnchorFrame carries order/customer/product schemas; response
        // is one capsule with nested refs.
        string nwpAnchor = """
        {"nps":"0.4","type":"anchor","anchor_ref":"sha256:graph.order.v1","ttl":3600,
         "schema":{
          "order":{"fields":["id","status","customer","items","totals","created_at"],
                   "items.fields":["sku","qty","unit_price"],
                   "totals.fields":["subtotal","tax","shipping","total"]},
          "customer":{"fields":["id","email","name","tier"]},
          "product":{"fields":["sku","name","price","stock"]}}}
        """;

        string nwpQuery = """
        {"nps":"0.4","type":"query","anchor_ref":"sha256:graph.order.v1",
         "root":{"type":"order","id":"ord_7777"},
         "traverse":[{"rel":"customer","depth":1},{"rel":"items.product","depth":1}]}
        """;

        string nwpResponse = """
        {"nps":"0.4","type":"caps","anchor_ref":"sha256:graph.order.v1","count":1,"token_est":140,
         "data":[{
          "order":["ord_7777","confirmed","cust_42",[["WDG-001",2,19.99],["GIZ-001",1,49.99]],[89.97,8.10,5.00,103.07],"2026-04-19T12:00:00Z"],
          "customer":["cust_42","alice@example.com","Alice Example","gold"],
          "products":{
           "WDG-001":["WDG-001","Widget A",19.99,142],
           "GIZ-001":["GIZ-001","Gizmo Pro",49.99,23]
          }
         }]}
        """;

        return new Scenario(
            "S3 — Complex Node: order → customer → products graph",
            calls,
            Rest:Enumerable.Range(0, calls).SelectMany(_ => perTraversalRest).ToArray(),
            NwpOneShot:new[] { nwpAnchor },
            Nwp: Repeat((nwpQuery, nwpResponse), calls).ToArray(),
            Description:"Five traversals of an order → customer + items.products graph. " +
                         "REST requires four separate fetches per traversal; NWP Complex " +
                         "Node returns the joined graph as a single capsule referencing a " +
                         "shared schema bundle."
        );
    }

    private static IEnumerable<T> Repeat<T>(T value, int n)
    {
        for (var i = 0; i < n; i++) yield return value;
    }
}

/// <summary>
/// A scenario contrasts a REST session (<paramref name="Rest"/>) with an NWP
/// session (<paramref name="NwpOneShot"/> + <paramref name="Nwp"/>) at the same
/// payload-volume ruler.
/// </summary>
public sealed record Scenario(
    string Name,
    int Calls,
    (string Request, string Response)[] Rest,
    string[] NwpOneShot,
    (string Request, string Response)[] Nwp,
    string Description
);
