# LabAcacia.NPS.NIP

> **Neural Identity Protocol** CA Server library and Agent verifier for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.NIP.svg)](https://www.nuget.org/packages/LabAcacia.NPS.NIP/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-3 NIP v0.2](https://github.com/labacacia/nps/blob/main/spec/NPS-3-NIP.md)

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
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddNpsCore()
    .AddNip()          // frame registry: IdentFrame / TrustFrame / RevokeFrame
    .AddNipCa(o =>
    {
        o.CaAuthority      = "api.example.com";
        o.CaName           = "root";
        o.CertValidityDays = 365;
        o.OcspValiditySec  = 3600;
    });

var app = builder.Build();
app.MapNipCa();        // /.well-known/nps-ca + /ca/register|renew|revoke|ocsp|crl
app.Run();
```

## Quick start — verifying a peer

```csharp
using NPS.NIP.Verifier;

services.AddNipVerifier();                 // registers NipIdentVerifier
var verifier = sp.GetRequiredService<NipIdentVerifier>();

NipVerifyResult result = await verifier.VerifyAsync(peerIdentFrame, new NipVerifyContext
{
    RequiredScope        = "products:read",
    RequiredCapabilities = ["nwp:query"],
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
| `NipCaRouter` | `MapNipCa()` HTTP surface (OCSP, CRL, register, renew, revoke) |
| `NipIdentVerifier`, `NipVerifyContext`, `NipVerifyResult` | Agent-side verification |
| `NipErrorCodes` | `NIP-*` error code constants |

## Documentation

- **Full API reference:** [`doc/NPS.NIP.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.NIP.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-3 NIP](https://github.com/labacacia/nps/blob/main/spec/NPS-3-NIP.md)

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
