namespace Kiln.Recovery;

/// <summary>
/// The recovery policy's decision for one frame: which recovery action, if any, the encoder should
/// take in response to client feedback (PLI or FIR) or a scene change, plus running totals.
/// </summary>
/// <param name="ForceIdr">True when the encoder should emit an IDR.</param>
/// <param name="EnableIntraRefresh">True when recovery was requested during the IDR cooldown, so
/// gradual intra refresh should be used instead of an IDR.</param>
/// <param name="RecoveryReason">Short reason code for the decision, or an empty string when no action is taken.</param>
/// <param name="IdrCount">Running total of IDRs this policy has forced.</param>
/// <param name="PliCount">Running total of PLI (picture loss indication) signals handled.</param>
/// <param name="FirCount">Running total of FIR (full intra request) signals handled.</param>
public sealed record RecoveryDecision(
    bool ForceIdr,
    bool EnableIntraRefresh,
    string RecoveryReason,
    int IdrCount,
    int PliCount,
    int FirCount);
