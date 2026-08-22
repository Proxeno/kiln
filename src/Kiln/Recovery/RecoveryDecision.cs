namespace Kiln.Recovery;

/// <summary>
/// Output of the recovery policy decision logic.
/// Specifies what recovery action (if any) the encoder should take
/// in response to client feedback (PLI/FIR).
/// </summary>
public sealed record RecoveryDecision(
    /// <summary>Should the encoder emit an IDR (I-frame) for recovery?</summary>
    bool ForceIdr,

    /// <summary>Should the encoder use intra refresh for gradual recovery?</summary>
    bool EnableIntraRefresh,

    /// <summary>Human-readable reason for the recovery decision.</summary>
    string RecoveryReason,

    /// <summary>Total number of IDR frames forced due to recovery feedback.</summary>
    int IdrCount,

    /// <summary>Total number of PLI (Picture Loss Indication) signals received.</summary>
    int PliCount,

    /// <summary>Total number of FIR (Full Intra Request) signals received.</summary>
    int FirCount
);
