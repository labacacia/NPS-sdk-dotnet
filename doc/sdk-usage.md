English | [中文版](./sdk-usage.cn.md)

# NPS .NET SDK — Usage Guide

Copyright 2026 INNO LOTUS PTY LTD — Licensed under Apache 2.0

---

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
  - [NCP — Frame Codec](#ncp--frame-codec)
  - [NWP — Memory Node](#nwp--memory-node)
  - [NIP — Identity](#nip--identity)
  - [NDP — Discovery](#ndp--discovery)
  - [NOP — Orchestration](#nop--orchestration)
- [API Reference Summary](#api-reference-summary)
- [Configuration](#configuration)
- [Testing](#testing)

---

## Installation

The SDK ships as five NuGet packages. Add only the packages you need; each pulls in `NPS.Core` automatically.

```bash
# Core framing + codec (required by all packages)
dotnet add package NPS.Core --version 1.0.0-alpha.1

# Neural Web Protocol — query/action frames, Memory Node middleware
dotnet add package NPS.NWP --version 1.0.0-alpha.1

# Neural Identity Protocol — identity frames, key management, CA client
dotnet add package NPS.NIP --version 1.0.0-alpha.1

# Neural Discovery Protocol — announce/resolve/graph frames, in-memory registry
dotnet add package NPS.NDP --version 1.0.0-alpha.1

# Neural Orchestration Protocol — task/delegate/sync frames, DAG orchestrator
dotnet add package NPS.NOP --version 1.0.0-alpha.1
```

Add the NuGet source if packages are not yet on nuget.org:

```xml
<!-- nuget.config -->
<configuration>
  <packageSources>
    <add key="labacacia" value="https://nuget.pkg.github.com/LabAcacia/index.json" />
  </packageSources>
</configuration>
```

---

## Quick Start

### NCP — Frame Codec

NCP is the wire-format foundation of NPS. All higher-level frames are encoded and decoded through the `NpsFrameCodec`.

**DI setup (ASP.NET Core):**

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.DefaultTier     = EncodingTier.MsgPack; // Tier-2 production default
    opts.AnchorTtlSeconds = 3600;                // AnchorFrame cache TTL
    opts.AllowPlaintext  = false;                // Require TLS in production
});
```

**Encoding a frame:**

```csharp
using NPS.Core.Codecs;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

// Resolve from DI or construct directly
var codec = serviceProvider.GetRequiredService<NpsFrameCodec>();

// Build an AnchorFrame (publishes a schema, NCP §5)
var anchor = new AnchorFrame
{
    AnchorId = "sha256:abc123...",
    Schema   = JsonDocument.Parse(@"{""type"":""object"",""properties"":{""id"":{""type"":""string""}}}").RootElement,
    Ttl      = 3600,
};

byte[] wire = codec.Encode(anchor);        // Tier-2 MsgPack by default
```

**Decoding a frame:**

```csharp
IFrame frame = codec.Decode(wire);

if (frame is AnchorFrame received)
{
    Console.WriteLine($"AnchorId: {received.AnchorId}");
}
```

**Switching to Tier-1 JSON (development / debugging):**

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.DefaultTier = EncodingTier.Json;
});
```

**AnchorFrame cache:**

```csharp
using NPS.Core.Caching;

var cache = serviceProvider.GetRequiredService<AnchorFrameCache>();
cache.Store(anchor);

if (cache.TryGet("sha256:abc123...", out var cached))
{
    Console.WriteLine($"Cache hit: TTL remaining = {cached.Ttl}s");
}
```

---

### NWP — Memory Node

NWP implements structured data query and action invocation over HTTP or native mode. The `MemoryNodeMiddleware` turns any ASP.NET Core app into a compliant NWP Memory Node.

**DI setup:**

```csharp
using NPS.NWP.Extensions;
using NPS.NWP.MemoryNode;

builder.Services.AddNpsCore();
builder.Services.AddNwp(opts =>
{
    opts.Port               = 17433;  // unified NPS port
    opts.DefaultTokenBudget = 0;      // 0 = unlimited
    opts.MaxDepth           = 5;      // X-NWP-Depth header limit
    opts.DefaultLimit       = 20;     // default query page size
});

// Register your data provider
builder.Services.AddSingleton<IMemoryNodeProvider, MyProductProvider>();
builder.Services.AddMemoryNode<MyProductProvider>(opts =>
{
    opts.PathPrefix = "/products";    // exposes at /products/.nwm, /products/query, etc.
    opts.NodeNid    = "urn:nps:node:example.com:products";
});
```

**Implement IMemoryNodeProvider:**

```csharp
using NPS.NWP.MemoryNode;
using NPS.NWP.Frames;

public sealed class MyProductProvider : IMemoryNodeProvider
{
    public MemoryNodeSchema GetSchema() => new()
    {
        AnchorId = "sha256:products-v1",
        Fields   =
        [
            new MemoryNodeField { Name = "id",    Type = "string",  Required = true },
            new MemoryNodeField { Name = "name",  Type = "string",  Required = true },
            new MemoryNodeField { Name = "price", Type = "number",  Required = false },
        ],
    };

    public async Task<QueryResult> QueryAsync(QueryFrame query, CancellationToken ct)
    {
        // Apply query.Filter, query.Fields, query.Limit, query.Cursor, query.Order
        var products = await FetchFromDatabase(query, ct);
        return new QueryResult
        {
            Records    = products,
            NextCursor = products.Count == query.Limit ? "cursor:next" : null,
        };
    }
}
```

**Map the middleware in the request pipeline:**

```csharp
app.UseNpsMemoryNode(); // maps all registered Memory Nodes
```

**Sending a QueryFrame from a client:**

```csharp
using NPS.NWP.Frames;
using System.Text.Json;

var query = new QueryFrame
{
    AnchorRef = "sha256:products-v1",
    Filter    = JsonDocument.Parse(@"{""price"":{""$lt"":100}}").RootElement,
    Fields    = ["id", "name", "price"],
    Limit     = 10,
    Order     = [new QueryOrderClause("price", "ASC")],
};

byte[] wire = codec.Encode(query);
// Send wire bytes over HTTP mode or native TCP to port 17433
```

**Sending an ActionFrame:**

```csharp
using NPS.NWP.Frames;

var action = new ActionFrame
{
    Action  = "restock",
    Payload = JsonDocument.Parse(@"{""product_id"":""p1"",""quantity"":50}").RootElement,
};

byte[] wire = codec.Encode(action);
```

---

### NIP — Identity

NIP provides agent identity, certificate issuance, and verification using Ed25519.

**Generate an Ed25519 keypair and manage keys:**

```csharp
using NPS.NIP.Crypto;

var keyManager = new NipKeyManager();

// Generate and persist (AES-256-GCM encrypted, PBKDF2-SHA256 derived key)
keyManager.Generate("/data/agent.key.enc", passphrase: Environment.GetEnvironmentVariable("KEY_PASS")!);

// Load in subsequent runs
keyManager.Load("/data/agent.key.enc", passphrase: Environment.GetEnvironmentVariable("KEY_PASS")!);

// Export public key in NPS wire format: "ed25519:<base64url-DER>"
string pubKey = keyManager.ExportPublicKeyString();
```

**Sign and verify with NipSigner:**

```csharp
using NPS.NIP.Crypto;

var signer = new NipSigner(keyManager);

byte[] payload   = System.Text.Encoding.UTF8.GetBytes("canonical-json-payload");
string signature = signer.Sign(payload);   // "ed25519:<base64url>"

bool valid = signer.Verify(payload, signature, pubKey);
```

**Build an IdentFrame (agent identity declaration):**

```csharp
using NPS.NIP.Frames;

var ident = new IdentFrame
{
    Nid          = "urn:nps:agent:ca.example.com:550e8400-e29b-41d4-a716-446655440000",
    PubKey       = pubKey,
    Capabilities = ["nwp:query", "nwp:stream"],
    Scope        = JsonDocument.Parse(@"{""nodes"":[""*""],""actions"":[]}").RootElement,
    IssuedBy     = "urn:nps:org:ca.example.com",
    IssuedAt     = DateTime.UtcNow.ToString("O"),
    ExpiresAt    = DateTime.UtcNow.AddDays(30).ToString("O"),
    Serial       = "0x0001",
    Signature    = signature,
};
```

**TrustFrame (cross-CA delegation):**

```csharp
using NPS.NIP.Frames;

var trust = new TrustFrame
{
    DelegatorNid = "urn:nps:org:ca-a.example.com",
    DelegateeNid = "urn:nps:org:ca-b.example.com",
    Capabilities = ["nwp:query"],
    ExpiresAt    = DateTime.UtcNow.AddDays(7).ToString("O"),
    Signature    = delegatorSignature,
};
```

**RevokeFrame:**

```csharp
using NPS.NIP.Frames;

var revoke = new RevokeFrame
{
    Nid       = "urn:nps:agent:ca.example.com:550e8400-...",
    Reason    = "cessation_of_operation",
    RevokedAt = DateTime.UtcNow.ToString("O"),
    Serial    = "0x0001",
    Signature = caSignature,
};
```

---

### NDP — Discovery

NDP handles node address announcement and resolution (analogous to DNS).

**Setup with in-memory registry:**

```csharp
using NPS.NDP.Registry;

// Thread-safe in-memory registry; TTL expiry is evaluated lazily on read
var registry = new InMemoryNdpRegistry();
```

**AnnounceFrame — broadcast node address:**

```csharp
using NPS.NDP.Frames;

var announce = new AnnounceFrame
{
    Nid       = "urn:nps:node:example.com:data-store-01",
    NodeType  = "memory",
    Addresses =
    [
        new NdpAddress { Host = "10.0.0.5", Port = 17433, Protocol = "nwp+tls" },
        new NdpAddress { Host = "api.example.com", Port = 17433, Protocol = "nwp+tls" },
    ],
    Capabilities = ["nwp:query", "nwp:stream", "vector_search"],
    Ttl          = 300,   // seconds; 0 = orderly shutdown (evicts from registry)
};

registry.Announce(announce);
```

**ResolveFrame — resolve a NID to a physical endpoint:**

```csharp
using NPS.NDP.Frames;

var resolve = new ResolveFrame
{
    Nid      = "urn:nps:node:example.com:data-store-01",
    Protocol = "nwp+tls",
};

// Registry returns matching addresses
IReadOnlyList<NdpResolveResult> results = registry.Resolve(resolve.Nid);
foreach (var r in results)
{
    Console.WriteLine($"{r.Host}:{r.Port} TTL={r.Ttl}s");
}
```

**GraphFrame — full graph sync:**

```csharp
using NPS.NDP.Frames;

var graph = new GraphFrame
{
    Nodes = registry.GetAll().Select(a => new NdpGraphNode
    {
        Nid       = a.Nid,
        NodeType  = a.NodeType,
        Addresses = a.Addresses,
    }).ToList(),
};
```

---

### NOP — Orchestration

NOP executes multi-agent task DAGs. The `NopOrchestrator` dispatches sub-tasks to worker agents via `INopWorkerClient`.

**DI setup:**

```csharp
using NPS.NOP.Extensions;
using NPS.NOP.Orchestration;

// Register your worker client (dispatches DelegateFrames to agents over HTTP)
builder.Services.AddSingleton<INopWorkerClient, MyHttpWorkerClient>();
builder.Services.AddHttpClient();

// Register the orchestrator
builder.Services.AddNopOrchestrator(opts =>
{
    opts.MaxConcurrentNodes = 4;
    opts.DefaultTimeoutMs   = 30_000;
},
useInMemoryStore: true);  // false → register a custom INopTaskStore
```

**Define a TaskFrame:**

```csharp
using NPS.NOP.Frames;
using NPS.NOP.Models;

var task = new TaskFrame
{
    TaskId     = Guid.NewGuid().ToString(),
    Priority   = TaskPriority.Normal,
    TimeoutMs  = 60_000,
    MaxRetries = 2,
    Preflight  = true,
    CallbackUrl = "https://my-service.example.com/nop/callback",  // MUST be https://
    Dag        = new TaskDag
    {
        Nodes =
        [
            new DagNode
            {
                Id         = "fetch-data",
                AgentNid   = "urn:nps:agent:ca.example.com:fetcher-01",
                Action     = "fetch",
                Parameters = JsonDocument.Parse(@"{""url"":""https://data.example.com""}").RootElement,
            },
            new DagNode
            {
                Id         = "analyze",
                AgentNid   = "urn:nps:agent:ca.example.com:analyzer-01",
                Action     = "analyze",
                DependsOn  = ["fetch-data"],   // waits for fetch-data to complete
            },
        ],
    },
    Context = new TaskContext
    {
        RequestId = Guid.NewGuid().ToString(),
    },
};
```

**Execute a task:**

```csharp
var orchestrator = serviceProvider.GetRequiredService<INopOrchestrator>();

NopTaskResult result = await orchestrator.ExecuteAsync(task, CancellationToken.None);

if (result.Success)
{
    Console.WriteLine($"Task {result.TaskId} completed.");
    foreach (var (nodeId, nodeResult) in result.NodeResults)
        Console.WriteLine($"  {nodeId}: {nodeResult.Status}");
}
else
{
    Console.WriteLine($"Task failed: [{result.ErrorCode}] {result.ErrorMessage}");
}
```

**Implement INopWorkerClient:**

```csharp
using NPS.NOP.Orchestration;
using NPS.NOP.Frames;

public sealed class MyHttpWorkerClient : INopWorkerClient
{
    private readonly HttpClient _http;

    public MyHttpWorkerClient(IHttpClientFactory factory)
        => _http = factory.CreateClient();

    public async Task<DelegateResult> DelegateAsync(
        DelegateFrame frame, CancellationToken ct)
    {
        // Encode and POST to agent endpoint at port 17433
        var response = await _http.PostAsync(
            $"https://{frame.AgentHost}:17433/nop/delegate",
            new ByteArrayContent(codec.Encode(frame)), ct);

        return response.IsSuccessStatusCode
            ? DelegateResult.Ok(frame.SubTaskId)
            : DelegateResult.Fail(frame.SubTaskId, "NOP-DELEGATE-FAILED", "Worker error");
    }
}
```

**AlignStreamFrame — directed multi-agent state sync:**

```csharp
using NPS.NOP.Frames;

var align = new AlignStreamFrame
{
    TaskId    = task.TaskId,
    AgentNid  = "urn:nps:agent:ca.example.com:coordinator-01",
    Payload   = JsonDocument.Parse(@"{""checkpoint"":42,""state"":""ok""}").RootElement,
    Sequence  = 1,
};
```

---

## API Reference Summary

### NPS.Core

| Type | Namespace | Description |
|------|-----------|-------------|
| `NpsFrameCodec` | `NPS.Core.Codecs` | Encode/decode frames using Tier-1 (JSON) or Tier-2 (MsgPack) |
| `Tier1JsonCodec` | `NPS.Core.Codecs` | Raw JSON codec (development / debugging) |
| `Tier2MsgPackCodec` | `NPS.Core.Codecs` | Raw MsgPack codec (production default) |
| `AnchorFrameCache` | `NPS.Core.Caching` | Scoped per-session AnchorFrame cache with TTL |
| `FrameRegistry` | `NPS.Core.Registry` | Maps `FrameType` bytes to concrete frame types |
| `AnchorFrame` | `NPS.Core.Frames.Ncp` | Schema anchor — establishes a global schema reference |
| `DiffFrame` | `NPS.Core.Frames.Ncp` | Incremental patch — transmits only changed fields |
| `StreamFrame` | `NPS.Core.Frames.Ncp` | Ordered streaming chunk with back-pressure support |
| `CapsFrame` | `NPS.Core.Frames.Ncp` | Full response envelope referencing an anchor |
| `HelloFrame` | `NPS.Core.Frames.Ncp` | Native-mode client handshake (NPS-1 §4.6) |
| `ErrorFrame` | `NPS.Core.Frames.Ncp` | Unified error frame across all protocol layers (0xFE) |
| `FrameType` | `NPS.Core.Frames` | Enum of all frame byte codes (NCP 0x01–0x06, NWP 0x10–0x11, NIP 0x20–0x22, NDP 0x30–0x32, NOP 0x40–0x43) |
| `EncodingTier` | `NPS.Core.Frames` | `Json` (Tier-1) or `MsgPack` (Tier-2) |
| `NpsCoreOptions` | `NPS.Core.Extensions` | Options for `AddNpsCore()` |
| `NpsCoreServiceExtensions` | `NPS.Core.Extensions` | `AddNpsCore()` DI registration |

### NPS.NWP

| Type | Namespace | Description |
|------|-----------|-------------|
| `QueryFrame` | `NPS.NWP.Frames` | Structured data query (0x10); supports filter DSL, pagination, vector search |
| `ActionFrame` | `NPS.NWP.Frames` | Operation invocation (0x11) |
| `MemoryNodeMiddleware` | `NPS.NWP.MemoryNode` | ASP.NET Core middleware exposing /.nwm, /.schema, /query, /stream |
| `IMemoryNodeProvider` | `NPS.NWP.MemoryNode` | Implement to provide schema and query results |
| `MemoryNodeOptions` | `NPS.NWP.MemoryNode` | PathPrefix, NodeNid, capability flags |
| `MemoryNodeSchema` | `NPS.NWP.MemoryNode` | Schema definition returned from `GetSchema()` |
| `NwpOptions` | `NPS.NWP.Extensions` | Port, DefaultTokenBudget, MaxDepth, DefaultLimit |
| `NwpServiceExtensions` | `NPS.NWP.Extensions` | `AddNwp()`, `AddMemoryNode<T>()` DI registration |

### NPS.NIP

| Type | Namespace | Description |
|------|-----------|-------------|
| `IdentFrame` | `NPS.NIP.Frames` | Agent identity declaration + certificate (0x20) |
| `TrustFrame` | `NPS.NIP.Frames` | Cross-CA trust chain delegation (0x21) |
| `RevokeFrame` | `NPS.NIP.Frames` | Revoke a NID or capability grant (0x22) |
| `NipKeyManager` | `NPS.NIP.Crypto` | Ed25519 keypair generation, AES-256-GCM encrypted persistence, PBKDF2-SHA256 key derivation |
| `NipSigner` | `NPS.NIP.Crypto` | Ed25519 sign and verify; produces `ed25519:<base64url>` signatures |

### NPS.NDP

| Type | Namespace | Description |
|------|-----------|-------------|
| `AnnounceFrame` | `NPS.NDP.Frames` | Node/agent capability broadcast (0x30) |
| `ResolveFrame` | `NPS.NDP.Frames` | Resolve nwp:// address to physical endpoint (0x31) |
| `GraphFrame` | `NPS.NDP.Frames` | Full node graph sync (0x32) |
| `InMemoryNdpRegistry` | `NPS.NDP.Registry` | Thread-safe in-memory registry; lazy TTL eviction |
| `INdpRegistry` | `NPS.NDP.Registry` | Interface to implement a custom registry backend |
| `NdpAddress` | `NPS.NDP.Frames` | Physical address entry (host, port, protocol) |
| `NdpResolveResult` | `NPS.NDP.Frames` | Resolved endpoint with cert fingerprint and TTL |
| `NdpGraphNode` | `NPS.NDP.Frames` | Single node entry in a GraphFrame |

### NPS.NOP

| Type | Namespace | Description |
|------|-----------|-------------|
| `TaskFrame` | `NPS.NOP.Frames` | Task definition + DAG dispatch (0x40) |
| `DelegateFrame` | `NPS.NOP.Frames` | Sub-task delegation to a worker agent (0x41) |
| `SyncFrame` | `NPS.NOP.Frames` | Multi-agent state synchronisation point (0x42) |
| `AlignStreamFrame` | `NPS.NOP.Frames` | Directed task stream; supersedes AlignFrame (0x43) |
| `NopOrchestrator` | `NPS.NOP.Orchestration` | Core orchestrator; DAG execution, retries, condition skipping, result aggregation |
| `INopOrchestrator` | `NPS.NOP.Orchestration` | Interface for `ExecuteAsync()` and `CancelAsync()` |
| `INopWorkerClient` | `NPS.NOP.Orchestration` | Implement to dispatch DelegateFrames to worker agents |
| `INopTaskStore` | `NPS.NOP.Orchestration` | Interface for task persistence; default: in-memory |
| `NopOrchestratorOptions` | `NPS.NOP.Orchestration` | MaxConcurrentNodes, DefaultTimeoutMs, retry policy |
| `NopServiceExtensions` | `NPS.NOP.Extensions` | `AddNopOrchestrator()` DI registration |
| `NopConstants` | `NPS.NOP` | `MaxDelegateChainDepth` = 3, `MaxDagNodes` = 32 |

---

## Configuration

### Default Values

| Setting | Default | Description |
|---------|---------|-------------|
| Port | 17433 | Unified NPS port (all protocols share this port) |
| Encoding | MsgPack (Tier-2) | ~60% size reduction vs JSON |
| AnchorFrame TTL | 3600 s | Schema cache lifetime |
| Max frame payload | 65 535 bytes (64 KiB) | EXT=0 mode; enable `EnableExtendedFrameHeader` for up to 4 GB |
| Max query limit | 1000 records | Per QueryFrame specification |
| Default query limit | 20 records | When `QueryFrame.Limit` is absent |
| Max NOP DAG nodes | 32 | `NopConstants.MaxDagNodes` |
| Max delegate chain depth | 3 | `NopConstants.MaxDelegateChainDepth` |
| Max graph traversal depth | 5 | `X-NWP-Depth` header limit |
| Agent cert validity | 30 days | CA server default |
| Node cert validity | 90 days | CA server default |

### Extended Frame Header

For payloads exceeding 64 KiB, enable the 8-byte extended frame header (EXT=1):

```csharp
builder.Services.AddNpsCore(opts =>
{
    opts.EnableExtendedFrameHeader = true;
    opts.MaxFramePayload = 4_294_967_295; // up to 4 GB
});
```

### Token Budget

Set per-connection token budget limits via the `X-NWP-Budget` HTTP header or via `NwpOptions`:

```csharp
builder.Services.AddNwp(opts =>
{
    opts.DefaultTokenBudget = 100_000; // CGN units; 0 = unlimited
});
```

---

## Testing

Run the full test suite (429 tests):

```bash
cd /path/to/release/1.0.0-alpha.1
dotnet test NPS.sln --verbosity normal
```

Run a specific project:

```bash
dotnet test tests/NPS.Tests/NPS.Tests.csproj --verbosity normal
```

Filter by category:

```bash
# Run only NWP tests
dotnet test --filter "Category=NWP"

# Run only integration tests
dotnet test --filter "Category=Integration"
```

Generate coverage report:

```bash
dotnet test NPS.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults

# Generate HTML report (requires reportgenerator tool)
reportgenerator \
  -reports:"./TestResults/**/coverage.cobertura.xml" \
  -targetdir:"./TestResults/html" \
  -reporttypes:Html
```

**Coverage targets:**
- Overall: ≥ 90%
- NPS.Core: ≥ 95%
- NPS.NWP: ≥ 90%
- NPS.NIP: ≥ 90%
- NPS.NDP: ≥ 90%
- NPS.NOP: ≥ 90%

---

*Copyright 2026 INNO LOTUS PTY LTD — Licensed under the Apache License, Version 2.0*
