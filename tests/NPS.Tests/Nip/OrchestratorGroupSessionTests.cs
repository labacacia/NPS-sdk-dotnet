// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0
//
// NPS-CR-0003 — Orchestrator group NIDs and short-lived session NIDs.
// Covers the five scenarios required by the CR §5 acceptance criteria:
//   1. issuing group NID
//   2. issuing session NID linked to group NID
//   3. rejecting session NID without valid group authority
//   4. verifying parent / group metadata is signed and round-trips
//   5. group revocation blocking future session issuance (and cascading)

using System.Text;
using System.Text.Json;
using NPS.NIP;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NSec.Cryptography;

namespace NPS.Tests.Nip;

public sealed class OrchestratorGroupSessionTests : IDisposable
{
    private const string CaNid = "urn:nps:org:ca.test.example";

    private readonly string         _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly NipKeyManager  _caKeys;
    private readonly NipCaOptions   _opts;
    private readonly InMemoryStore  _store;
    private readonly NipCaService   _svc;

    public OrchestratorGroupSessionTests()
    {
        Directory.CreateDirectory(_tempDir);

        _caKeys = new NipKeyManager();
        _caKeys.Generate(Path.Combine(_tempDir, "ca.key.enc"), "testpass");

        _opts = new NipCaOptions
        {
            CaNid                  = CaNid,
            KeyFilePath            = Path.Combine(_tempDir, "ca.key.enc"),
            KeyPassphrase          = "testpass",
            BaseUrl                = "https://ca.test.example",
            AgentCertValidityDays  = 30,
            NodeCertValidityDays   = 90,
            RenewalWindowDays      = 7,
            GroupCertValidityDays  = 365,
            SessionDefaultValidity = TimeSpan.FromHours(1),
            SessionMaxValidity     = TimeSpan.FromHours(24),
            SessionMinValidity     = TimeSpan.FromMinutes(1),
            SessionJwsClockSkew    = TimeSpan.FromMinutes(5),
        };

        _store = new InMemoryStore();
        _svc   = new NipCaService(_opts, _store, _caKeys);
    }

    public void Dispose()
    {
        _caKeys.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Scenario 1: issuing group NID ─────────────────────────────────────────

    [Fact]
    public async Task IssuingGroup_ReturnsLineageGroupFrame()
    {
        var (group, _) = await IssueGroup(ownerUserId: "user-7f3c", ownerKeyId: "op-kid-2026");

        Assert.StartsWith($"urn:nps:agent:ca.test.example:group-", group.Nid);
        Assert.NotNull(group.Lineage);
        Assert.Equal(IdentLineageRole.Group, group.Lineage!.Role);
        Assert.Equal("user-7f3c", group.Lineage.OwnerUserId);
        Assert.Equal("op-kid-2026", group.Lineage.OwnerKeyId);
        Assert.Null(group.Lineage.ParentNid);
        Assert.Null(group.Lineage.GroupNid);
        Assert.StartsWith("ed25519:", group.Signature);

        // Stored record carries role=group, parent_nid=null, lineage_json populated
        var record = await _store.GetByNidAsync(group.Nid);
        Assert.NotNull(record);
        Assert.Equal(IdentLineageRole.Group, record!.NidRole);
        Assert.Null(record.ParentNid);
        Assert.False(string.IsNullOrEmpty(record.LineageJson));
    }

    [Fact]
    public async Task IssuingGroup_WithExplicitNonGroupIdentifier_IsRejected()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var pubKey = NipSigner.EncodePublicKey(key.PublicKey);

        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.RegisterGroupAsync(
                identifier:  "not-a-group-prefix",
                pubKey:      pubKey,
                capabilities: ["nwp:query"],
                scopeJson:   """{"nodes":["nwp://api.test.example/*"]}"""));
        Assert.Contains("group-", ex.Message);
    }

    [Fact]
    public async Task IssuingGroup_DefaultExpiry_Is365Days()
    {
        var before     = DateTime.UtcNow;
        var (group, _) = await IssueGroup();
        var expires    = DateTime.Parse(group.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(expires >= before.AddDays(364).AddHours(23));
        Assert.True(expires <= before.AddDays(365).AddSeconds(5));
    }

    // ── Scenario 2: issuing session NID linked to group ───────────────────────

    [Fact]
    public async Task IssuingSession_ChainsToGroup()
    {
        var (group, _)   = await IssueGroup();
        var (sess, _)    = await IssueSession(group.Nid, purpose: "data-extraction-job-42");

        Assert.Contains(":session-", sess.Nid);
        Assert.NotNull(sess.Lineage);
        var l = sess.Lineage!;
        Assert.Equal(IdentLineageRole.Session, l.Role);
        Assert.Equal(group.Nid, l.ParentNid);
        Assert.Equal(group.Nid, l.GroupNid);
        Assert.NotNull(l.SessionId);
        Assert.StartsWith("session-", l.SessionId);
        // session_id MUST match the URN's session-... segment
        var urnSeg = sess.Nid.Substring(sess.Nid.LastIndexOf(':') + 1);
        Assert.Equal(urnSeg, l.SessionId);
        Assert.Equal("data-extraction-job-42", l.Purpose);
    }

    [Fact]
    public async Task IssuingSession_DefaultExpiry_Is1Hour()
    {
        var (group, _) = await IssueGroup();
        var before     = DateTime.UtcNow;
        var (sess, _)  = await IssueSession(group.Nid);
        var expires    = DateTime.Parse(sess.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(expires >= before.AddMinutes(59));
        Assert.True(expires <= before.AddMinutes(60).AddSeconds(5));
    }

    [Fact]
    public async Task IssuingSession_ValidityBelowMin_IsRejected()
    {
        var (group, _) = await IssueGroup();
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            IssueSession(group.Nid, validity: TimeSpan.FromSeconds(10)));
        Assert.Equal(NipErrorCodes.SessionValidityInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task IssuingSession_ValidityAboveMax_IsRejected()
    {
        var (group, _) = await IssueGroup();
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            IssueSession(group.Nid, validity: TimeSpan.FromHours(48)));
        Assert.Equal(NipErrorCodes.SessionValidityInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task IssuingSession_CapabilityNotInGroup_IsRejected()
    {
        var (group, _) = await IssueGroup(capabilities: ["nwp:query"]);
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            IssueSession(group.Nid, capabilities: ["nwp:query", "nwp:action"]));
        Assert.Equal(NipErrorCodes.ScopeExpansion, ex.ErrorCode);
    }

    [Fact]
    public async Task IssuingSession_InheritsGroupCapabilities_WhenNotOverridden()
    {
        var (group, _)  = await IssueGroup(capabilities: ["nwp:query", "nwp:stream"]);
        var (sess, _)   = await IssueSession(group.Nid);
        Assert.Equal(group.Capabilities.OrderBy(c => c), sess.Capabilities.OrderBy(c => c));
    }

    // ── Scenario 3: rejecting session without valid authority ─────────────────

    [Fact]
    public async Task IssuingSession_GroupNotFound_IsRejected()
    {
        using var sessKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.IssueSessionAsync(
                groupNid:      "urn:nps:agent:ca.test.example:group-does-not-exist",
                sessionPubKey: NipSigner.EncodePublicKey(sessKey.PublicKey)));
        Assert.Equal(NipErrorCodes.ParentNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task IssuingSession_ParentIsAgentNotGroup_IsRejected()
    {
        // Register an ordinary agent (no lineage)
        using var agentKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        await _svc.RegisterAsync(
            "agent", "plain-agent",
            NipSigner.EncodePublicKey(agentKey.PublicKey),
            ["nwp:query"], "{}");

        using var sessKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.IssueSessionAsync(
                groupNid:      "urn:nps:agent:ca.test.example:plain-agent",
                sessionPubKey: NipSigner.EncodePublicKey(sessKey.PublicKey)));
        Assert.Equal(NipErrorCodes.ParentNotGroup, ex.ErrorCode);
    }

    [Fact]
    public void GroupJws_TamperedSignature_FailsVerify()
    {
        // A JWS signed by a foreign key MUST NOT verify against the group's public key.
        using var groupKey   = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        using var foreignKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });

        var jws = MakeFlattenedJws(
            kid:        "urn:nps:agent:ca.test.example:group-x",
            payload:    """{"session_pub_key":"ed25519:abcd","iat":1714672800}""",
            signingKey: foreignKey);

        var ok = NipGroupJws.TryVerify(jws, groupKey.PublicKey, out _, out _, out var err);
        Assert.False(ok);
        Assert.Equal(NipErrorCodes.JwsInvalid, err);
    }

    [Fact]
    public void GroupJws_BadHeaderAlg_FailsVerify()
    {
        using var groupKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var headerJson  = """{"alg":"HS256","kid":"urn:nps:agent:x:group-y","nps-purpose":"session-issue"}""";
        var protectedB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64   = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"iat":1}"""));
        var sig          = SignatureAlgorithm.Ed25519.Sign(
            groupKey, Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64));

        var jws = new NipGroupJws.FlattenedJws
        {
            Protected = protectedB64,
            Payload   = payloadB64,
            Signature = Base64UrlEncode(sig),
        };

        var ok = NipGroupJws.TryVerify(jws, groupKey.PublicKey, out _, out _, out var err);
        Assert.False(ok);
        Assert.Equal(NipErrorCodes.JwsInvalid, err);
    }

    [Fact]
    public void GroupJws_ValidEd25519Signature_Verifies()
    {
        using var groupKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var jws = MakeFlattenedJws(
            kid:        "urn:nps:agent:ca.test.example:group-test",
            payload:    """{"session_pub_key":"ed25519:abcd","iat":1714672800}""",
            signingKey: groupKey);

        var ok = NipGroupJws.TryVerify(jws, groupKey.PublicKey, out var payload, out var kid, out var err);
        Assert.True(ok, $"Expected verify to succeed; err={err}");
        Assert.Null(err);
        Assert.Equal("urn:nps:agent:ca.test.example:group-test", kid);
        Assert.Contains("session_pub_key", payload);
    }

    // ── Scenario 4: lineage metadata is signed and verifies ──────────────────

    [Fact]
    public async Task LineageMetadata_IsSigned_AndVerifies()
    {
        var (group, _) = await IssueGroup(ownerUserId: "user-7f3c", ownerKeyId: "op-kid-1");
        var (sess, _)  = await IssueSession(group.Nid, purpose: "audit-trail-test");

        // Re-canonicalize the issued frame and verify the CA signature
        // against the canonical JSON. Signature MUST verify with lineage
        // present (i.e. lineage was included in the signed payload).
        var caPub = _caKeys.PublicKey;
        Assert.True(VerifyIdentSignature(sess, caPub),
            "Session IdentFrame signature did not verify — lineage was not included in the signed payload.");

        // Tamper with lineage and confirm signature now FAILS
        var tampered = sess with
        {
            Lineage = sess.Lineage! with { ParentNid = "urn:nps:agent:evil.example:group-attacker" }
        };
        Assert.False(VerifyIdentSignature(tampered, caPub),
            "Tampered lineage should invalidate signature; it did not.");
    }

    [Fact]
    public async Task ListSessions_ReturnsOnlySessionsOfGroup()
    {
        var (groupA, _) = await IssueGroup();
        var (groupB, _) = await IssueGroup();
        await IssueSession(groupA.Nid);
        await IssueSession(groupA.Nid);
        await IssueSession(groupB.Nid);

        var aSessions = await _svc.ListSessionsAsync(groupA.Nid);
        var bSessions = await _svc.ListSessionsAsync(groupB.Nid);

        Assert.Equal(2, aSessions.Count);
        Assert.Single(bSessions);
        Assert.All(aSessions, s => Assert.Equal(groupA.Nid, s.ParentNid));
    }

    // ── Scenario 5: group revocation blocks future + cascades to live ────────

    [Fact]
    public async Task RevokingGroup_BlocksFutureSessions()
    {
        var (group, _) = await IssueGroup();
        await _svc.RevokeAsync(group.Nid, "key_compromise");

        using var sessKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var ex = await Assert.ThrowsAsync<NipCaException>(() =>
            _svc.IssueSessionAsync(
                groupNid:      group.Nid,
                sessionPubKey: NipSigner.EncodePublicKey(sessKey.PublicKey)));
        Assert.Equal(NipErrorCodes.GroupRevoked, ex.ErrorCode);
    }

    [Fact]
    public async Task RevokingGroup_CascadesToLiveSessions()
    {
        var (group, _)    = await IssueGroup();
        var (sess1, _)    = await IssueSession(group.Nid);
        var (sess2, _)    = await IssueSession(group.Nid);

        await _svc.RevokeAsync(group.Nid, "key_compromise");

        // Both child sessions are now revoked with reason 'parent_revoked'
        var s1Record = await _store.GetByNidAsync(sess1.Nid);
        var s2Record = await _store.GetByNidAsync(sess2.Nid);
        Assert.NotNull(s1Record!.RevokedAt);
        Assert.Equal("parent_revoked", s1Record.RevokeReason);
        Assert.NotNull(s2Record!.RevokedAt);
        Assert.Equal("parent_revoked", s2Record.RevokeReason);

        // Both sessions appear in CRL alongside the group
        var crl = await _svc.GetCrlAsync();
        Assert.Contains(crl, r => r.Nid == group.Nid);
        Assert.Contains(crl, r => r.Nid == sess1.Nid);
        Assert.Contains(crl, r => r.Nid == sess2.Nid);
    }

    [Fact]
    public async Task VerifySession_AfterGroupRevoked_ReturnsParentRevoked()
    {
        // Even if cascade somehow missed a child, the verify-side chain
        // check (NPS-3 §7 step 3a) must independently reject. We simulate
        // the missed cascade by issuing a session, then revoking the
        // group's record only via a direct store path that does not
        // trigger the cascade — using NipCaService.RevokeAsync after we
        // have already issued the session and then issuing a NEW session
        // is blocked in the previous test; here we instead verify the
        // existing session post-revocation lookup explicitly.
        var (group, _) = await IssueGroup();
        var (sess, _)  = await IssueSession(group.Nid);

        // Revoke the group, which cascades. Then manually undo the cascade
        // on the session record so we can prove the chain check still bites.
        await _svc.RevokeAsync(group.Nid, "key_compromise");
        _store.UndoRevocation(sess.Nid);

        var result = await _svc.VerifyAsync(sess.Nid);
        Assert.False(result.Valid);
        Assert.Equal(NipErrorCodes.ParentRevoked, result.ErrorCode);
    }

    [Fact]
    public async Task VerifySession_HappyPath_Valid()
    {
        var (group, _) = await IssueGroup();
        var (sess, _)  = await IssueSession(group.Nid);
        var result = await _svc.VerifyAsync(sess.Nid);
        Assert.True(result.Valid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(IdentFrame Frame, Key PrivateKey)> IssueGroup(
        IReadOnlyList<string>? capabilities = null,
        string?                ownerUserId  = null,
        string?                ownerKeyId   = null)
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var frame = await _svc.RegisterGroupAsync(
            identifier:   null,
            pubKey:       NipSigner.EncodePublicKey(key.PublicKey),
            capabilities: capabilities ?? ["nwp:query", "nwp:stream"],
            scopeJson:    """{"nodes":["nwp://api.test.example/*"]}""",
            ownerUserId:  ownerUserId,
            ownerKeyId:   ownerKeyId);
        return (frame, key);
    }

    private async Task<(IdentFrame Frame, Key PrivateKey)> IssueSession(
        string                 groupNid,
        TimeSpan?              validity     = null,
        string?                purpose      = null,
        IReadOnlyList<string>? capabilities = null,
        string?                scopeJson    = null)
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var frame = await _svc.IssueSessionAsync(
            groupNid:      groupNid,
            sessionPubKey: NipSigner.EncodePublicKey(key.PublicKey),
            validity:      validity,
            purpose:       purpose,
            capabilities:  capabilities,
            scopeJson:     scopeJson);
        return (frame, key);
    }

    private static NipGroupJws.FlattenedJws MakeFlattenedJws(string kid, string payload, Key signingKey)
    {
        var headerJson   = $$"""{"alg":"EdDSA","kid":"{{kid}}","nps-purpose":"session-issue"}""";
        var protectedB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64   = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var sig          = SignatureAlgorithm.Ed25519.Sign(
            signingKey, Encoding.ASCII.GetBytes(protectedB64 + "." + payloadB64));
        return new NipGroupJws.FlattenedJws
        {
            Protected = protectedB64,
            Payload   = payloadB64,
            Signature = Base64UrlEncode(sig),
        };
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Reproduce the canonical JSON that the CA signed at issuance and
    /// verify the CA Ed25519 signature against it. Used to prove that
    /// lineage participated in the signed payload.
    /// </summary>
    private static bool VerifyIdentSignature(IdentFrame frame, PublicKey caPubKey)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilities"] = frame.Capabilities,
            ["expires_at"]   = frame.ExpiresAt,
            ["frame"]        = frame.Frame,
            ["issued_at"]    = frame.IssuedAt,
            ["issued_by"]    = frame.IssuedBy,
            ["nid"]          = frame.Nid,
            ["pub_key"]      = frame.PubKey,
            ["scope"]        = frame.Scope,
            ["serial"]       = frame.Serial,
        };
        if (frame.AssuranceLevel is not null)
            payload["assurance_level"] = frame.AssuranceLevel.Value;
        if (frame.Lineage is not null)
            payload["lineage"] = frame.Lineage;

        return NipSigner.Verify(caPubKey, payload, frame.Signature);
    }

    // ── In-memory store ──────────────────────────────────────────────────────

    private sealed class InMemoryStore : INipCaStore
    {
        private readonly List<NipCertRecord> _records = new();
        private long _serial;

        public Task SaveAsync(NipCertRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(
                _records.Where(r => r.Nid == nid).MaxBy(r => r.IssuedAt));

        public Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct = default) =>
            Task.FromResult<NipCertRecord?>(_records.FirstOrDefault(r => r.Serial == serial));

        public Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct = default)
        {
            var idx = _records.FindLastIndex(r => r.Nid == nid && !r.RevokedAt.HasValue);
            if (idx < 0) return Task.FromResult(false);
            var r = _records[idx];
            _records[idx] = new NipCertRecord
            {
                Nid          = r.Nid,          EntityType   = r.EntityType,
                Serial       = r.Serial,       PubKey       = r.PubKey,
                Capabilities = r.Capabilities, ScopeJson    = r.ScopeJson,
                IssuedBy     = r.IssuedBy,     IssuedAt     = r.IssuedAt,
                ExpiresAt    = r.ExpiresAt,    RevokedAt    = revokedAt,
                RevokeReason = reason,         MetadataJson = r.MetadataJson,
                NidRole      = r.NidRole,      ParentNid    = r.ParentNid,
                LineageJson  = r.LineageJson,
            };
            return Task.FromResult(true);
        }

        public Task<string> NextSerialAsync(CancellationToken ct = default) =>
            Task.FromResult($"0x{Interlocked.Increment(ref _serial):X}");

        public Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => r.RevokedAt.HasValue).ToList());

        public Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NipCertRecord>>(
                _records.Where(r => r.ParentNid == parentNid).ToList());

        /// <summary>
        /// Test-only helper: reverse a revocation on the latest record for
        /// <paramref name="nid"/>. Used by the chain-check test to simulate a
        /// CA whose cascade missed a session, so we can prove that
        /// verify-time chain check (NPS-3 §7 step 3a) still rejects.
        /// </summary>
        public void UndoRevocation(string nid)
        {
            var idx = _records.FindLastIndex(r => r.Nid == nid);
            if (idx < 0) return;
            var r = _records[idx];
            _records[idx] = new NipCertRecord
            {
                Nid          = r.Nid,          EntityType   = r.EntityType,
                Serial       = r.Serial,       PubKey       = r.PubKey,
                Capabilities = r.Capabilities, ScopeJson    = r.ScopeJson,
                IssuedBy     = r.IssuedBy,     IssuedAt     = r.IssuedAt,
                ExpiresAt    = r.ExpiresAt,    RevokedAt    = null,
                RevokeReason = null,           MetadataJson = r.MetadataJson,
                NidRole      = r.NidRole,      ParentNid    = r.ParentNid,
                LineageJson  = r.LineageJson,
            };
        }
    }
}
