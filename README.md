English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## Projects

| Project | Version | Description |
|---------|---------|-------------|
| `NPS.Core` | 1.0.0-alpha.3 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), encoding/decoding (JSON/MsgPack), AnchorFrame cache, frame registry, NCP connection preamble (RFC-0001) |
| `NPS.NWP` | 1.0.0-alpha.3 | Neural Web Protocol — NWM manifest, Query/Action frames, Memory/Action/Complex Node middleware |
| `NPS.NWP.Anchor` | 1.0.0-alpha.3 | Anchor Node middleware (CR-0001 — replaces former Gateway Node package) — stateless AaaS entry point that translates ActionFrames into NOP TaskFrames |
| `NPS.NWP.Bridge` | 1.0.0-alpha.3 | Bridge Node skeleton (CR-0001) — stateless translator from NPS frames to non-NPS protocols (HTTP / gRPC / MCP / A2A) |
| `NPS.NIP` | 1.0.0-alpha.3 | Neural Identity Protocol — CA, key generation, certificate issuance/revocation, OCSP, CRL, assurance levels (RFC-0003), reputation log (RFC-0004) |
| `NPS.NDP` | 1.0.0-alpha.3 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation, AnnounceFrame `node_kind`/`cluster_anchor`/`bridge_protocols` |
| `NPS.NOP` | 1.0.0-alpha.3 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Under active development (v1.0.0-alpha.3). All seven NWP/NIP/NDP/NOP packages are implemented; **575 tests passing**.

Includes NCP native-mode HelloFrame (0x06) handshake + RFC-0001 connection preamble, Tier-1 JSON / Tier-2 MsgPack codecs, AnchorFrame JCS canonicalization, NWP Memory/Action/Complex Nodes + new Anchor & Bridge Node packages (CR-0001), NIP CA + OCSP/CRL + RFC-0003 assurance levels + RFC-0004 reputation log, NDP registry + announce validation with `node_kind`/`cluster_anchor`/`bridge_protocols`, NOP DAG orchestration with delegation/sync/alignment.
