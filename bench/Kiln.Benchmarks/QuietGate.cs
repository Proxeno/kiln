using System.Runtime.InteropServices;

namespace Kiln.Benchmarks;

/// <summary>
/// The same <c>getloadavg(3)</c> ps-gate as <c>PP0Tool/QuietGate.cs</c> (itself reproduced from
/// <c>KilnPilotTool/Packet2.cs</c>) — reproduced again here rather than referenced, since this
/// tool deliberately has no dependency on either sibling tool. "Wait for quiet windows, poll and
/// retry; label coarse if never quiet" is this session's standing hygiene instruction.
/// </summary>
internal static partial class QuietGate
{
    public enum Outcome
    {
        Immediate,
        Waited,
        RanHot,
    }

    public sealed record Result(double LoadBefore, double WaitSeconds, Outcome Outcome, double Threshold);

    [LibraryImport("libc", EntryPoint = "getloadavg")]
    private static unsafe partial int GetLoadAverage(double* loadavg, int nelem);

    /// <summary>The 1-minute load average, or NaN if it could not be read.</summary>
    public static unsafe double OneMinuteLoad()
    {
        var buf = stackalloc double[3];
        return GetLoadAverage(buf, 3) >= 1 ? buf[0] : double.NaN;
    }

    public static Result WaitForQuiet(double threshold = 2.5, double maxWaitSeconds = 30.0)
    {
        var load = OneMinuteLoad();
        if (double.IsNaN(load) || load <= threshold)
        {
            return new Result(load, 0.0, Outcome.Immediate, threshold);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < maxWaitSeconds)
        {
            Thread.Sleep(1000);
            load = OneMinuteLoad();
            if (load <= threshold)
            {
                return new Result(load, sw.Elapsed.TotalSeconds, Outcome.Waited, threshold);
            }
        }

        return new Result(load, sw.Elapsed.TotalSeconds, Outcome.RanHot, threshold);
    }
}
