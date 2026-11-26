using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Focused tests for the shared slice-dispatch scheduler:
/// <list type="bullet">
/// <item>an exception thrown by a slice task faults that task with the original exception (which
/// <c>Parallel.For</c> then aggregates for the caller) and the pool keeps working afterwards;</item>
/// <item>the worker count is never exceeded, even when more tasks than workers are queued at
/// once;</item>
/// <item>many frames encoded back to back stay byte-identical, which exercises the worker wakeup
/// path repeatedly and would hang or diverge on a lost wakeup.</item>
/// </list>
/// </summary>
public sealed class H264SliceSchedulerTests
{
    private static H264BaselineEncoder.LimitedConcurrencyLevelTaskScheduler NewScheduler(int workers) =>
        new(workers);

    [Fact]
    public async Task Slice_task_exception_is_captured_and_pool_survives()
    {
        var scheduler = NewScheduler(2);
        var factory = new TaskFactory(scheduler);

        var failed = factory.StartNew(() => throw new InvalidOperationException("slice boom"));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        thrown.Message.Should().Be("slice boom");

        // The same pool must still dispatch and run work after a task has faulted.
        var recovered = await factory.StartNew(() => 7);
        recovered.Should().Be(7);
    }

    [Fact]
    public async Task Scheduler_never_exceeds_its_worker_count()
    {
        const int workers = 3;
        const int tasks = 12;
        var scheduler = NewScheduler(workers);
        scheduler.MaximumConcurrencyLevel.Should().Be(workers);

        var factory = new TaskFactory(scheduler);
        var concurrent = 0;
        var maxObserved = 0;
        var completed = 0;

        var all = new Task[tasks];
        for (var i = 0; i < tasks; i++)
        {
            all[i] = factory.StartNew(() =>
            {
                var running = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxObserved, running);
                Thread.Sleep(2);
                Interlocked.Decrement(ref concurrent);
                Interlocked.Increment(ref completed);
            });
        }

        await Task.WhenAll(all);

        Volatile.Read(ref completed).Should().Be(tasks);
        Volatile.Read(ref maxObserved).Should().BeLessThanOrEqualTo(workers,
            "the pool has exactly {0} workers and must not oversubscribe", workers);
    }

    [Fact]
    public void Many_frames_back_to_back_are_byte_identical()
    {
        const int w = 320;
        const int h = 240;
        const int frames = 120;
        const int sliceCount = 8;
        var ySize = w * h;
        var uvSize = ySize / 4;

        var first = EncodeSequence(w, h, frames, sliceCount);
        var second = EncodeSequence(w, h, frames, sliceCount);

        second.Length.Should().Be(first.Length);
        second.AsSpan().SequenceEqual(first)
            .Should().BeTrue("deterministic encoding must produce identical bytes across runs and scheduler reuse");
    }

    private static byte[] EncodeSequence(int w, int h, int frames, int sliceCount)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 60,
            SliceCount = sliceCount,
        });

        var output = new MemoryStream();
        var frame = new byte[ySize + 2 * uvSize];
        var annex = new byte[ySize * 4 + 262_144];
        for (var f = 0; f < frames; f++)
        {
            // Deterministic moving gradient: a horizontal pan plus a bright 32x32 block.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    frame[y * w + x] = (byte)((x + 3 * f + y) % 200 + 24);
                }
            }

            Array.Fill(frame, (byte)128, ySize, 2 * uvSize);
            var bx = (f * 4) % (w - 32);
            var by = h / 2 - 16;
            for (var yy = 0; yy < 32; yy++)
            {
                Array.Fill(frame, (byte)235, (by + yy) * w + bx, 32);
            }

            var n = enc.EncodeFrame(
                frame.AsSpan(0, ySize),
                frame.AsSpan(ySize, uvSize),
                frame.AsSpan(ySize + uvSize, uvSize),
                w, w / 2, annex,
                forceKeyframe: f == 0);
            output.Write(annex, 0, n);
        }

        return output.ToArray();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
