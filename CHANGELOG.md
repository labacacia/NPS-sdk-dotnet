English | [中文版](./CHANGELOG.cn.md)

# Changelog — .NET SDK

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.5] — 2026-05-01

The .NET SDK remains the **reference implementation** for the NPS suite. This
release adds the alpha.5 spec suite (STH gossip error codes, `NPS-SERVER-UNSUPPORTED`,
topology capability gate, `cgn_est` per-event), the `AssuranceLevel` empty-string fix,
and full NDP DNS TXT fallback resolution.

### Added

- **`NPS.NWP.Anchor` — `NWP-RESERVED-TYPE-UNSUPPORTED` enforcement**: `AnchorNodeMiddleware`
  now returns HTTP 501 / `NPS-SERVER-UNSUPPORTED` / `NWP-RESERVED-TYPE-UNSUPPORTED` when
  `/anchor/query` or `/anchor/subscribe` receive an unrecognised reserved `type` value.
  Previously returned 404 / `NWP-ACTION-NOT-FOUND` (incorrect per spec).

- **`NPS.NWP.Anchor` — `topology:read` capability gate**: New `AnchorNodeOptions.RequireTopologyCapability`
  (default `false`). When `true`, both `/query` and `/subscribe` check the `X-NWP-Capabilities`
  request header for `"topology:read"` (case-insensitive, comma-separated); missing capability
  returns HTTP 403 / `NPS-AUTH-FORBIDDEN` / `NWP-TOPOLOGY-UNAUTHORIZED`. New
  `NwpHttpHeaders.Capabilities = "X-NWP-Capabilities"` constant added.

- **`NPS.NWP.Anchor` — `cgn_est` on `TopologyEventEnvelope`**: New nullable `cgn_est: uint?`
  field populated with `Math.Max(1, UTF8.GetByteCount(payload) / 4)` on every pushed event
  (per spec/token-budget.md §7.2 SHOULD).

- **`NPS.NDP` — `ResolveViaDns` DNS TXT fallback resolution**: New
  `InMemoryNdpRegistry.ResolveViaDns(target, dnsLookup?)` falls back to `_nps-node.{host}`
  TXT record lookup (NPS-4 §5) when no in-memory entry matches. New `IDnsTxtLookup` interface
  + `SystemDnsTxtLookup` (DnsClient v1.8.0 NuGet); `InMemoryNdpRegistry.ParseNpsTxtRecord`
  helper; `ResolveViaDns` added to `INdpRegistry` interface (with default `null` lookup for
  backward compatibility). Tests: 655 → 665.

### Changed

- **`NPS.NIP` — `AssuranceLevels.FromWireOrAnonymous("")` fix**: Empty string `""` now returns
  `Anonymous` (consistent with `null`). `FromWireOrAnonymous` doc updated; `FromWireOrAnonymous_UnknownNonEmpty_Throws`
  test added to enforce spec m6 forward-compat rule.

- **All 7 packages bumped to `1.0.0-alpha.5`** — synchronized with NPS suite alpha.5 release.

### Tests

- Test count: **629 → 665** (all passing).
  - 13 new `GossipStateTests` (STH gossip interval, peer storage, multi-peer, `FromEnvironment`
    parsing, NipSigner round-trip).
  - `AnchorTopologyTests` — 501 assertion updated from 404; new `MissingCapability_Returns403`.
  - `AssuranceLevelTests` — `NullOrEmpty_ReturnsAnonymous` + `UnknownNonEmpty_Throws` (m6 fix).
  - `NdpRegistryTests` — 10 new DNS TXT tests (happy path, invalid records, host extraction,
    in-memory priority, injectable mock resolver).

---

## [1.0.0-alpha.4] — 2026-04-30

The .NET SDK remains the **reference implementation** for the NPS suite. This
release lands NPS-RFC-0002 (X.509 + ACME) Phase A/B, NPS-RFC-0001 Phase 2
(NCP preamble runtime parity), and NPS-CR-0002 (Anchor topology queries).

### Added

- **`NPS.NIP.X509`** + **`NPS.NIP.Acme`** (NPS-RFC-0002 Phase A/B —
  X.509 NID certificates + ACME `agent-01` reference). New types:
  - `X509.NipCertBuilder` — builds X.509 certificates carrying NID
    custom OIDs (subject, assurance level, key authorisation).
  - `X509.NipX509Verifier` — verifies the X.509 leaf + chain against
    a configured trust anchor, returns the extracted NID + assurance
    level.
  - `Acme.AcmeAgent01Server` — server side of ACME `agent-01`
    challenge issuance + verification (challenge mint, key
    authorisation hashing, JWS-signed wire envelope).
  - `Acme.AcmeAgent01Client` — client side: nonce fetch, account
    register, order, key-authorisation answer, finalize, retrieve
    issued cert chain.
- **`NPS.NIP.Verification.NipIdentVerifier` dual-trust path** — the
  verifier now accepts IdentFrames carrying either `cert_format=v1`
  (Ed25519, alpha.3 wire format) or `cert_format=x509` (X.509 leaf +
  chain). v1 IdentFrames written by alpha.3 consumers continue to
  verify unchanged.
- **`NPS.NWP.Anchor.Topology`** + **`NPS.NWP.Anchor.Client`**
  (NPS-CR-0002 — Anchor Node topology queries). New types:
  - `Topology.TopologyTypes` — `topology.snapshot` + `topology.stream`
    query types, including the `IncludeMembers` / `IncludeCapabilities`
    / `IncludeTags` / `IncludeMetrics` filter flags and the
    `AnchorStateVersionRebased` rebase frame.
  - `IAnchorTopologyService` (in `Topology/`) — pluggable topology
    backing store interface (default in-memory implementation).
  - `Client.AnchorNodeClient` — typed client for invoking
    `topology.snapshot` (one-shot) and subscribing to `topology.stream`
    (long-lived, with explicit `AnchorStateVersionRebased` handling).
  - 10 NPS-AaaS L2 conformance tests (`AnchorTopology*Tests`) green.
- **NPS-RFC-0001 Phase 2** — wire-level NCP preamble runtime parity:
  the alpha.3 `NcpPreamble` helper is now invoked at the native-mode
  transport boundary (preamble parser + writer wired into the .NET
  framing pipeline).

### Changed

- All published packages bumped to `1.0.0-alpha.4`:
  `LabAcacia.NPS.Core`, `LabAcacia.NPS.NWP`, `LabAcacia.NPS.NWP.Anchor`,
  `LabAcacia.NPS.NWP.Bridge`, `LabAcacia.NPS.NIP`, `LabAcacia.NPS.NDP`,
  `LabAcacia.NPS.NOP`.
- `NPS.NIP.Frames.IdentFrame` wire shape extended with optional
  `cert_format` discriminator + `x509_chain` (DER-encoded leaf chain)
  alongside the existing v1 fields.
- `NPS.NIP.Crypto.NipSigner` learns to emit X.509-format IdentFrames
  when configured with an X.509 issuer; default behaviour
  (Ed25519-only) is unchanged.
- 639 tests green (was 575 at alpha.3; +64 from RFC-0002 X.509 + ACME
  port + CR-0002 Anchor topology + RFC-0001 Phase 2 runtime tests).

### Suite-wide highlights at alpha.4

- **NPS-RFC-0002 X.509 + ACME** — full cross-SDK port wave:
  .NET (this) / Java / Python / TypeScript / Go / Rust all carry
  parity X.509 builders + ACME `agent-01` round-trip. Server side of
  ACME runs in `NPS.NIP.Acme.AcmeAgent01Server` (this package); SDK
  consumers can embed it directly into their HTTP host.
- **`nps-registry` SQLite-backed real registry** + **`nps-ledger`
  Phase 2** (RFC 9162 Merkle tree + operator-signed STH + inclusion
  proofs) shipped in the daemon repos.

### Deferred to alpha.5+

- **NPS-RFC-0004 Phase 3** — STH gossip federation between
  `nps-ledger` operators (cross-organisation audit redundancy).
- **NPS-CR-0002 Phase 2** — server-side push of topology updates
  from Anchor Node middleware (alpha.4 ships the protocol + client +
  conformance tests; middleware-side push integration is a follow-up).
- Full Ed25519 signature verification on incoming reputation log
  entries (alpha.4 still validates the `ed25519:` prefix structurally).

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

[1.0.0-alpha.4]: https://github.com/labacacia/NPS-sdk-dotnet/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.3
[1.0.0-alpha.2]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.1
