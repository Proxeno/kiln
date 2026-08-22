using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Internal.H264.Queue;
using Kiln.RateControl;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Phase 3 unit tests for queue-aware frame dropping.
/// Tests verify that the LatestFrameQueue maintains newest-frame-wins semantics,
/// tracks dropped frames accurately, and the FrameDropPolicy detects stale frames correctly.
/// </summary>
public sealed class Phase3_FrameDropTests
{
    /// <summary>Helper record for test frames.</summary>
    private sealed record TestFrame(int Id);

    // ========== LatestFrameQueue Tests ==========

    /// <summary>
    /// Test 1: Queue keeps only the latest frame when multiple frames are enqueued.
    /// Enqueue 3 frames rapidly and verify that only the newest one is retrieved.
    /// </summary>
    [Fact]
    public void Queue_KeepsLatestFrame_WhenMultipleEnqueued()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        var frame1 = new TestFrame(1);
        var frame2 = new TestFrame(2);
        var frame3 = new TestFrame(3);

        queue.Enqueue(frame1, 0);
        queue.Enqueue(frame2, 10);
        queue.Enqueue(frame3, 20);

        Assert.True(queue.TryDequeue(out var dequeuedFrame, out var arrivalTime));
        Assert.NotNull(dequeuedFrame!);
        Assert.Equal(3, dequeuedFrame.Id);
        Assert.Equal(20, arrivalTime);
    }

    /// <summary>
    /// Test 2: Queue drops old frames and tracks the count.
    /// When 3 frames are enqueued in rapid succession, 2 should be dropped.
    /// </summary>
    [Fact]
    public void Queue_DropsOldFrames_AndTracksCount()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        queue.Enqueue(new TestFrame(1), 0);
        queue.Enqueue(new TestFrame(2), 1);
        queue.Enqueue(new TestFrame(3), 2);

        // When frame 2 is enqueued, frame 1 is dropped (count = 1)
        // When frame 3 is enqueued, frame 2 is dropped (count = 2)
        Assert.Equal(2, queue.DroppedFrameCount);

        // Dequeue should return only frame 3
        Assert.True(queue.TryDequeue(out var frame, out _));
        Assert.NotNull(frame!);
        Assert.Equal(3, frame.Id);

        // Queue is now empty
        Assert.False(queue.TryDequeue(out _, out _));
    }

    /// <summary>
    /// Test 3: Queue never exceeds depth of 1 even under extreme load.
    /// Enqueue 1000 frames and verify PendingFrameCount never exceeds 1.
    /// </summary>
    [Fact]
    public void Queue_NeverExceedsDepthOne()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        for (int i = 0; i < 1000; i++)
        {
            queue.Enqueue(new TestFrame(i), i);
            Assert.True(
                queue.PendingFrameCount <= 1,
                $"Queue depth should not exceed 1, but was {queue.PendingFrameCount} at iteration {i}"
            );
        }
    }

    /// <summary>
    /// Test 4: HasPendingFrame does not remove the frame.
    /// Call HasPendingFrame multiple times and verify the frame is still there.
    /// </summary>
    [Fact]
    public void Queue_HasPendingFrame_DoesNotRemove()
    {
        var queue = new LatestFrameQueue<TestFrame>();
        queue.Enqueue(new TestFrame(1), 0);

        Assert.True(queue.HasPendingFrame());
        Assert.True(queue.HasPendingFrame()); // Should still return true

        Assert.True(queue.TryDequeue(out var frame, out _));
        Assert.NotNull(frame);
        Assert.Equal(1, frame.Id);

        Assert.False(queue.HasPendingFrame()); // Now empty
    }

    /// <summary>
    /// Test 5: TryDequeue returns false when queue is empty.
    /// </summary>
    [Fact]
    public void Queue_TryDequeue_ReturnsFalse_WhenEmpty()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        Assert.False(queue.TryDequeue(out var frame, out var arrivalTime));
        Assert.Null(frame);
        Assert.Equal(0, arrivalTime);
    }

    /// <summary>
    /// Test 6: Queue accurately tracks arrival time.
    /// Enqueue with specific times and verify retrieval maintains accuracy.
    /// </summary>
    [Fact]
    public void Queue_TracksArrivalTime_Accurately()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        queue.Enqueue(new TestFrame(1), 100);
        queue.Enqueue(new TestFrame(2), 150);

        Assert.Equal(150, queue.LatestFrameArrivalTimeMs);

        queue.TryDequeue(out var frame, out var arrivalTime);
        Assert.Equal(150, arrivalTime);
        Assert.NotNull(frame!);
        Assert.Equal(2, frame.Id);
    }

    /// <summary>
    /// Test 7: RecordDroppedFrame increments the counter.
    /// Call RecordDroppedFrame multiple times and verify count.
    /// </summary>
    [Fact]
    public void Queue_DroppedCount_IncrementedByRecordDroppedFrame()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        Assert.Equal(0, queue.DroppedFrameCount);

        queue.RecordDroppedFrame();
        Assert.Equal(1, queue.DroppedFrameCount);

        queue.RecordDroppedFrame();
        queue.RecordDroppedFrame();
        Assert.Equal(3, queue.DroppedFrameCount);
    }

    /// <summary>
    /// Test 8: Queue is thread-safe under concurrent enqueue/dequeue.
    /// Multiple threads enqueuing and dequeueing simultaneously.
    /// </summary>
    /// <remarks>
    /// LatestFrameQueue is a single-slot "newest wins" buffer (see class doc): a producer
    /// racing ahead of a slow consumer is expected to silently overwrite/drop older pending
    /// frames. The previous version of this test asserted every one of the 100 enqueued
    /// frames would be dequeued, which is incompatible with that documented contract — the
    /// fast producer loop finishes almost immediately, most frames get dropped, and
    /// `dequeueCount < frameCount` can then never become false, hanging forever (reproduced
    /// via a captured blame-hang dump). Assert the real accounting invariant instead: every
    /// enqueued frame is either dequeued or counted as dropped, with nothing lost or
    /// corrupted under concurrent access.
    /// </remarks>
    [Fact]
    public async Task Queue_IsThreadSafe_ConcurrentEnqueueDequeue()
    {
        var queue = new LatestFrameQueue<TestFrame>();
        var dequeuedFrames = new ConcurrentBag<TestFrame>();
        const int frameCount = 100;

        var enqueueTask = Task.Run(() =>
        {
            for (int i = 0; i < frameCount; i++)
            {
                queue.Enqueue(new TestFrame(i), i);
            }
        });

        var dequeueTask = Task.Run(() =>
        {
            while (!enqueueTask.IsCompleted || queue.HasPendingFrame())
            {
                if (queue.TryDequeue(out var frame, out _) && frame != null)
                {
                    dequeuedFrames.Add(frame);
                }
                else
                {
                    Thread.Sleep(0); // Yield
                }
            }
        });

        await Task.WhenAll(enqueueTask, dequeueTask).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(frameCount, dequeuedFrames.Count + queue.DroppedFrameCount);
        Assert.All(dequeuedFrames, frame => Assert.NotNull(frame));
    }

    /// <summary>
    /// Test 9: ResetDroppedFrameCount resets the counter.
    /// Record some drops, then reset and verify counter goes to zero.
    /// </summary>
    [Fact]
    public void Queue_ResetDroppedFrameCount_RetsCounterToZero()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        queue.Enqueue(new TestFrame(1), 0);
        queue.Enqueue(new TestFrame(2), 1); // Drops frame 1
        queue.Enqueue(new TestFrame(3), 2); // Drops frame 2

        Assert.Equal(2, queue.DroppedFrameCount);

        queue.ResetDroppedFrameCount();
        Assert.Equal(0, queue.DroppedFrameCount);
    }

    /// <summary>
    /// Test 10: Clear removes the pending frame without incrementing drop count.
    /// </summary>
    [Fact]
    public void Queue_Clear_RemovesFrameWithoutDropping()
    {
        var queue = new LatestFrameQueue<TestFrame>();

        queue.Enqueue(new TestFrame(1), 0);
        Assert.Equal(0, queue.DroppedFrameCount); // No drops yet

        queue.Clear();
        Assert.False(queue.HasPendingFrame());
        Assert.Equal(0, queue.DroppedFrameCount); // Clear doesn't count as a drop
    }

    // ========== FrameDropPolicy Tests ==========

    /// <summary>
    /// Test 11: FrameDropPolicy drops stale frames with backlog.
    /// A frame older than 50ms with pending frames > 1 should be dropped.
    /// </summary>
    [Fact]
    public void FrameDropPolicy_DropsStalFrame_WithBacklog()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 100; // 100ms later (stale)
        var pendingInputFrames = 2; // Backlog present

        var shouldDrop = policy.ShouldDropCurrentFrame(
            currentFrameArrivalTimeMs,
            currentTimeMs,
            pendingInputFrames);

        Assert.True(shouldDrop, "Stale frame (age > 50ms) with backlog (pending > 1) should be dropped");
    }

    /// <summary>
    /// Test 12: FrameDropPolicy does not drop fresh frames.
    /// A frame younger than 50ms should not be dropped.
    /// </summary>
    [Fact]
    public void FrameDropPolicy_DoesNotDrop_FreshFrame()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 10; // Only 10ms later (fresh)
        var pendingInputFrames = 5; // Even with backlog

        var shouldDrop = policy.ShouldDropCurrentFrame(
            currentFrameArrivalTimeMs,
            currentTimeMs,
            pendingInputFrames);

        Assert.False(shouldDrop, "Fresh frame (age < 50ms) should not be dropped");
    }

    /// <summary>
    /// Test 13: FrameDropPolicy does not drop stale frames without backlog.
    /// A frame older than 50ms but no backlog (pending = 1) should not be dropped.
    /// </summary>
    [Fact]
    public void FrameDropPolicy_DoesNotDrop_WithoutBacklog()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 100; // Stale (100ms old)
        var pendingInputFrames = 1; // No backlog

        var shouldDrop = policy.ShouldDropCurrentFrame(
            currentFrameArrivalTimeMs,
            currentTimeMs,
            pendingInputFrames);

        Assert.False(shouldDrop, "Stale frame without backlog (pending = 1) should not be dropped");
    }

    /// <summary>
    /// Test 14: FrameDropPolicy respects custom stale threshold.
    /// When a custom threshold is provided, it should be used instead of default (50ms).
    /// </summary>
    [Fact]
    public void FrameDropPolicy_RespectsCustomThreshold()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 75; // 75ms old
        var pendingInputFrames = 2;
        var customThresholdMs = 100; // Custom threshold

        var shouldDrop = policy.ShouldDropCurrentFrame(
            currentFrameArrivalTimeMs,
            currentTimeMs,
            pendingInputFrames,
            customThresholdMs);

        Assert.False(shouldDrop, "Frame age (75ms) < threshold (100ms) should not be dropped");
    }

    /// <summary>
    /// Test 15: FrameDropPolicy edge case at exactly the threshold.
    /// A frame exactly at the threshold age should not be dropped (>= check).
    /// </summary>
    [Fact]
    public void FrameDropPolicy_EdgeCase_ExactlyAtThreshold()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 50; // Exactly at default threshold
        var pendingInputFrames = 2;

        var shouldDrop = policy.ShouldDropCurrentFrame(
            currentFrameArrivalTimeMs,
            currentTimeMs,
            pendingInputFrames);

        // Frame is NOT older than 50ms (it's exactly 50ms), so should not drop
        // The condition is frameAgeMs > staleThresholdMs, not >=
        Assert.False(shouldDrop, "Frame exactly at threshold should not be dropped");
    }

    /// <summary>
    /// Test 16: FrameDropPolicy respects pending input frame count boundary.
    /// With exactly 1 pending frame (no backlog), should not drop even if stale.
    /// With exactly 2 pending frames (backlog present), should drop if stale.
    /// </summary>
    [Fact]
    public void FrameDropPolicy_BacklogBoundary()
    {
        var logger = new NullLogger<FrameDropPolicy>();
        var policy = new FrameDropPolicy(logger);

        var currentFrameArrivalTimeMs = 0;
        var currentTimeMs = 100; // Stale

        // With 1 pending frame
        var shouldDrop1 = policy.ShouldDropCurrentFrame(currentFrameArrivalTimeMs, currentTimeMs, 1);
        Assert.False(shouldDrop1, "With 1 pending frame, should not drop");

        // With 2 pending frames
        var shouldDrop2 = policy.ShouldDropCurrentFrame(currentFrameArrivalTimeMs, currentTimeMs, 2);
        Assert.True(shouldDrop2, "With 2 pending frames, should drop");
    }
}
