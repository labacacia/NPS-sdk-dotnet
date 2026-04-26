English | [中文版](./CHANGELOG.cn.md)

# Changelog — .NET SDK

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.3] — 2026-04-25

The .NET SDK is the **reference implementation** for the NPS `v1.0.0-alpha.3` suite milestone — this release is where each newly Accepted RFC and CR lands as working code first.

### Added

- **`NPS.Core.Ncp.NcpPreamble`** (NPS-RFC-0001 — NCP connection preamble). Reference helper for the literal `b"NPS/1.0\n"` (8-byte) preamble that native-mode connections now begin with so receivers can disambiguate NPS framing from random bytes / TLS / HTTP. Provides `Bytes` / `ToArray()` / `Matches()` / `TryValidate()` / `Validate()` / `WriteAsync()`, with `Length=8`, `ReadTimeout=10s`, and a 500 ms close deadline. New `NCP-PREAMBLE-INVALID` error code in `NPS.Core.NcpErrorCodes` mapped to NPS status `NPS-PROTO-PREAMBLE-INVALID`.
- **`NPS.NIP.AssuranceLevel`** + `AssuranceLevels` static class + STJ converter (NPS-RFC-0003 — Agent identity assurance levels). Tri-state `anonymous` / `attested` / `verified`. Wired into `IdentFrame` (new `AssuranceLevel?` field), `NipVerifyContext` (new `MinAssuranceLevel`), and `NeuralWebManifest.MinAssuranceLevel`. Mismatch / unknown errors live under `NIP-AUTH-ASSURANCE-MISMATCH` / `NIP-AUTH-ASSURANCE-UNKNOWN`. NWP layer can reject below-threshold callers via `NWP-AUTH-ASSURANCE-TOO-LOW` / `NWP-AUTH-REPUTATION-BLOCKED`.
- **`NPS.NIP.Reputation`** namespace (NPS-RFC-0004 — NID reputation log, CT-style, Phase 1). New types `IncidentType`, `Severity`, `ReputationLogEntry`, `ReputationLogEntrySigner` — append-only Merkle log entry shape, with Ed25519-signed entries. Errors `NIP-REPUTATION-ENTRY-INVALID` and `NIP-REPUTATION-LOG-UNREACHABLE`.
- **`LabAcacia.NPS.NWP.Anchor`** — new package replacing the old **`LabAcacia.NPS.NWP.Gateway`** (NPS-CR-0001 — Anchor / Bridge node split). The legacy "Gateway Node" role is now **Anchor Node**: cluster control plane, stateless AaaS entry point. Public types renamed `Gateway*` → `Anchor*` (`AnchorNodeMiddleware`, `AnchorNodeOptions`, `IAnchorRouter`, `IAnchorRateLimiter`, `InMemoryAnchorRateLimiter`, `AnchorNodeMetadata`). Compile-time `[Obsolete(error: true)]` shims under `LegacyGatewayShim.cs` keep the old `GatewayNodeMiddleware` / `IGatewayRouter` / `IGatewayRateLimiter` / `LegacyGatewayServiceExtensions.UseGatewayNode` / `AddGatewayNode` symbols visible so consumers get a clear migration error pointing at the new types.
- **`LabAcacia.NPS.NWP.Bridge`** — new package (Phase 1 type-only skeleton). The "translate NPS↔external protocol" role is now its own **Bridge Node** type. Ships `BridgeNodeMetadata.NodeType="bridge"`, `BridgeProtocols.{Http,Grpc,Mcp,A2a}`, `BridgeNodeDescriptor`, `BridgeTarget`. AnnounceFrame in NDP gained `node_kind` / `cluster_anchor` / `bridge_protocols`.
- New tests: `NcpPreambleTests` (20), `AssuranceLevelTests` (27), `ReputationLogEntryTests` (33), `AnchorNodeMiddlewareTests` (renamed from `GatewayNodeMiddlewareTests`).

### Changed

- All published packages bumped to `1.0.0-alpha.3` for suite-wide synchronization: `LabAcacia.NPS.Core`, `LabAcacia.NPS.NWP`, `LabAcacia.NPS.NWP.Anchor` (was `.Gateway`), `LabAcacia.NPS.NWP.Bridge` (new), `LabAcacia.NPS.NIP`, `LabAcacia.NPS.NDP`, `LabAcacia.NPS.NOP`.
- `NPS.NWP.Nwm.NeuralWebManifest` `node_type` enum now permits `"anchor"` and `"bridge"` alongside the existing `memory` / `action` / `complex` (former `gateway` value still accepted by parsers but is documented as deprecated in favor of `anchor`).
- `NPS.Core.NpsStatusCodes` adds `ProtoPreambleInvalid` and `DownstreamUnavailable` (NPS-PROTO category introduced).
- 575 tests green (was 495 at alpha.2; +80 from RFC-0001/0003/0004 + Anchor middleware refresh).

### Suite-wide highlights

- **6 NPS resident daemons** (NPS-Dev `daemons/` tree). The new daemons live in NPS-Dev — not in NPS-sdk-dotnet — but consumers should know they exist:
  - `npsd` — host-local protocol ingress + state host (L1-functional reference at alpha.3)
  - `nps-runner` — host-local FaaS scheduler (skeleton)
  - `nps-gateway` — public Internet ingress (skeleton)
  - `nps-registry` — cross-machine NDP registry (skeleton)
  - `nps-cloud-ca` — X.509 issuance (skeleton — defers to per-language `tools/nip-ca-server*` until alpha.4)
  - `nps-ledger` — append-only NPS-RFC-0004 log collector (in-memory functional reference)

  See [`docs/daemons/architecture.md`](https://github.com/LabAcacia/NPS-Dev/blob/v1.0.0-alpha.3/docs/daemons/architecture.md) for the three-layer topology.

### Deferred to alpha.4

- **NPS-RFC-0001 Phase 2** — wire-level preamble runtime in `NPS.Core` NCP transport (Phase 1 ships the helper + error code + 20 unit tests; the actual native-mode listener side wiring lands at alpha.4).
- **NPS-RFC-0002** — X.509 + ACME for NID certs (still Draft).
- **NPS-CR-0002** — Anchor topology queries.

### Covered modules

- NPS.Core / NPS.NWP / NPS.NWP.Anchor / NPS.NWP.Bridge / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.2] — 2026-04-19

### Changed

- `NPS.Core` README explicitly enumerates the full NCP frame set (AnchorFrame / DiffFrame / StreamFrame / CapsFrame / HelloFrame / ErrorFrame).
- `NPS.NWP` README calls out all four node types (Memory / Action / Complex / Gateway).
- Status: 495 tests green, including new wire-size benchmark, Gateway Node middleware, and A2A Bridge.

### Covered modules

- NPS.Core / NPS.NWP / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.1] — 2026-04-10

First public alpha as part of the NPS suite `v1.0.0-alpha.1` release.

[1.0.0-alpha.3]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.3
[1.0.0-alpha.2]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.1
