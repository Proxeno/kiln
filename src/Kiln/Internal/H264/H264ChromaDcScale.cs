namespace Kiln.Internal.H264;

/// <summary>
/// Chroma DC quantisation and reconstruction for 4:2:0 (ChromaArrayType equal to 1).
/// </summary>
/// <remarks>
/// <para>
/// The chroma DC path of ITU-T H.264 (ISO/IEC 14496-10) is: the four 4×4 chroma DC coefficients of an
/// 8×8 chroma component are carried through a 2×2 Hadamard transform (clause 8.5.11.1), and the decoder
/// recovers them as <c>dcC = ((f · LevelScale4x4(qP%6, 0, 0)) &lt;&lt; (qP/6)) &gt;&gt; 5</c>, where
/// <c>f</c> is the inverse 2×2 Hadamard of the parsed levels and <c>qP</c> is the chroma quantisation
/// parameter derived in clause 8.5.8 from QP<sub>Y</sub> and chroma_qp_index_offset.
/// </para>
/// <para>
/// This class folds <c>LevelScale4x4(qP%6, 0, 0) &lt;&lt; (qP/6)</c> into a single multiplier
/// (<see cref="ChromaDcQmul"/>) and carries the shift in this encoder's own convention rather than the
/// spec's: coefficients here live in a 2×-scaled DCT domain (this encoder's forward 4×4 DCT emits
/// <c>coeff[0] = f0 &lt;&lt; 1</c>), and <see cref="ChromaDcDequantIdct"/> applies <c>&gt;&gt; 8</c>
/// instead of the spec's <c>&gt;&gt; 5</c>. The two conventions are documented on the members below and
/// cancel out downstream, so the reconstructed samples are bit-identical to a conforming decoder's.
/// </para>
/// </remarks>
internal static class H264ChromaDcScale
{
    /// <summary>Optional J = D + λ·R for chroma-DC level refinement (<see cref="QuantChromaDcLevelsFromDctDc"/>).</summary>
    /// <param name="dctDc4Target">Target DCT-domain DC values (before WHT).</param>
    /// <param name="qmul"><see cref="ChromaDcQmul"/></param>
    /// <param name="quantizedZ4">Quantized chroma-DC levels (4).</param>
    /// <param name="reconDct4">Workspace: reconstructed DCT DC after dequant+inverse WHT.</param>
    /// <param name="cavlcLevel16">Workspace for CAVLC (≥16 short).</param>
    /// <param name="cavlcRun16">Workspace for CAVLC (≥16 byte).</param>
    /// <param name="lambda">Rate weight.</param>
    /// <param name="cost">J</param>
    /// <param name="distortion">D</param>
    /// <param name="rateBits">R in bits (not normalized).</param>
    internal delegate void ChromaDcRdCostFn(
        ReadOnlySpan<int> dctDc4Target,
        int qmul,
        ReadOnlySpan<short> quantizedZ4,
        Span<int> reconDct4,
        Span<short> cavlcLevel16,
        Span<byte> cavlcRun16,
        double lambda,
        out double cost,
        out double distortion,
        out double rateBits);

    private const int LevelMin = -2048;
    private const int LevelMax = 2047;

    /// <summary>
    /// ITU-T H.264 Table 8-15 (specification of QP<sub>C</sub> as a function of qP<sub>I</sub>),
    /// indexed by qP<sub>I</sub> = Clip3(0, 51, QP<sub>Y</sub> + chroma_qp_index_offset). Identity below
    /// 30, compressed above it. See clause 8.5.8.
    /// </summary>
    private static ReadOnlySpan<byte> ChromaQpFromLumaY =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39,
    ];

    /// <summary>
    /// normAdjust4x4(m, (0, 0)) for m = qP % 6, i.e. the DC-position column of the <c>v</c> matrix in
    /// ITU-T H.264 clause 8.5.9 (derivation process for scaling functions). With a flat scaling list
    /// the DC level scale is <c>LevelScale4x4(m, 0, 0) = weightScale4x4(0, 0) × normAdjust4x4(m, (0, 0))
    /// = 16 × v[m]</c>.
    /// </summary>
    private static ReadOnlySpan<byte> ChromaDcNormAdjustDc =>
    [
        10, 11, 13, 14, 16, 18,
    ];

    public static int ChromaQpFromLuma(int qpY, int chromaQpIndexOffset)
    {
        var i = Math.Clamp(qpY + chromaQpIndexOffset, 0, 51);
        return ChromaQpFromLumaY[i];
    }

    /// <summary>
    /// Chroma-DC dequantisation multiplier: <c>LevelScale4x4(qP%6, 0, 0) &lt;&lt; (qP/6)</c> with a flat
    /// scaling list (weightScale4x4 is 16 at every position; Baseline profile never transmits a scaling
    /// matrix), i.e. <c>16 × normAdjust4x4(qP%6, (0,0)) &lt;&lt; (qP/6)</c> per ITU-T H.264 clause 8.5.9.
    /// </summary>
    /// <remarks>
    /// The extra <c>&lt;&lt; 2</c> is this encoder's bookkeeping, not part of the spec value: the
    /// companion <see cref="ChromaDcDequantIdct"/> finishes with <c>&gt;&gt; 8</c> where clause 8.5.11.1
    /// specifies <c>&gt;&gt; 5</c>, so <c>2^2 / 2^8 = 2^-6</c> — the spec's <c>2^-5</c> with one extra
    /// halving, which the reconstruction helper undoes by doubling the DC before running this encoder's
    /// 2×-scaled inverse DCT.
    /// </remarks>
    public static int ChromaDcQmul(int chromaQp)
    {
        chromaQp = Math.Clamp(chromaQp, 0, 51);
        var shift = (chromaQp / 6) + 2;
        var idx = chromaQp % 6;
        return (ChromaDcNormAdjustDc[idx] * 16) << shift;
    }

    /// <summary>
    /// Encoder-side reconstruction of the four chroma DCT DCs from their quantised levels: the inverse
    /// 2×2 Hadamard of ITU-T H.264 clause 8.5.11.1 (<c>f = [[1,1],[1,-1]] · c · [[1,1],[1,-1]]</c>,
    /// written here as a butterfly) followed by the dequant multiply by <paramref name="qmul"/>.
    /// </summary>
    /// <remarks>
    /// Output is in this encoder's 2×-scaled DCT domain — the same domain as the <c>dc4</c> input to
    /// <see cref="QuantChromaDcLevelsFromDctDc"/> — so the final shift is <c>&gt;&gt; 8</c> rather than
    /// the <c>&gt;&gt; 5</c> that clause 8.5.11.1 states for <c>dcC</c> (see <see cref="ChromaDcQmul"/>,
    /// which contributes <c>&lt;&lt; 2</c> of that difference). The remaining factor of ½ is consumed by
    /// <c>InverseDctMatrixMultiplyEncoderRecon</c>, which doubles the DC before running this encoder's
    /// 2×-scaled inverse DCT; the per-pixel result therefore matches a conforming decoder's
    /// <c>(x + 32) &gt;&gt; 6</c> output exactly.
    /// </remarks>
    public static void ChromaDcDequantIdct(ReadOnlySpan<short> z, int qmul, Span<int> outDctDc4)
    {
        var a = (int)z[0];
        var b = (int)z[1];
        var c = (int)z[2];
        var d = (int)z[3];
        var e = a - b;
        a += b;
        b = c - d;
        c += d;
        var q = (long)qmul;
        outDctDc4[0] = (int)((a + c) * q >> 8);
        outDctDc4[1] = (int)((e + b) * q >> 8);
        outDctDc4[2] = (int)((a - c) * q >> 8);
        outDctDc4[3] = (int)((e - b) * q >> 8);
    }

    /// <summary>Default λ when not set in <see cref="H264BaselineEncoderOptions.ChromaDcRdLambda"/>.</summary>
    public static double DefaultChromaDcRdLambdaFromLumaQp(int qpY)
    {
        qpY = Math.Clamp(qpY, 0, 51);
        return 0.05 * Math.Pow(2.0, (qpY - 28.0) / 6.0);
    }

    /// <summary>Baseline RD: D = SSE in DCT domain; R = chroma-DC CAVLC bits.</summary>
    internal static void DefaultChromaDcRdCost(
        ReadOnlySpan<int> dctDc4Target,
        int qmul,
        ReadOnlySpan<short> quantizedZ4,
        Span<int> reconDct4,
        Span<short> cavlcLevel16,
        Span<byte> cavlcRun16,
        double lambda,
        out double cost,
        out double distortion,
        out double rateBits)
    {
        ChromaDcDequantIdct(quantizedZ4, qmul, reconDct4);
        double d = 0;
        for (var i = 0; i < 4; i++)
        {
            var diff = reconDct4[i] - dctDc4Target[i];
            d += diff * (double)diff;
        }

        rateBits = H264CavlcResidual.CountChromaDcResidualBits(quantizedZ4, cavlcLevel16, cavlcRun16);
        cost = d + lambda * rateBits;
        distortion = d;
    }

    /// <summary>
    /// Quantize chroma-DC levels using λ·R + D coordinate descent (with optional pairwise pass). Same entry as
    /// production when <paramref name="rdLambda"/> matches encoder options.
    /// </summary>
    public static void QuantChromaDcLevelsFromDctDc(
        ReadOnlySpan<int> dctDc4,
        int qmul,
        double rdLambda,
        Span<short> outZ,
        ChromaDcRdCostFn? customCost = null,
        bool skipCoordinateRefinement = false)
    {
        Span<int> w = stackalloc int[4];
        H264BlockTransform.ChromaDcHadamardForward(dctDc4, w);

        // Exact algebraic inverse of the reconstruction path in ChromaDcDequantIdct, so that a level
        // fed straight back through it reproduces dc4 up to rounding: the inverse 2x2 Hadamard there
        // has gain 4 and its result is scaled by qmul >> 8, so the forward direction divides by
        // qmul / 64. This also absorbs this encoder's 2x forward DCT scaling, since dc4 and the
        // reconstruction output share that same 2x-scaled domain.
        var scale = 64.0 / qmul;
        Span<short> q = stackalloc short[4];
        for (var i = 0; i < 4; i++)
        {
            var z = (int)Math.Round(w[i] * scale);
            q[i] = (short)Math.Clamp(z, LevelMin, LevelMax);
        }

        if (!skipCoordinateRefinement)
        {
            RefineChromaDcLevelsCoordinateDescent(
                dctDc4,
                qmul,
                rdLambda,
                q,
                customCost ?? DefaultChromaDcRdCost);
        }

        q.CopyTo(outZ);
    }

    /// <summary>Uses <see cref="DefaultChromaDcRdLambdaFromLumaQp"/> for <paramref name="lumaQp"/>.</summary>
    public static void QuantChromaDcLevelsFromDctDc(
        ReadOnlySpan<int> dctDc4, int qmul, int lumaQp, Span<short> outZ, bool skipCoordinateRefinement = false)
    {
        QuantChromaDcLevelsFromDctDc(dctDc4, qmul, DefaultChromaDcRdLambdaFromLumaQp(lumaQp), outZ, null, skipCoordinateRefinement);
    }

    internal static void RefineChromaDcLevelsCoordinateDescent(
        ReadOnlySpan<int> dctDc4,
        int qmul,
        double rdLambda,
        Span<short> q,
        ChromaDcRdCostFn evaluateRd)
    {
        Span<int> recon = stackalloc int[4];
        Span<short> cavlcLevel = stackalloc short[16];
        Span<byte> cavlcRun = stackalloc byte[16];
        Span<short> cand = stackalloc short[4];

        evaluateRd(
            dctDc4,
            qmul,
            q,
            recon,
            cavlcLevel,
            cavlcRun,
            rdLambda,
            out var bestCost,
            out _,
            out _);

        const int maxPasses = 3;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var changed = false;
            var maxStep = pass == 0 ? 2 : 1;

            for (var i = 0; i < 4; i++)
            {
                var saved = q[i];
                var bestLocalCost = bestCost;
                var bestVal = saved;

                for (var step = -maxStep; step <= maxStep; step++)
                {
                    if (step == 0)
                    {
                        continue;
                    }

                    var v = (int)saved + step;
                    var nv = (short)Math.Clamp(v, LevelMin, LevelMax);
                    if (nv == saved)
                    {
                        continue;
                    }

                    q[i] = nv;
                    evaluateRd(
                        dctDc4,
                        qmul,
                        q,
                        recon,
                        cavlcLevel,
                        cavlcRun,
                        rdLambda,
                        out var c,
                        out _,
                        out _);
                    if (c < bestLocalCost)
                    {
                        bestLocalCost = c;
                        bestVal = nv;
                    }
                }

                q[i] = bestVal;
                if (bestLocalCost < bestCost)
                {
                    bestCost = bestLocalCost;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        ReadOnlySpan<(int a, int b)> pairs =
        [
            (0, 1),
            (0, 2),
            (0, 3),
            (1, 2),
            (1, 3),
            (2, 3),
        ];
        ReadOnlySpan<(int da, int db)> moves =
        [
            (1, 1),
            (1, -1),
            (-1, 1),
            (-1, -1),
        ];

        foreach (var (a, b) in pairs)
        {
            foreach (var (da, db) in moves)
            {
                q.CopyTo(cand);
                var na = (short)Math.Clamp(cand[a] + da, LevelMin, LevelMax);
                var nb = (short)Math.Clamp(cand[b] + db, LevelMin, LevelMax);
                if (na == cand[a] && nb == cand[b])
                {
                    continue;
                }

                cand[a] = na;
                cand[b] = nb;
                evaluateRd(
                    dctDc4,
                    qmul,
                    cand,
                    recon,
                    cavlcLevel,
                    cavlcRun,
                    rdLambda,
                    out var c,
                    out _,
                    out _);
                if (c < bestCost)
                {
                    bestCost = c;
                    cand.CopyTo(q);
                }
            }
        }
    }
}
