English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## NuGet Packages

| Package | Version | Description |
|---------|---------|-------------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.4 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), JSON/MsgPack codecs, AnchorFrame cache, frame registry |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.4 | Neural Web Protocol — NWM manifest, Query/Action/Subscribe/Diff frames, Memory/Action/Complex/Anchor/Bridge Node middleware |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.4 | NWP Anchor Node: stateless AaaS entry point translating ActionFrames to NOP TaskFrames; `AnchorNodeClient` for `topology.snapshot` / `topology.stream` queries |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.4 | NWP Bridge Node: stateless translator from NPS frames to non-NPS protocols (HTTP / gRPC / MCP / A2A target adapters) |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.4 | Neural Identity Protocol — CA, Ed25519 key generation, IdentFrame issuance/revocation, OCSP, CRL; X.509 + ACME `agent-01` challenge (RFC-0002 prototype) |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.4 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.4 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Active development (v1.0.0-alpha.4). 629 tests passing.

Alpha.4 highlights: NCP native-mode connection preamble (`NPS/1.0\n`) across all 5 non-.NET SDKs; NWP Anchor topology queries (`topology.snapshot` / `topology.stream`) + `AnchorNodeClient`; NIP X.509 + ACME `agent-01` prototype (RFC-0002); nps-registry SQLite backend; nps-ledger Phase 2 (SQLite + RFC 9162 Merkle tree + operator-signed STH + inclusion proof endpoint).
