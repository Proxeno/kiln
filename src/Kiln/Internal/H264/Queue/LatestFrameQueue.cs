namespace Kiln.Internal.H264.Queue;

/// <summary>
/// A thread-safe queue that retains only the newest frame (depth ≤ 1).
/// When a newer frame arrives while an older frame is pending, the older frame is silently dropped.
/// This implements "newest-frame-wins" semantics for low-latency encoding scenarios.
/// </summary>
public sealed class LatestFrameQueue<T> where T : class
{
    private T? _latestFrame;
    private long _frameArrivalTimeMs;
    private int _droppedFrameCount;
    private readonly object _lock = new object();

    /// <summary>
    /// Enqueue a frame. If a frame is already pending, it is silently dropped and replaced
    /// with the new frame. The dropped frame count is incremented automatically.
    /// </summary>
    public void Enqueue(T frame, long arrivalTimeMs)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_lock)
        {
            // If there's already a pending frame, record it as dropped
            if (_latestFrame != null)
            {
                _droppedFrameCount++;
            }

            _latestFrame = frame;
            _frameArrivalTimeMs = arrivalTimeMs;
        }
    }

    /// <summary>
    /// Try to dequeue the latest frame. Returns true if a frame was available,
    /// false if the queue is empty. The frame is removed from the queue after dequeue.
    /// </summary>
    public bool TryDequeue(out T? frame, out long arrivalTimeMs)
    {
        lock (_lock)
        {
            if (_latestFrame == null)
            {
                frame = null;
                arrivalTimeMs = 0;
                return false;
            }

            frame = _latestFrame;
            arrivalTimeMs = _frameArrivalTimeMs;
            _latestFrame = null; // Clear after dequeue
            return true;
        }
    }

    /// <summary>
    /// Check if a pending frame is available without removing it.
    /// Useful for checking if a newer frame has arrived during encoding.
    /// </summary>
    public bool HasPendingFrame()
    {
        lock (_lock)
        {
            return _latestFrame != null;
        }
    }

    /// <summary>
    /// Get the cumulative count of dropped frames.
    /// This counter is incremented whenever Enqueue replaces a pending frame
    /// or when RecordDroppedFrame() is called explicitly.
    /// </summary>
    public int DroppedFrameCount
    {
        get
        {
            lock (_lock)
            {
                return _droppedFrameCount;
            }
        }
    }

    /// <summary>
    /// Get the current queue depth (0 or 1).
    /// Returns 1 if there is a pending frame, 0 if empty.
    /// </summary>
    public int PendingFrameCount
    {
        get
        {
            lock (_lock)
            {
                return _latestFrame != null ? 1 : 0;
            }
        }
    }

    /// <summary>
    /// Get the arrival time (in milliseconds) of the pending frame.
    /// Returns 0 if the queue is empty.
    /// </summary>
    public long LatestFrameArrivalTimeMs
    {
        get
        {
            lock (_lock)
            {
                return _frameArrivalTimeMs;
            }
        }
    }

    /// <summary>
    /// Clear the queue by removing the pending frame.
    /// Useful for scene changes or system resets.
    /// Does not increment the dropped frame counter.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _latestFrame = null;
        }
    }

    /// <summary>
    /// Explicitly record a dropped frame (increment the dropped frame counter).
    /// Called by external code when dropping frames due to policy decisions.
    /// </summary>
    public void RecordDroppedFrame()
    {
        lock (_lock)
        {
            _droppedFrameCount++;
        }
    }

    /// <summary>
    /// Reset the dropped frame counter (e.g., at the start of a new scene or analysis window).
    /// </summary>
    public void ResetDroppedFrameCount()
    {
        lock (_lock)
        {
            _droppedFrameCount = 0;
        }
    }
}
