namespace Kiln.Internal.H264;

/// <summary>
/// <b>Optional</b> capability an <see cref="IH264KernelSet"/> may additionally implement: score an
/// entire integer-pel candidate grid for one 16×16 luma block in a single call, instead of one
/// <see cref="IH264KernelSet.Sad16x16"/> call per candidate.
///
/// <para>This is deliberately <em>not</em> a member of <see cref="IH264KernelSet"/>. Every member of
/// that interface is a mandatory per-instance kernel with a scalar/SIMD parity test behind it
/// (CONTRIBUTING.md, "Test layout"); a whole-grid search is a different kind of thing — a strategy
/// that some kernel sets can offer and most cannot — so it is expressed as a capability the search
/// probes for (<c>kernels is IH264BatchSadSearch</c>) rather than a contract every ISA kernel set is
/// forced to satisfy. Kernel sets that do not implement it are unaffected, and
/// <see cref="H264MotionEstimator"/>'s exhaustive search behaves exactly as before.</para>
///
/// <para><b>Contract.</b> On <c>true</c>, <paramref name="sadGrid"/> is a span of exactly
/// <c>(2·searchRange + 1)²</c> entries indexed
/// <c>(dy + searchRange) · (2·searchRange + 1) + (dx + searchRange)</c> for
/// <c>dy, dx ∈ [-searchRange, +searchRange]</c>, where entry <c>(dy, dx)</c> is
/// <c>Sad16x16(current, reference-at-(originX + dx, originY + dy))</c> — bit-identical to what
/// <see cref="IH264KernelSet.Sad16x16"/> would return for that candidate. Entries whose 16×16
/// reference window is <b>not</b> wholly inside the reference plane are <b>unspecified</b>: the
/// caller must reject those candidates on its own bounds test before reading them (which
/// <see cref="H264MotionEstimator"/>'s <c>Fits</c> check already does, for every candidate, whether
/// or not this capability is present). The returned span stays valid until the next call to this
/// method on the same thread.</para>
///
/// <para>Returning <c>false</c> is always legal — for an unsupported search range, an unsupported
/// geometry, or because the provider is switched off — and means "use the per-candidate kernel", so
/// a provider can decline any case it cannot serve exactly rather than approximate it.</para>
/// </summary>
internal interface IH264BatchSadSearch
{
    /// <param name="current">The 16×16 current block, at its top-left sample.</param>
    /// <param name="currentStride">Row stride of <paramref name="current"/>.</param>
    /// <param name="reference">The whole reference plane (padded), at sample (0, 0).</param>
    /// <param name="referenceStride">Row stride (and width) of <paramref name="reference"/>.</param>
    /// <param name="referenceHeight">Row count of <paramref name="reference"/>.</param>
    /// <param name="originX">Candidate-grid centre X in <paramref name="reference"/> coordinates.</param>
    /// <param name="originY">Candidate-grid centre Y in <paramref name="reference"/> coordinates.</param>
    /// <param name="searchRange">Half-window in integer pels; the grid is (2·searchRange + 1)².</param>
    /// <param name="sadGrid">On <c>true</c>, the scored grid; see the contract above.</param>
    bool TryScoreSad16x16Grid(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride, int referenceHeight,
        int originX, int originY,
        int searchRange,
        out ReadOnlySpan<int> sadGrid);
}
