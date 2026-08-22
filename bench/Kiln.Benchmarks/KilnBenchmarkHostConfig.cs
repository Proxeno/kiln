using BenchmarkDotNet.Configs;

namespace Kiln.Benchmarks;

/// <summary>Host-level BDN merges: default exporters/loggers plus Kiln extras.</summary>
internal static class KilnBenchmarkHostConfig
{
    public static IConfig Create()
    {
        var cfg = ManualConfig.CreateEmpty();
        cfg.Add(DefaultConfig.Instance);
        return cfg;
    }
}
