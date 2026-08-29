using System;

namespace ZeroAccel;

// Receives relative counts and monotonic timestamps, not cursor/screen coordinates.
// Event-driven averaging caps UI updates at 30 Hz without a timer or per-packet allocations.
internal sealed class MotionSampler
{
    private readonly long frequency;
    private long start, last;
    private bool initialized;
    private double distance;

    internal MotionSampler(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        this.frequency = frequency;
    }

    internal void Reset() { initialized = false; distance = 0; }

    internal bool Add(int x, int y, long timestamp, out double speed)
    {
        speed = 0;
        if (x == 0 && y == 0) return false;
        if (!initialized || timestamp < last || timestamp - last > frequency / 10)
        {
            initialized = true; start = last = timestamp; distance = 0;
            return false; // No reliable interval for the first packet after a pause.
        }
        last = timestamp;
        distance += Math.Sqrt((double)x * x + (double)y * y);
        long elapsed = timestamp - start;
        if (elapsed < Math.Max(1, (frequency + 29) / 30)) return false;
        speed = distance * frequency / (elapsed * 1000.0);
        start = timestamp; distance = 0;
        return true;
    }
}
