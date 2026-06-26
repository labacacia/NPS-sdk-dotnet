English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](../../LICENSE)
[![Release](https://img.shields.io/badge/release-v1.0.0--alpha.14-orange.svg)](../../CHANGELOG.md)
[![NCP](https://img.shields.io/badge/NCP-v0.8-5b8cff.svg)]()
[![NWP](https://img.shields.io/badge/NWP-v0.14-4af0b0.svg)]()
[![NIP](https://img.shields.io/badge/NIP-v0.10-7b61ff.svg)]()
[![NDP](https://img.shields.io/badge/NDP-v0.9-f0a050.svg)]()
[![NOP](https://img.shields.io/badge/NOP-v0.7-ff8c42.svg)]()

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## NuGet Packages

| Package | Version | Description |
|---------|---------|-------------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.14 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), JSON/MsgPack codecs, AnchorFrame cache, frame registry |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.14 | Neural Web Protocol — NWM manifest, Query/Action/Subscribe/Diff frames, Memory/Action/Complex/Anchor/Bridge Node middleware plus native-mode serving |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.14 | NWP Anchor Node: stateless AaaS entry point translating ActionFrames to NOP TaskFrames; `AnchorNodeClient` for `topology.snapshot` / `topology.stream` queries |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.14 | NWP Bridge Node: stateless dispatcher from NPS frames to non-NPS protocols, with built-in HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC adapters |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.14 | Neural Identity Protocol — CA, Ed25519 key generation, IdentFrame issuance/revocation, typed remote CA client, OCSP, CRL; X.509 + ACME `agent-01` challenge (RFC-0002 prototype) |
| `LabAcacia.NPS.NIP.Storage.Sqlite` | 1.0.0-alpha.14 | SQLite storage backend for embedded/self-hosted NIP CA deployments |
| `LabAcacia.NPS.NIP.Storage.Postgres` | 1.0.0-alpha.14 | PostgreSQL storage backend for service NIP CA deployments |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.14 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.14 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |
| `LabAcacia.NPS.Daemon.Observability` | 1.0.0-alpha.14 | JSON logging, transport-neutral health/readiness renderers, ASP.NET endpoint helpers, Prometheus metrics, graceful shutdown |
| `LabAcacia.NPS.Conformance` | 1.0.0-alpha.14 | Node L1/L2 conformance case catalog, run manifest model, and CI validation helpers |

## Open vs NPS Cloud

| Area | Open package support | NPS Cloud / commercial support |
|------|----------------------|--------------------------------|
| NCP/NWP/NDP/NOP frame codecs and in-process services | Included | Managed hosting and operations |
| NIP CA, IdentFrame issuance, revocation, OCSP/CRL, X.509 prototype | Included | Managed CA operations and policy automation |
| TrustFrame parsing and basic validation | Included via `TrustFrame` and `TrustFrameValidator` for explicitly pinned grantor anchors | Hosted multi-CA federation, managed trust-anchor discovery, revocation feeds, and commercial trust-chain policy |
| Daemon observability | Included as transport-neutral renderers plus ASP.NET mapping helpers | Hosted monitoring/SLO integration |

## Quickstarts

### Core Codec

```csharp
using NPS.Core.Codecs;
using NPS.Core.Frames.Ncp;

var codec = NpsFrameCodec.CreateDefault();
var frame = new HelloFrame { Version = "1.0", NodeId = "urn:nps:demo", Capabilities = ["ncp"] };
var wire = codec.Encode(frame);
var header = codec.Peek(wire);
var decoded = codec.Decode(wire);
```

`LabAcacia.NPS.Core` uses MessagePack-CSharp for the built-in Tier-2 codec.
Hosts that need a different binary codec can supply their own `IFrameCodec`
implementation by constructing `NpsFrameCodec` with custom codec instances;
the DI helper wires the default JSON + MessagePack pair.

### Dependency Injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Extensions;

var services = new ServiceCollection()
    .AddNpsCore(options => options.EnableExtendedFrameHeader = true);
```

### NIP Basic TrustFrame Validation

```csharp
using NPS.NIP.Verification;

var result = TrustFrameValidator.Validate(trustFrame, new TrustFrameValidationContext
{
    TrustedGrantors = new HashSet<string> { "urn:nps:org-a:ca" },
    ExpectedGranteeCa = "urn:nps:org-b:ca",
    RequiredCapabilities = ["nwp:query"],
    TargetNodePath = "nwp://api.example.com/products",
});
```

### Observability Without Kestrel

```csharp
using NPS.Daemon.Observability.HealthChecks;

var health = HealthProbeRenderer.RenderHealthz();
var ready = await HealthProbeRenderer.RenderReadyzAsync(readinessProbes, cancellationToken);
```

### ASP.NET Observability Endpoints

```csharp
using NPS.Daemon.Observability;

builder.Services.AddNpsObservability();
app.MapNpsObservability();
```

### Storage Packages

```csharp
using NPS.NIP.Extensions;

services.AddNipCaWithSqlite(
    options => ConfigureCa(options),
    "Data Source=nip-ca.db");

services.AddNipCaWithPostgres(options =>
{
    ConfigureCa(options);
    options.ConnectionString = postgresConnectionString;
});
```

### Bridge and Ingress Packages

`LabAcacia.NPS.NWP.Bridge` models the NPS-to-external Bridge Node path and ships built-in HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC dispatchers. The external-to-NPS adapters are published as separate packages: `LabAcacia.McpIngress`, `LabAcacia.A2aIngress`, and `LabAcacia.GrpcIngress`.

```csharp
using System.Text.Json;
using NPS.NWP.Bridge;
using NPS.NWP.Frames;

var registry = BridgeDispatcherRegistry.CreateDefault(new HttpClient());
var bridge = new BridgeNode(registry);

using var parameters = JsonDocument.Parse("""
{
  "bridge_target": {
    "protocol": "http",
    "endpoint": "https://api.example.test/run",
    "method": "POST",
    "allowed_prefixes": [ "https://api.example.test/" ],
    "headers": { "x-agent": "nps" }
  },
  "body": { "task": "sync" }
}
""");

var response = await bridge.DispatchAsync(new ActionFrame
{
    ActionId = "bridge.dispatch",
    Params = parameters.RootElement.Clone(),
    TimeoutMs = 5000
});
```

ASP.NET hosts can expose the same Bridge Node over NWP endpoints:

```csharp
builder.Services.AddBridgeNode(
    options =>
    {
        options.NodeId = "bridge-1";
        options.PathPrefix = "/bridge";
    },
    dispatchers =>
    {
        // Optional: dispatchers.Register(new GrpcBridgeDispatcher(...));
    });

app.UseBridgeNode(); // GET /bridge/.nwm, GET /bridge/actions, POST /bridge/invoke
```

The hosted Bridge Node uses the named `HttpClient` `nps-bridge`; configure it
through `IHttpClientFactory` when you need custom timeout, proxy, TLS, or retry
policy:

```csharp
builder.Services.AddHttpClient(BridgeServiceExtensions.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

Bridge dispatchers reject private and loopback endpoint hosts by default
(`reject_private=true`). Set `reject_private=false` only for trusted local
development targets.

## Native NCP and Revocation

Native-mode NCP listeners can require an authenticated stream before the
`NPS/1.0\n` preamble is read. Use the hook to install `SslStream` for TLS/mTLS:

```csharp
var server = new NcpServer(port, NpsFrameCodec.CreateDefault(), new NcpServerOptions
{
    RequireAuthenticatedStream = true,
    HandshakeReadTimeout = TimeSpan.FromSeconds(10),
    MaxHelloPayload = 16 * 1024,
    AuthenticateStreamAsync = async (_, stream, ct) =>
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(serverOptions, ct);
        return tls;
    }
});
```

`NipIdentVerifier` supports static CRLs, live callbacks, CA stores, and OCSP.
OCSP transport failures fail closed by default; set `OcspFailOpen=true` only
when the host policy explicitly accepts that risk.

```csharp
var verifier = new NipIdentVerifier(new NipVerifierOptions
{
    TrustedIssuers = trustedIssuers,
    RevocationStore = await SqliteNipCaStore.OpenAsync("Data Source=nip-ca.db"),
    RevocationCheck = (frame, ct) => ValueTask.FromResult<NipIdentVerifyResult?>(null),
    OcspUrl = "https://ca.example.test/v1/agents"
}, httpClientFactory);
```

### Remote NIP CA Client

```csharp
using NPS.NIP.Client;

httpClient.BaseAddress = new Uri("https://ca.example.test/");
var ca = new NipCaClient(httpClient);
var discovery = await ca.GetDiscoveryAsync(cancellationToken);
var issued = await ca.RegisterAgentAsync(new NipCaRegisterRequest(
    Identifier: "agent-1",
    PubKey: publicKeyBase64Url,
    Capabilities: ["nwp:query"],
    ScopeJson: "{}"),
    bearerToken,
    cancellationToken);
```

### Native NWP Serving

```csharp
using NPS.Core.Codecs;
using NPS.Core.Ncp;
using NPS.NWP.Native;

var nativeNode = new NwpNativeNodeServer(
    NpsFrameCodec.CreateDefault(),
    new NwpNativeNodeOptions { MemoryOptions = memoryOptions, ActionOptions = actionOptions },
    memoryProvider,
    actionProvider);

await nativeNode.ServeAsync(ncpSession, cancellationToken);
```

### Conformance Manifests

```csharp
using NPS.Conformance;

var manifest = NpsConformanceManifest.Create(
    NpsConformanceProfiles.NodeL1,
    iutName: "my-node",
    iutVersion: "0.1.0",
    iutNid: "urn:nps:node:example.test:node-1",
    peerName: "nps-dotnet-reference",
    peerVersion: "1.0.0-alpha.14",
    results: caseResults);

var validation = NpsConformanceValidator.Validate(manifest);
```

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Active development (v1.0.0-alpha.14). 696 tests passing.

Alpha.14 highlights: warning-clean .NET package family with SourceLink symbols; native NCP TLS hook and bounded Hello reads; live NIP revocation checks and signed CRL artifacts; `NipCaClient`; `NwpNativeNodeServer`; built-in Bridge dispatchers for HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC; transport-neutral observability renderers; `LabAcacia.NPS.Conformance`; loopback dev stack.
