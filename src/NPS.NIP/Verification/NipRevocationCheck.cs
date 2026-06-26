// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Frames;

namespace NPS.NIP.Verification;

/// <summary>
/// Live revocation callback for <see cref="NipIdentVerifier"/>.
/// Return a failing result to reject the identity, or <c>null</c> / OK to continue
/// to the next configured revocation source.
/// </summary>
public delegate ValueTask<NipIdentVerifyResult?> NipRevocationCheck(
    IdentFrame frame,
    CancellationToken cancellationToken);
