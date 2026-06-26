English | [中文版](./CHANGELOG.cn.md)

# Changelog — .NET SDK

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Until NPS reaches v1.0 stable, every repository in the suite is synchronized to the same pre-release version tag.

---

## [1.0.0-alpha.14] — Unreleased

### Added

- `NPS.NWP.Bridge` now includes concrete outbound dispatchers for HTTP/HTTPS, gRPC JSON unary, MCP JSON-RPC, and A2A JSON-RPC.
- Added `BridgeNode`, `BridgeDispatcherRegistry`, `IBridgeDispatcher`, `BridgeNodeMiddleware`, `AddBridgeNode`, and `UseBridgeNode` for host-independent and ASP.NET-hosted Bridge dispatch.
- Added `BridgeEndpointValidator` with private/loopback SSRF guard and URI-aware `allowed_prefixes` validation.
- Added `HealthProbeRenderer` for transport-neutral daemon health/readiness output.
- Added `TrustFrameValidator` for open-source pinned-grantor TrustFrame validation.
- Added `NpsFrameCodec.CreateDefault()`, `NpsFrameCodec.Create(...)`, and instance `Peek(...)`.
- Added `NcpServerOptions` with a pre-preamble authenticated stream hook, handshake read timeout, and bounded Hello payload size.
- Added live revocation hooks for `NipIdentVerifier`: `NipRevocationCheck`, `NipVerifierOptions.RevocationStore`, and secure-by-default OCSP transport failure handling.
- Added `INipCaStore.ListAsync()` and a production package `InMemoryNipCaStore` for tests, demos, and local development stacks.
- Added `NipCaClient`, a typed remote CA client for discovery, CRL, Ed25519 registration/renewal/revocation/verification, and RFC-0002 X.509 registration.
- Added `NwpNativeNodeServer` so Memory and Action Nodes can serve native-mode NWP traffic over `NcpSession` or any NCP stream.
- Added `LabAcacia.NPS.Conformance` with Node L1/L2 case catalogs, run manifests, and validation helpers for CI self-certification.

### Changed

- Public package projects now emit SourceLink-enabled `.snupkg` symbol packages.
- NuGet publish workflow now packs and verifies the complete public .NET package family, including `LabAcacia.NPS.Conformance`.
- `ActionNodeMiddleware` and `ComplexNodeMiddleware` avoid nullable `CapsFrame.AnchorRef` assignments.
- NIP CA CRL responses now include `issued_at` and a detached CA signature; CA discovery points OCSP clients at mapped verify endpoints.
- Test and build output is warning-clean for the SDK test gate.

### Docs

- Added Bridge target schema docs and expanded .NET quickstarts for Core codec, DI, TrustFrame validation, observability, storage, native NWP serving, Bridge, ingress, remote CA, and conformance packages.

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

[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
