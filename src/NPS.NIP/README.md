# LabAcacia.NPS.NIP

> **Neural Identity Protocol** CA Server library and Agent verifier for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.NIP.svg)](https://www.nuget.org/packages/LabAcacia.NPS.NIP/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-3 NIP v0.6](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-3-NIP.md)

NIP is the TLS/PKI of NPS. This package ships everything you need to run a CA
Server that issues / renews / revokes NID certificates (Ed25519) and to verify
a peer's `IdentFrame` inside any ASP.NET Core app. Key material is AES-256-GCM
encrypted with PBKDF2-SHA256 (600 000 iterations).

## Install

```bash
dotnet add package LabAcacia.NPS.NIP
```

## Quick start — CA server

```csharp
using NPS.NIP.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNipCa(o =>
{
    o.CaNid            = "urn:nps:org:ca.example.com";
    o.DisplayName      = "My CA";
    o.KeyFilePath      = "/data/ca.key.enc";
    o.KeyPassphrase    = Environment.GetEnvironmentVariable("CA_PASSPHRASE")!;
    o.BaseUrl          = "https://ca.example.com";
    o.ConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONN")!;
    // optional
    o.AgentCertValidityDays = 30;   // default
    o.NodeCertValidityDays  = 90;   // default
    o.OperatorApiKey        = Environment.GetEnvironmentVariable("OPERATOR_API_KEY");
}, generateKeyIfMissing: true);

var app = builder.Build();
app.MapNipCa();   // all NIP CA routes: /v1/agents/*, /v1/nodes/*, /v1/ca/*, /.well-known/nps-ca
app.Run();
```

## Quick start — verifying a peer

```csharp
using NPS.NIP.Extensions;
using NPS.NIP.Verification;

// registration
services.AddNipVerifier(o =>
{
    o.TrustedIssuers["urn:nps:org:ca.example.com"] = "ed25519:<base64url-pubkey>";
    o.OcspUrl = "https://ca.example.com/v1/agents/{nid}/verify";
});

// usage
var verifier = sp.GetRequiredService<NipIdentVerifier>();

NipIdentVerifyResult result = await verifier.VerifyAsync(peerIdentFrame, new NipVerifyContext
{
    RequiredCapabilities = ["nwp:query"],
    TargetNodePath       = "nwp://api.myapp.com/products",
});

if (!result.IsValid)
    throw new UnauthorizedAccessException(result.Message);
```

The verifier runs the full NPS-3 §7 six-step flow: expiry → trusted issuer →
signature → revocation (OCSP fail-open per RFC 6960 §2.4, CRL fallback) →
capabilities → scope.

## Key types

| Type | Purpose |
|------|---------|
| `NipKeyManager`, `NipSigner` | Ed25519 key generation, signing, canonical-JSON signatures |
| `IdentFrame`, `TrustFrame`, `RevokeFrame` | NIP frame set |
| `NipCaService`, `NipCaOptions`, `INipCaStore`, `PostgreSqlNipCaStore` | CA service + storage |
| `NipCaRouter` | `MapNipCa()` HTTP surface — all `/v1/agents/*`, `/v1/nodes/*`, `/v1/ca/cert`, `/v1/crl`, `/.well-known/nps-ca` routes |
| `NipIdentVerifier`, `NipVerifyContext`, `NipIdentVerifyResult` | Node-side peer verification |
| `NipErrorCodes` | `NIP-*` error code constants |

## Documentation

- **Full API reference:** [`doc/NPS.NIP.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.NIP.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-3 NIP](https://github.com/labacacia/NPS-Release/blob/main/spec/NPS-3-NIP.md)

## NPS Repositories

| Purpose | Repo |
|---------|------|
| Spec + Release notes | [NPS-Release](https://github.com/labacacia/NPS-Release) |
| .NET SDK (this package) | [NPS-sdk-dotnet](https://github.com/labacacia/NPS-sdk-dotnet) |
| Python SDK | [NPS-sdk-py](https://github.com/labacacia/NPS-sdk-py) |
| TypeScript SDK | [NPS-sdk-ts](https://github.com/labacacia/NPS-sdk-ts) |
| Java SDK | [NPS-sdk-java](https://github.com/labacacia/NPS-sdk-java) |
| Rust SDK | [NPS-sdk-rust](https://github.com/labacacia/NPS-sdk-rust) |
| Go SDK | [NPS-sdk-go](https://github.com/labacacia/NPS-sdk-go) |

## License

Apache 2.0 © LabAcacia / INNO LOTUS PTY LTD
