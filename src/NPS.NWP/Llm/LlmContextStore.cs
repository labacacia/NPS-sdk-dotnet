// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NPS.NWP.Http;

namespace NPS.NWP.Llm;

/// <summary>Authenticated owner identity used to scope every context lookup.</summary>
public sealed record LlmContextOwner(string Nid, string SecurityScope);

/// <summary>
/// Immutable inputs that determine whether a retained model prefix can be reused.
/// The fingerprint is SDK-internal and is never sent on the wire.
/// </summary>
public sealed record LlmContextBinding
{
    public required string Model { get; init; }
    public required IReadOnlyList<LlmMessageDto> SystemMessages { get; init; }
    public IReadOnlyList<LlmToolDefinitionDto>? Tools { get; init; }
    public required string RuntimeRevision { get; init; }

    internal string Fingerprint()
    {
        var canonical = NPS.NWP.Actions.NwpActionPayloadCodec.EncodeJson(new
        {
            model = Model,
            system_messages = SystemMessages,
            tools = Tools,
            runtime_revision = RuntimeRevision,
        });
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}

/// <summary>Request admitted into the context store before provider dispatch.</summary>
public sealed record LlmContextMutationRequest
{
    public required LlmContextOperation Operation { get; init; }
    public required LlmContextOwner Owner { get; init; }
    public string? ContextId { get; init; }
    public ulong? BaseVersion { get; init; }
    public required LlmContextBinding Binding { get; init; }
    public required IReadOnlyList<LlmMessageDto> Messages { get; init; }
    public uint? TtlSeconds { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestId { get; init; }
}

/// <summary>Opaque reservation returned after atomic admission.</summary>
public sealed record LlmContextMutationReservation
{
    internal string ReservationId { get; init; } = string.Empty;
    internal LlmContextMutationRequest Request { get; init; } = null!;
    internal string BindingFingerprint { get; init; } = string.Empty;
    internal IReadOnlyList<LlmMessageDto> BaseTranscript { get; init; } = [];
    internal uint? EffectiveTtlSeconds { get; init; }
    internal string? ParentContextId { get; init; }
    internal ulong? ParentVersion { get; init; }

    public LlmContextOperation Operation => Request.Operation;
    public string RequestId => Request.RequestId;
}

/// <summary>Read-only committed state exposed to providers after admission.</summary>
public sealed record LlmContextSnapshot(
    string ContextId,
    ulong Version,
    LlmContextState State,
    IReadOnlyList<LlmMessageDto> Transcript,
    LlmContextBinding Binding,
    DateTimeOffset? ExpiresAt);

public sealed class LlmContextStoreException : Exception
{
    public LlmContextStoreException(string errorCode, string message, ulong? currentVersion = null)
        : base(message)
    {
        ErrorCode = errorCode;
        CurrentVersion = currentVersion;
    }

    public string ErrorCode { get; }
    public ulong? CurrentVersion { get; }
}

public sealed record LlmContextStoreOptions
{
    public uint MaxContextsPerPrincipal { get; init; } = 32;
    public uint DefaultTtlSeconds { get; init; } = 3600;
    public uint MaxTtlSeconds { get; init; } = 3600;
    public uint TombstoneSeconds { get; init; } = 86400;
    public TimeSpan IdempotencyTtl { get; init; } = TimeSpan.FromHours(24);
    public ISet<LlmContextOperation> SupportedOperations { get; init; } =
        new HashSet<LlmContextOperation>
        {
            LlmContextOperation.Create,
            LlmContextOperation.Append,
            LlmContextOperation.Fork,
            LlmContextOperation.Reset,
            LlmContextOperation.Release,
        };
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<string> ContextIdFactory { get; init; } = CreateContextId;

    private static string CreateContextId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Truthful NWM-facing limits and capabilities of a context store.</summary>
public sealed record LlmContextStoreDescriptor(
    IReadOnlyList<LlmContextOperation> Operations,
    string Persistence,
    uint MaxContextsPerPrincipal,
    uint MaxTtlSeconds,
    uint TombstoneSeconds);

/// <summary>
/// Process-local reference store for NWP 0.21 context semantics. Provider-private
/// cache handles may be maintained beside this store, keyed by context/version.
/// </summary>
public sealed class InMemoryLlmContextStore
{
    private const string CompleteAction = "llm.complete";
    private const string ReleaseAction = "llm.context.release";

    private static readonly Regex ContextIdPattern =
        new("^[A-Za-z0-9_-]{22,128}$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly LlmContextStoreOptions _options;
    private readonly Dictionary<string, Entry> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyEntry> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LlmContextMutationReservation> _reservations = new(StringComparer.Ordinal);

    public InMemoryLlmContextStore(LlmContextStoreOptions? options = null) =>
        _options = options ?? new LlmContextStoreOptions();

    public LlmContextStoreDescriptor Descriptor => new(
        Enum.GetValues<LlmContextOperation>()
            .Where(_options.SupportedOperations.Contains)
            .ToArray(),
        "process",
        _options.MaxContextsPerPrincipal,
        _options.MaxTtlSeconds,
        _options.TombstoneSeconds);

    public LlmContextMutationReservation Reserve(LlmContextMutationRequest request)
    {
        lock (_gate)
        {
            SweepLocked(_options.Clock());
            ValidateRequest(request);
            EnsureOperationSupported(request.Operation);

            var idemKey = OwnerKey(request.Owner, CompleteAction, request.IdempotencyKey);
            if (_idempotency.TryGetValue(idemKey, out var prior))
            {
                throw new LlmContextStoreException(
                    NwpErrorCodes.ActionIdempotencyConflict,
                    prior.State == IdempotencyState.Busy
                        ? "A request with this idempotency key is already running."
                        : "A completed outcome already exists for this idempotency key.");
            }

            LlmContextMutationReservation reservation;
            if (request.Operation == LlmContextOperation.Create)
            {
                EnsureAllocationAvailable(request.Owner);
                reservation = NewReservation(
                    request,
                    baseTranscript: [],
                    effectiveTtlSeconds: ClampTtl(request.TtlSeconds ?? _options.DefaultTtlSeconds));
            }
            else
            {
                var entry = RequireMutableContext(request.Owner, request.ContextId!);
                if (entry.ReservationId is not null || entry.Version != request.BaseVersion)
                {
                    throw new LlmContextStoreException(
                        NwpErrorCodes.LlmContextVersionConflict,
                        "The context version is stale or another mutation is in progress.",
                        entry.Version);
                }

                if (request.Operation is LlmContextOperation.Append or LlmContextOperation.Fork &&
                    entry.BindingFingerprint != request.Binding.Fingerprint())
                {
                    throw new LlmContextStoreException(
                        NwpErrorCodes.LlmContextBindingMismatch,
                        "The request binding differs from the retained context binding.");
                }

                if (request.Operation == LlmContextOperation.Fork)
                {
                    EnsureAllocationAvailable(request.Owner);
                }

                var now = _options.Clock();
                uint? effectiveTtl = request.TtlSeconds.HasValue
                    ? ClampTtl(request.TtlSeconds.Value)
                    : request.Operation == LlmContextOperation.Fork
                        ? RemainingTtlSeconds(now, entry.ExpiresAt)
                        : entry.TtlSeconds > 0
                            ? entry.TtlSeconds
                            : null;
                reservation = NewReservation(
                    request,
                    entry.Transcript.ToArray(),
                    effectiveTtl,
                    request.Operation == LlmContextOperation.Fork ? entry.ContextId : null,
                    request.Operation == LlmContextOperation.Fork ? entry.Version : null);
                if (request.Operation != LlmContextOperation.Fork)
                {
                    entry.ReservationId = reservation.ReservationId;
                }
            }

            _reservations.Add(reservation.ReservationId, reservation);
            _idempotency.Add(idemKey, new IdempotencyEntry
            {
                Owner = request.Owner,
                State = IdempotencyState.Busy,
                RequestId = request.RequestId,
                ReservationId = reservation.ReservationId,
                RetainUntil = _options.Clock().Add(_options.IdempotencyTtl),
            });
            return reservation;
        }
    }

    public LlmContextReceiptDto Commit(
        LlmContextMutationReservation reservation,
        LlmMessageDto assistantResult)
    {
        lock (_gate)
        {
            var current = RequireReservation(reservation);
            var request = current.Request;
            var now = _options.Clock();
            var committedExpiry = current.EffectiveTtlSeconds.HasValue
                ? now.AddSeconds(current.EffectiveTtlSeconds.Value)
                : (DateTimeOffset?)null;
            Entry entry;
            ulong version;
            string contextId;

            if (request.Operation is LlmContextOperation.Create or LlmContextOperation.Fork)
            {
                contextId = NextUniqueContextId();
                version = 1;
                var transcript = request.Operation == LlmContextOperation.Fork
                    ? current.BaseTranscript.Concat(request.Messages).Append(assistantResult).ToList()
                    : request.Messages.Append(assistantResult).ToList();
                entry = new Entry
                {
                    ContextId = contextId,
                    Owner = request.Owner,
                    Version = version,
                    State = LlmContextState.Active,
                    Binding = request.Binding,
                    BindingFingerprint = current.BindingFingerprint,
                    Transcript = transcript,
                    TtlSeconds = current.EffectiveTtlSeconds ?? 0,
                    ExpiresAt = committedExpiry,
                };
                _contexts.Add(contextId, entry);
                ClearParentReservation(current);
            }
            else
            {
                entry = RequireEntry(request.ContextId!);
                contextId = entry.ContextId;
                version = checked(entry.Version + 1);
                entry.Version = version;
                entry.State = LlmContextState.Active;
                entry.ReservationId = null;
                entry.ExpiresAt = committedExpiry;
                entry.TtlSeconds = current.EffectiveTtlSeconds ?? 0;
                if (request.Operation == LlmContextOperation.Reset)
                {
                    entry.Binding = request.Binding;
                    entry.BindingFingerprint = current.BindingFingerprint;
                    entry.Transcript = request.Messages.Append(assistantResult).ToList();
                }
                else
                {
                    entry.Transcript.AddRange(request.Messages);
                    entry.Transcript.Add(assistantResult);
                }
            }

            var receipt = new LlmContextReceiptDto
            {
                ContextId = contextId,
                Version = version,
                Operation = request.Operation,
                State = LlmContextState.Active,
                ExpiresAt = committedExpiry?.ToString("O"),
                ParentContextId = current.ParentContextId,
                ParentVersion = current.ParentVersion,
            };
            CompleteIdempotency(current, receipt);
            _reservations.Remove(current.ReservationId);
            return receipt;
        }
    }

    public void Abort(LlmContextMutationReservation reservation, string? errorCode = null)
    {
        lock (_gate)
        {
            var current = RequireReservation(reservation);
            ClearParentReservation(current);
            _reservations.Remove(current.ReservationId);
            var key = OwnerKey(
                current.Request.Owner,
                CompleteAction,
                current.Request.IdempotencyKey);
            _idempotency[key] = new IdempotencyEntry
            {
                Owner = current.Request.Owner,
                State = IdempotencyState.Failed,
                RequestId = current.Request.RequestId,
                ErrorCode = errorCode,
                RetainUntil = _options.Clock().Add(_options.IdempotencyTtl),
            };
            SweepLocked(_options.Clock());
        }
    }

    public LlmContextReceiptDto Release(
        LlmContextOwner owner,
        string contextId,
        ulong baseVersion,
        string idempotencyKey)
    {
        lock (_gate)
        {
            SweepLocked(_options.Clock());
            EnsureOperationSupported(LlmContextOperation.Release);
            ValidateContextId(contextId);
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw ParamsInvalid("release requires idempotency_key.");
            }
            var idemKey = OwnerKey(owner, ReleaseAction, idempotencyKey);
            if (_idempotency.TryGetValue(idemKey, out var replay))
            {
                if (replay.State == IdempotencyState.Completed &&
                    replay.Receipt is not null &&
                    replay.ContextId == contextId &&
                    replay.BaseVersion == baseVersion)
                {
                    return replay.Receipt;
                }
                throw new LlmContextStoreException(
                    NwpErrorCodes.ActionIdempotencyConflict,
                    "A release with this idempotency key already exists.");
            }

            var entry = RequireMutableContext(owner, contextId);
            if (entry.ReservationId is not null || entry.Version != baseVersion)
            {
                throw new LlmContextStoreException(
                    NwpErrorCodes.LlmContextVersionConflict,
                    "The context version is stale or another mutation is in progress.",
                    entry.Version);
            }

            entry.Version = checked(entry.Version + 1);
            entry.State = LlmContextState.Released;
            entry.ExpiresAt = null;
            entry.TombstoneUntil = _options.Clock().AddSeconds(_options.TombstoneSeconds);
            var receipt = new LlmContextReceiptDto
            {
                ContextId = entry.ContextId,
                Version = entry.Version,
                Operation = LlmContextOperation.Release,
                State = LlmContextState.Released,
            };
            _idempotency[idemKey] = new IdempotencyEntry
            {
                Owner = owner,
                State = IdempotencyState.Completed,
                Receipt = receipt,
                ContextId = contextId,
                BaseVersion = baseVersion,
                RetainUntil = _options.Clock().Add(_options.IdempotencyTtl),
            };
            return receipt;
        }
    }

    public LlmContextStatusDto Status(
        LlmContextOwner owner,
        string? contextId = null,
        string? idempotencyKey = null)
    {
        lock (_gate)
        {
            SweepLocked(_options.Clock());
            if ((contextId is null) == (idempotencyKey is null))
            {
                throw new LlmContextStoreException(
                    NwpErrorCodes.ActionParamsInvalid,
                    "Status requires exactly one of context_id or idempotency_key.");
            }

            if (idempotencyKey is not null)
            {
                if (!_idempotency.TryGetValue(
                        OwnerKey(owner, CompleteAction, idempotencyKey), out var outcome))
                {
                    throw NotFound();
                }
                return outcome.State switch
                {
                    IdempotencyState.Busy => new LlmContextStatusDto
                    {
                        State = LlmContextState.Busy,
                        RequestId = outcome.RequestId,
                    },
                    IdempotencyState.Failed => new LlmContextStatusDto
                    {
                        State = LlmContextState.Failed,
                        RequestId = outcome.RequestId,
                        ErrorCode = outcome.ErrorCode,
                    },
                    _ => StatusFromReceipt(owner, outcome.Receipt!),
                };
            }

            ValidateContextId(contextId!);
            if (!_contexts.TryGetValue(contextId!, out var entry))
            {
                throw NotFound();
            }
            EnsureOwner(entry, owner);
            return new LlmContextStatusDto
            {
                State = entry.ReservationId is null ? entry.State : LlmContextState.Busy,
                ContextId = entry.ContextId,
                Version = entry.Version,
                ExpiresAt = entry.ExpiresAt?.ToString("O"),
                RequestId = entry.ReservationId is not null &&
                    _reservations.TryGetValue(entry.ReservationId, out var reservation)
                        ? reservation.RequestId
                        : null,
            };
        }
    }

    public LlmContextSnapshot Snapshot(LlmContextOwner owner, string contextId)
    {
        lock (_gate)
        {
            SweepLocked(_options.Clock());
            var entry = RequireMutableContext(owner, contextId);
            return new LlmContextSnapshot(
                entry.ContextId,
                entry.Version,
                entry.State,
                entry.Transcript.ToArray(),
                entry.Binding,
                entry.ExpiresAt);
        }
    }

    public int SweepExpired()
    {
        lock (_gate)
        {
            return SweepLocked(_options.Clock());
        }
    }

    private LlmContextStatusDto StatusFromReceipt(
        LlmContextOwner owner,
        LlmContextReceiptDto receipt)
    {
        if (_contexts.ContainsKey(receipt.ContextId))
        {
            return Status(owner, contextId: receipt.ContextId);
        }
        return new LlmContextStatusDto
        {
            State = receipt.State,
            ContextId = receipt.ContextId,
            Version = receipt.Version,
            ExpiresAt = receipt.ExpiresAt,
        };
    }

    private LlmContextMutationReservation NewReservation(
        LlmContextMutationRequest request,
        IReadOnlyList<LlmMessageDto> baseTranscript,
        uint? effectiveTtlSeconds,
        string? parentContextId = null,
        ulong? parentVersion = null) => new()
        {
            ReservationId = Guid.NewGuid().ToString("N"),
            Request = request,
            BindingFingerprint = request.Binding.Fingerprint(),
            BaseTranscript = baseTranscript,
            EffectiveTtlSeconds = effectiveTtlSeconds,
            ParentContextId = parentContextId,
            ParentVersion = parentVersion,
        };

    private void ValidateRequest(LlmContextMutationRequest request)
    {
        if (request.Operation == LlmContextOperation.Release)
        {
            throw ParamsInvalid("release uses the llm.context.release lifecycle action.");
        }
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw ParamsInvalid("A stateful request requires idempotency_key.");
        }
        if (request.TtlSeconds == 0)
        {
            throw ParamsInvalid("ttl_seconds must be greater than zero.");
        }
        if (request.Operation == LlmContextOperation.Create)
        {
            if (request.ContextId is not null || request.BaseVersion is not null)
            {
                throw ParamsInvalid("create forbids context_id and base_version.");
            }
        }
        else
        {
            if (request.ContextId is null || request.BaseVersion is null)
            {
                throw ParamsInvalid("append/fork/reset require context_id and base_version.");
            }
            ValidateContextId(request.ContextId);
        }
        if (request.Operation != LlmContextOperation.Fork && request.Messages.Count == 0)
        {
            throw ParamsInvalid("Only fork may carry an empty message delta.");
        }
        if (request.Operation is LlmContextOperation.Append or LlmContextOperation.Fork &&
            request.Messages.Any(item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase)))
        {
            throw new LlmContextStoreException(
                NwpErrorCodes.LlmContextBindingMismatch,
                "append/fork deltas must not contain system messages.");
        }
    }

    private void EnsureOperationSupported(LlmContextOperation operation)
    {
        if (!_options.SupportedOperations.Contains(operation))
        {
            throw new LlmContextStoreException(
                NwpErrorCodes.LlmContextOperationUnsupported,
                $"Context operation '{operation}' is not advertised.");
        }
    }

    private void EnsureAllocationAvailable(LlmContextOwner owner)
    {
        var live = _contexts.Values.Count(entry =>
            entry.Owner == owner && entry.State == LlmContextState.Active);
        var pendingAllocations = _reservations.Values.Count(item =>
            item.Request.Owner == owner &&
            item.Request.Operation is LlmContextOperation.Create or LlmContextOperation.Fork);
        if (live + pendingAllocations >= _options.MaxContextsPerPrincipal)
        {
            throw new LlmContextStoreException(
                NwpErrorCodes.LlmContextLimitExceeded,
                "The principal's live context limit has been reached.");
        }
    }

    private Entry RequireMutableContext(LlmContextOwner owner, string contextId)
    {
        var entry = RequireEntry(contextId);
        EnsureOwner(entry, owner);
        if (entry.State == LlmContextState.Expired)
        {
            throw new LlmContextStoreException(
                NwpErrorCodes.LlmContextExpired,
                "The context expired.", entry.Version);
        }
        if (entry.State == LlmContextState.Released)
        {
            throw NotFound();
        }
        return entry;
    }

    private Entry RequireEntry(string contextId) =>
        _contexts.TryGetValue(contextId, out var entry) ? entry : throw NotFound();

    private static void EnsureOwner(Entry entry, LlmContextOwner owner)
    {
        if (entry.Owner != owner)
        {
            throw new LlmContextStoreException(
                NwpErrorCodes.LlmContextForbidden,
                "The caller does not own this context.");
        }
    }

    private LlmContextMutationReservation RequireReservation(LlmContextMutationReservation value) =>
        _reservations.TryGetValue(value.ReservationId, out var current) && ReferenceEquals(value, current)
            ? current
            : throw new InvalidOperationException("The context reservation is not active.");

    private void ClearParentReservation(LlmContextMutationReservation reservation)
    {
        if (reservation.Request.ContextId is not null &&
            _contexts.TryGetValue(reservation.Request.ContextId, out var parent) &&
            parent.ReservationId == reservation.ReservationId)
        {
            parent.ReservationId = null;
        }
    }

    private void CompleteIdempotency(
        LlmContextMutationReservation reservation,
        LlmContextReceiptDto receipt)
    {
        _idempotency[OwnerKey(
            reservation.Request.Owner,
            CompleteAction,
            reservation.Request.IdempotencyKey)] =
            new IdempotencyEntry
            {
                Owner = reservation.Request.Owner,
                State = IdempotencyState.Completed,
                RequestId = reservation.Request.RequestId,
                Receipt = receipt,
                RetainUntil = _options.Clock().Add(_options.IdempotencyTtl),
            };
    }

    private string NextUniqueContextId()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = _options.ContextIdFactory();
            ValidateContextId(id);
            if (!_contexts.ContainsKey(id)) return id;
        }
        throw new InvalidOperationException("Context ID factory repeatedly produced collisions.");
    }

    private int SweepLocked(DateTimeOffset now)
    {
        var changed = 0;
        foreach (var entry in _contexts.Values)
        {
            if (entry.State == LlmContextState.Active && entry.ReservationId is null &&
                entry.ExpiresAt is not null && entry.ExpiresAt <= now)
            {
                entry.State = LlmContextState.Expired;
                entry.ExpiresAt = null;
                entry.TombstoneUntil = now.AddSeconds(_options.TombstoneSeconds);
                changed++;
            }
        }
        foreach (var key in _contexts
            .Where(pair => (pair.Value.State is LlmContextState.Expired or LlmContextState.Released) &&
                           pair.Value.TombstoneUntil <= now)
            .Select(pair => pair.Key).ToArray())
        {
            _contexts.Remove(key);
            changed++;
        }
        foreach (var key in _idempotency
            .Where(pair => pair.Value.State != IdempotencyState.Busy &&
                           pair.Value.RetainUntil <= now)
            .Select(pair => pair.Key).ToArray())
        {
            _idempotency.Remove(key);
            changed++;
        }
        return changed;
    }

    private uint ClampTtl(uint seconds) => Math.Min(seconds, _options.MaxTtlSeconds);

    private static uint? RemainingTtlSeconds(DateTimeOffset now, DateTimeOffset? expiry) =>
        expiry is null
            ? null
            : checked((uint)Math.Max(1, Math.Ceiling((expiry.Value - now).TotalSeconds)));

    private static string OwnerKey(LlmContextOwner owner, string action, string key) =>
        $"{owner.Nid}\u001f{owner.SecurityScope}\u001f{action}\u001f{key}";

    private static void ValidateContextId(string value)
    {
        if (!ContextIdPattern.IsMatch(value))
        {
            throw ParamsInvalid("context_id must be a 22-128 character unpadded base64url locator.");
        }
    }

    private static LlmContextStoreException ParamsInvalid(string message) =>
        new(NwpErrorCodes.ActionParamsInvalid, message);

    private static LlmContextStoreException NotFound() =>
        new(NwpErrorCodes.LlmContextNotFound, "The context or retained outcome was not found.");

    private sealed class Entry
    {
        public required string ContextId { get; init; }
        public required LlmContextOwner Owner { get; init; }
        public required ulong Version { get; set; }
        public required LlmContextState State { get; set; }
        public required LlmContextBinding Binding { get; set; }
        public required string BindingFingerprint { get; set; }
        public required List<LlmMessageDto> Transcript { get; set; }
        public required uint TtlSeconds { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? TombstoneUntil { get; set; }
        public string? ReservationId { get; set; }
    }

    private enum IdempotencyState { Busy, Completed, Failed }

    private sealed class IdempotencyEntry
    {
        public required LlmContextOwner Owner { get; init; }
        public required IdempotencyState State { get; init; }
        public string? RequestId { get; init; }
        public string? ReservationId { get; init; }
        public string? ErrorCode { get; init; }
        public LlmContextReceiptDto? Receipt { get; init; }
        public string? ContextId { get; init; }
        public ulong? BaseVersion { get; init; }
        public required DateTimeOffset RetainUntil { get; init; }
    }
}
