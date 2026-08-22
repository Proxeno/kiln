namespace Kiln.Internal.H264;

/// <summary>
/// Per-MB rate control state machine. Encoder-private — H.264 does not normatively specify rate
/// control (JVT-G012 / reference encoders are informative only).
/// </summary>
/// <remarks>
/// <para>
/// Single-encoder use only — this type is not thread-safe. Constant-QP mode is active whenever
/// the effective per-frame bit budget is 0. The constructor establishes the default budget; each
/// <see cref="StartFrame"/> call passes 0 to retain that default or a non-zero value to override it
/// for the next picture. While the effective budget is 0,
/// <see cref="NextMbQp"/> always returns <c>initialQp</c>.
/// </para>
/// <para>
/// Call sequence: invoke <see cref="StartFrame"/> once at the beginning of each coded picture,
/// then for each macroblock in raster order call <see cref="NextMbQp"/> before entropy coding the MB,
/// then <see cref="Update"/> with the measured bit count for that MB.
/// </para>
/// </remarks>
internal sealed class H264RateControl
{
    // Proportional gain as a rational kP ≈ KpNumerator / (KpDenominator · mbsPerFrame) applied to the
    // integer budget error (see NextMbQp). Tuned for synthetic acceptance tests: tight mb_qp_delta,
    // convergence band, and deterministic integer rounding.
    private const int KpNumerator = 2;

    private const int KpDenominator = 125;

    /// <summary>
    /// Right-shift applied to the per-MB complexity hint when biasing the proportional error
    /// (larger hint → lower QP for complex macroblocks).
    /// </summary>
    private const int ComplexityScaleBits = 9;

    private readonly int _initialQp;
    private readonly int _initialTargetBitsPerFrame;
    private readonly int _mbsPerFrame;

    /// <summary>
    /// Macroblock count used in <see cref="NextMbQp"/> schedule and gain (<see cref="ProportionalDeltaRounded"/>).
    /// Full picture in single-slice mode; per-slice MB count in multi-slice + rate-control mode.
    /// </summary>
    private int _rateScheduleMbs;

    /// <summary>Constant-QP base for the current slice (effective luma QP for first macroblock predictor chain).</summary>
    private int _effectiveBaseQp;

    private int _frameTargetBits;
    private long _cumSpentThisFrame;
    private bool _haveLastQpThisFrame;
    private int _lastQpThisFrame;

    /// <summary>Construct rate control with a per-frame bit budget. Pass 0 for constant-QP behaviour.</summary>
    /// <param name="initialQp">Starting QP in [0, 51].</param>
    /// <param name="targetBitsPerFrame">Per-frame bit budget; 0 means constant QP.</param>
    /// <param name="mbsPerFrame">Macroblock count per coded frame.</param>
    public H264RateControl(int initialQp, int targetBitsPerFrame, int mbsPerFrame)
    {
        _initialQp = initialQp;
        _effectiveBaseQp = Math.Clamp(initialQp, 0, 51);
        _initialTargetBitsPerFrame = targetBitsPerFrame;
        _mbsPerFrame = mbsPerFrame;
        _rateScheduleMbs = mbsPerFrame;
        _frameTargetBits = targetBitsPerFrame;
        _cumSpentThisFrame = 0;
        _haveLastQpThisFrame = false;
        _lastQpThisFrame = _effectiveBaseQp;
    }

    /// <summary>Constructor / picture-wide target bits per frame (0 = constant QP).</summary>
    internal int PictureTargetBits => _initialTargetBitsPerFrame;

    /// <summary>Returns the QP to use for MB <paramref name="mbIndex"/>.</summary>
    /// <param name="mbIndex">0-based MB index in the scheduled window (full picture or slice-local 0..sliceMbs-1).</param>
    /// <param name="complexity">Optional per-MB complexity hint (e.g. residual SAD); 0 for uniform allocation.</param>
    public int NextMbQp(int mbIndex, int complexity)
    {
        if (_frameTargetBits == 0)
        {
            _haveLastQpThisFrame = true;
            _lastQpThisFrame = _effectiveBaseQp;
            return _effectiveBaseQp;
        }

        // Ideal cumulative spend after completing MBs [0, mbIndex - 1], linearly over the schedule window.
        var cumTarget = _frameTargetBits * (long)mbIndex / _rateScheduleMbs;
        var error = _cumSpentThisFrame - cumTarget;

        // Shift error by a bounded, deterministic complexity term so complex MBs reserve effective
        // budget headroom (negative adjustment → lower QP when complexity is large).
        var complexitySkew = complexity == 0 ? 0 : complexity >> ComplexityScaleBits;
        error -= complexitySkew;

        var propDelta = ProportionalDeltaRounded(error);

        var candidate = _effectiveBaseQp + propDelta;
        if (candidate < 0)
        {
            candidate = 0;
        }
        else if (candidate > 51)
        {
            candidate = 51;
        }

        if (_haveLastQpThisFrame)
        {
            var minAllowed = _lastQpThisFrame - 26;
            var maxAllowed = _lastQpThisFrame + 25;
            if (candidate < minAllowed)
            {
                candidate = minAllowed;
            }
            else if (candidate > maxAllowed)
            {
                candidate = maxAllowed;
            }
        }

        if (candidate < 0)
        {
            candidate = 0;
        }
        else if (candidate > 51)
        {
            candidate = 51;
        }

        _haveLastQpThisFrame = true;
        _lastQpThisFrame = candidate;
        return candidate;
    }

    /// <summary>Report the actual bits spent on the most recent MB so the controller can converge.</summary>
    public void Update(int mbIndex, int bitsSpent)
    {
        _ = mbIndex;

        if (_frameTargetBits == 0)
        {
            return;
        }

        _cumSpentThisFrame += bitsSpent;
    }

    /// <summary>Reset frame-level state at the start of every new frame.</summary>
    /// <param name="targetBitsThisFrame">Per-frame bit budget; 0 keeps the constructor value (or slice override below).</param>
    /// <param name="constantSliceLumaQp">
    /// When in [0, 51], use this as the slice/base luma QP for the picture (constant-QP path and proportional base).
    /// When &lt; 0 or &gt; 51, falls back to the constructor&apos;s starting QP.
    /// </param>
    /// <param name="rateScheduleMbs">
    /// When &gt; 0, use this as the MB count for the linear budget schedule and proportional gain (multi-slice slice-local schedule).
    /// When &lt;= 0, use the full-picture <see cref="_mbsPerFrame"/> from the constructor.
    /// </param>
    /// <param name="sliceTargetBits">
    /// When &gt;= 0 and <paramref name="rateScheduleMbs"/> &gt; 0, set the bit budget for this schedule window (slice share of
    /// <see cref="PictureTargetBits"/>). When &lt; 0, picture budget comes from <paramref name="targetBitsThisFrame"/> /
    /// constructor as usual.
    /// </param>
    public void StartFrame(int targetBitsThisFrame, int constantSliceLumaQp = -1, int rateScheduleMbs = -1, int sliceTargetBits = -1)
    {
        if (targetBitsThisFrame != 0)
        {
            _frameTargetBits = targetBitsThisFrame;
        }
        else if (rateScheduleMbs > 0 && sliceTargetBits >= 0)
        {
            _frameTargetBits = sliceTargetBits;
        }
        else
        {
            _frameTargetBits = _initialTargetBitsPerFrame;
        }

        _rateScheduleMbs = rateScheduleMbs > 0 ? rateScheduleMbs : _mbsPerFrame;

        _effectiveBaseQp = constantSliceLumaQp is >= 0 and <= 51
            ? constantSliceLumaQp
            : Math.Clamp(_initialQp, 0, 51);

        _cumSpentThisFrame = 0;
        _haveLastQpThisFrame = false;
        _lastQpThisFrame = _effectiveBaseQp;
    }

    /// <summary>
    /// qpDelta = clip(round(kP · error / mbsPerFrame), -26, 25) with kP = KpNumerator / KpDenominator.
    /// </summary>
    private int ProportionalDeltaRounded(long error)
    {
        if (_rateScheduleMbs == 0)
        {
            return 0;
        }

        var scaled = KpNumerator * error;
        var divisor = (long)KpDenominator * _rateScheduleMbs;
        if (divisor == 0)
        {
            return 0;
        }

        // Banker's-style rounding to nearest integer, ties to even — deterministic on all runtimes.
        var q = scaled / divisor;
        var rem = scaled % divisor;
        var twiceAbsRem = 2L * (rem < 0 ? -rem : rem);
        if (twiceAbsRem > divisor)
        {
            q += scaled < 0 ? -1 : 1;
        }
        else if (twiceAbsRem == divisor)
        {
            if ((q & 1) != 0)
            {
                q += scaled < 0 ? -1 : 1;
            }
        }

        if (q > 25)
        {
            return 25;
        }

        if (q < -26)
        {
            return -26;
        }

        return (int)q;
    }
}
