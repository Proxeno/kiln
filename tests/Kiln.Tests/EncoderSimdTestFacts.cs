using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using FluentAssertions;

namespace Kiln.Tests;

/// <summary>
/// Fail-loud guards for encoder SIMD tests on CI x64 runners (SSSE3 + SSE4.1 + hardware Vector128).
/// </summary>
internal static class EncoderSimdTestFacts
{
    /// <summary>
    /// Asserts baseline x64 encoder SIMD is available. On CI, missing ISA support fails the test
    /// instead of silently skipping. On local non-x64 hosts, returns without asserting.
    /// </summary>
    internal static void RequiresX64EncoderSimd()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var onCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        if (!onCi && (!Ssse3.IsSupported || !Sse41.IsSupported || !Vector128.IsHardwareAccelerated))
        {
            return;
        }

        Ssse3.IsSupported.Should().BeTrue("x64 CI/dev host must expose SSSE3 for encoder SIMD tests");
        Sse41.IsSupported.Should().BeTrue("x64 CI/dev host must expose SSE4.1 for encoder SIMD tests");
        Vector128.IsHardwareAccelerated.Should().BeTrue("x64 CI/dev host must hardware-accelerate Vector128");
    }

    /// <summary>
    /// Asserts AVX2 is available for motion-SAD AVX2 parity tests. On CI x64, missing AVX2 fails the test
    /// instead of silently skipping. On local non-x64 hosts, returns without asserting.
    /// </summary>
    internal static void RequiresX64Avx2MotionSad()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var onCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        if (!onCi && !Avx2.IsSupported)
        {
            return;
        }

        Avx2.IsSupported.Should().BeTrue("x64 CI host must expose AVX2 for motion SAD AVX2 parity tests");
    }
}
