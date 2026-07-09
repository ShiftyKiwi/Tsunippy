using System;

namespace Tsunippy.Runtime
{
    public sealed class ModelEpoch
    {
        public ulong Current { get; private set; } = 1;
        public string LastResetReason { get; private set; } = "startup";
        public long LastResetTick { get; private set; } = Environment.TickCount64;
        public int StalePredictionsInvalidated { get; private set; }

        public TimeSpan TimeSinceReset => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - LastResetTick));

        public void Reset(string reason)
        {
            Current++;
            LastResetReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            LastResetTick = Environment.TickCount64;
        }

        public void AddStaleInvalidations(int count)
        {
            if (count > 0)
                StalePredictionsInvalidated += count;
        }
    }
}
