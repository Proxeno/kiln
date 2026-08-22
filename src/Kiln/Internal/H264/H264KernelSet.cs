using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

internal static class H264KernelSet
{
    public static IH264KernelSet CreateBest()
    {
        if (Avx2.IsSupported) return new Avx2KernelSet();
        if (Ssse3.IsSupported) return new Ssse3KernelSet();
        if (AdvSimd.Arm64.IsSupported) return new Neon64KernelSet();
        if (AdvSimd.IsSupported) return new NeonKernelSet();
        return new ScalarKernelSet();
    }
}
