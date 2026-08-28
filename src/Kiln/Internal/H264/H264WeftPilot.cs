namespace Kiln.Internal.H264;

/// <summary>
/// The single install point for the experimental Weft-backed batch-SAD provider
/// (<see cref="IH264BatchSadSearch"/>).
///
/// <para><b>This file carries no dependency on Weft and is always compiled.</b> The implementation
/// half lives in <c>H264WeftPilot.Weft.cs</c>, which is wholly inside <c>#if KILN_WEFT_PILOT</c> —
/// a symbol only the pilot build defines (<c>dotnet build -p:KilnWeftPilot=true</c>, which is also
/// what adds the Weft <c>ProjectReference</c>). In a normal build the partial method has no
/// implementation, the compiler removes the call entirely, and
/// <see cref="Decorate(IH264KernelSet)"/> is the identity: kiln's default build story — no Weft
/// checkout, no extra reference, no behaviour change — is untouched.</para>
///
/// <para>Even in a pilot build the provider is <b>off unless</b> the environment variable
/// <c>KILN_WEFT_BATCH_SAD</c> is set to <c>1</c>/<c>true</c>, following the same env-var gate kiln
/// already uses for its opt-in diagnostics subsystems.</para>
/// </summary>
internal static partial class H264WeftPilot
{
    /// <summary>Implemented only under <c>KILN_WEFT_PILOT</c>; a no-op otherwise.</summary>
    static partial void Install(ref IH264KernelSet kernels);

    /// <summary>
    /// Returns <paramref name="kernels"/>, optionally wrapped so it additionally implements
    /// <see cref="IH264BatchSadSearch"/>. Called wherever an encoder's kernel set is created.
    /// </summary>
    internal static IH264KernelSet Decorate(IH264KernelSet kernels)
    {
        Install(ref kernels);
        return kernels;
    }
}
