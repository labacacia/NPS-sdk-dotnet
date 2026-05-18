English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-v1.0.0--alpha.7-orange.svg)](CHANGELOG.md)
[![NCP](https://img.shields.io/badge/NCP-v0.6-5b8cff.svg)]()
[![NWP](https://img.shields.io/badge/NWP-v0.12-4af0b0.svg)]()
[![NIP](https://img.shields.io/badge/NIP-v0.8-7b61ff.svg)]()
[![NDP](https://img.shields.io/badge/NDP-v0.7-f0a050.svg)]()
[![NOP](https://img.shields.io/badge/NOP-v0.5-ff8c42.svg)]()

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## NuGet Packages

| Package | Version | Description |
|---------|---------|-------------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.7 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), JSON/MsgPack codecs, AnchorFrame cache, frame registry |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.7 | Neural Web Protocol — NWM manifest, Query/Action/Subscribe/Diff frames, Memory/Action/Complex/Anchor/Bridge Node middleware |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.7 | NWP Anchor Node: stateless AaaS entry point translating ActionFrames to NOP TaskFrames; `AnchorNodeClient` for `topology.snapshot` / `topology.stream` queries |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.7 | NWP Bridge Node: stateless translator from NPS frames to non-NPS protocols (HTTP / gRPC / MCP / A2A target adapters) |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.7 | Neural Identity Protocol — CA, Ed25519 key generation, IdentFrame issuance/revocation, OCSP, CRL; X.509 + ACME `agent-01` challenge (RFC-0002 prototype) |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.7 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.7 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Active development (v1.0.0-alpha.7). 699 tests passing.

Alpha.4 highlights: NCP native-mode connection preamble (`NPS/1.0\n`) across all 5 non-.NET SDKs; NWP Anchor topology queries (`topology.snapshot` / `topology.stream`) + `AnchorNodeClient`; NIP X.509 + ACME `agent-01` prototype (RFC-0002); nps-registry SQLite backend; nps-ledger Phase 2 (SQLite + RFC 9162 Merkle tree + operator-signed STH + inclusion proof endpoint).
