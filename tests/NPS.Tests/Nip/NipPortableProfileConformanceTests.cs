// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using NSec.Cryptography;
using NPS.NIP.Client;
using NPS.NIP.Crypto;
using NPS.NIP.Verification;

namespace NPS.Tests.Nip;

public sealed class NipPortableProfileConformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void SharedRevocationPolicyVectors_Pass()
    {
        var suite = ReadSuite<RevocationCase>(
            "spec/conformance/nip/revocation_policy_vectors.json");
        Assert.Equal(11, suite.Vectors.Count);

        foreach (var vector in suite.Vectors)
        {
            var evaluation = new NipRevocationEvaluation(
                ParseMode(vector.Input.RevocationMode),
                vector.Input.OcspFailOpen);
            NipIdentVerifyResult? result = null;
            foreach (var source in vector.Input.Sources)
            {
                result = evaluation.Observe(
                    ParseSource(source.Source),
                    ParseOutcome(source.Outcome));
                if (result is not null) break;
            }

            result ??= evaluation.Complete();
            Assert.Equal(vector.Expected.Valid, result.IsValid);
            Assert.Equal(vector.Expected.FailedStep ?? 0, result.FailedStep);
            Assert.Equal(vector.Expected.Error, result.ErrorCode);
            Assert.Equal(
                vector.Expected.ConsultedSources,
                evaluation.ConsultedSources.Select(FormatSource).ToArray());
        }
    }

    [Fact]
    public void SharedSignedCrlVectors_Pass()
    {
        var suite = ReadSuite<SignedCrlCase>(
            "spec/conformance/nip/signed_crl_vectors.json");
        Assert.Equal(2, suite.Vectors.Count);

        foreach (var vector in suite.Vectors)
        {
            var canonical = NipSigner.CanonicalJson(vector.Input.Body);
            if (vector.Expected.CanonicalForSigning is not null)
                Assert.Equal(vector.Expected.CanonicalForSigning, canonical);

            var publicKey = NipSigner.DecodePublicKey(vector.Input.PublicKey);
            Assert.NotNull(publicKey);
            Assert.Equal(
                vector.Expected.SignatureValid,
                NipSigner.Verify(
                    publicKey,
                    vector.Input.Body,
                    vector.Input.Signature));

            var body = vector.Input.Body;
            var crl = new NipCaCrl
            {
                IssuedBy = body.GetProperty("issued_by").GetString()!,
                IssuedAt = body.GetProperty("issued_at").GetString()!,
                Entries = JsonSerializer.Deserialize<IReadOnlyList<NipCaCrlEntry>>(
                    body.GetProperty("entries").GetRawText(),
                    JsonOptions)!,
                Signature = vector.Input.Signature,
            };
            Assert.Equal(
                vector.Expected.SignatureValid,
                NipCaClient.VerifyCrlSignature(crl, vector.Input.PublicKey));

            if (vector.Input.PrivateSeedHex is not null)
            {
                using var privateKey = Key.Import(
                    SignatureAlgorithm.Ed25519,
                    Convert.FromHexString(vector.Input.PrivateSeedHex),
                    KeyBlobFormat.RawPrivateKey,
                    new KeyCreationParameters
                    {
                        ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
                    });
                Assert.Equal(
                    vector.Input.Signature,
                    NipSigner.Sign(privateKey, vector.Input.Body));
            }
        }
    }

    [Fact]
    public void CrlVerification_RejectsMalformedSignatureWithoutThrowing()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
            });
        var crl = new NipCaCrl
        {
            IssuedBy = "urn:nps:org:ca.example",
            IssuedAt = "2026-07-29T00:00:00Z",
            Entries = [],
            Signature = "ed25519:not-base64!",
        };

        Assert.False(NipCaClient.VerifyCrlSignature(
            crl,
            NipSigner.EncodePublicKey(key.PublicKey)));
    }

    private static VectorSuite<T> ReadSuite<T>(string relative)
    {
        var path = FindRepoFile(relative);
        return JsonSerializer.Deserialize<VectorSuite<T>>(
                   File.ReadAllText(path),
                   JsonOptions)
               ?? throw new InvalidOperationException($"Could not parse '{relative}'.");
    }

    private static NipRevocationMode ParseMode(string value) => value switch
    {
        "if_configured" => NipRevocationMode.IfConfigured,
        "required" => NipRevocationMode.Required,
        _ => throw new InvalidOperationException($"Unknown revocation mode '{value}'."),
    };

    private static NipRevocationSource ParseSource(string value) => value switch
    {
        "local_crl" => NipRevocationSource.LocalCrl,
        "callback" => NipRevocationSource.Callback,
        "ca_store" => NipRevocationSource.CaStore,
        "ocsp" => NipRevocationSource.Ocsp,
        _ => throw new InvalidOperationException($"Unknown revocation source '{value}'."),
    };

    private static string FormatSource(NipRevocationSource value) => value switch
    {
        NipRevocationSource.LocalCrl => "local_crl",
        NipRevocationSource.Callback => "callback",
        NipRevocationSource.CaStore => "ca_store",
        NipRevocationSource.Ocsp => "ocsp",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    private static NipRevocationOutcome ParseOutcome(string value) => value switch
    {
        "good" => NipRevocationOutcome.Good,
        "revoked" => NipRevocationOutcome.Revoked,
        "unavailable" => NipRevocationOutcome.Unavailable,
        _ => throw new InvalidOperationException($"Unknown revocation outcome '{value}'."),
    };

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relative}'.");
    }

    private sealed record VectorSuite<T>(
        string Name,
        string Version,
        IReadOnlyList<T> Vectors);

    private sealed record RevocationCase(
        string Id,
        RevocationInput Input,
        RevocationExpected Expected);

    private sealed record RevocationInput(
        string RevocationMode,
        bool OcspFailOpen,
        IReadOnlyList<SourceInput> Sources);

    private sealed record SourceInput(string Source, string Outcome);

    private sealed record RevocationExpected
    {
        public bool Valid { get; init; }
        public int? FailedStep { get; init; }
        public string? Error { get; init; }
        public required IReadOnlyList<string> ConsultedSources { get; init; }
    }

    private sealed record SignedCrlCase(
        string Id,
        SignedCrlInput Input,
        SignedCrlExpected Expected);

    private sealed record SignedCrlInput
    {
        public string? PrivateSeedHex { get; init; }
        public required string PublicKey { get; init; }
        public JsonElement Body { get; init; }
        public required string Signature { get; init; }
    }

    private sealed record SignedCrlExpected
    {
        public string? CanonicalForSigning { get; init; }
        public bool SignatureValid { get; init; }
    }
}
