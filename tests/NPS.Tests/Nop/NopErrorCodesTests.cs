// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NOP;

namespace NPS.Tests.Nop;

public sealed class NopErrorCodesTests
{
    [Fact]
    public void Portable_profile_error_surface_is_complete()
    {
        string[] codes =
        [
            NopErrorCodes.StreamNak,
            NopErrorCodes.TaskResultExpired,
            NopErrorCodes.StreamNakUnresolvable,
            NopErrorCodes.CallbackInvalid,
            NopErrorCodes.CallbackHmacInvalid,
            NopErrorCodes.ClaimConflict,
            NopErrorCodes.SpawnSpecInvalid,
            NopErrorCodes.RuntimeIdleTimeout,
            NopErrorCodes.RuntimeMaxRuntime,
        ];

        Assert.All(codes, code => Assert.StartsWith("NOP-", code, StringComparison.Ordinal));
    }
}
