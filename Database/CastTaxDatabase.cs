using System;
using System.Collections.Generic;

namespace Tsunippy.Database
{
    /// <summary>
    /// Learned database for cast tax values, keyed by action and context.
    /// This is intentionally separate from the main lock database because cast tax
    /// operates on a different value range than normal animation locks.
    /// </summary>
    [Serializable]
    public class CastTaxDatabase
    {
        private const float DefaultCastTax = 0.1f;
        private const float MinimumCastTax = 0.05f;
        private const float MaximumCastTax = 0.25f;
        private const float MinimumOutlierWindow = 0.015f;
        private const int ResetOutlierThreshold = 3;

        public Dictionary<string, LockEntry> Entries { get; set; } = new();

        private static string MakeKey(uint actionID, GameContext context) => $"{actionID}_{(byte)context}";

        public float GetTax(uint actionID, GameContext context, float defaultTax = DefaultCastTax)
        {
            if (!Entries.TryGetValue(MakeKey(actionID, context), out var entry))
                return defaultTax;

            if (!float.IsFinite(entry.MeanLock) || entry.MeanLock < MinimumCastTax || entry.MeanLock > MaximumCastTax)
                return defaultTax;

            RefreshState(entry);
            return entry.CanUseForPrediction(MinimumCastTax, MaximumCastTax) ? entry.MeanLock : defaultTax;
        }

        public bool RecordTax(uint actionID, GameContext context, float lockValue)
        {
            if (!float.IsFinite(lockValue) || lockValue < MinimumCastTax || lockValue > MaximumCastTax)
                return false;

            var key = MakeKey(actionID, context);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!Entries.TryGetValue(key, out var entry))
            {
                Entries[key] = new LockEntry
                {
                    MeanLock = lockValue,
                    MeanDeviation = 0.005f,
                    SampleCount = 1,
                    LastObservedUnix = now,
                    State = LearnedEntryState.Learning,
                };
                return true;
            }

            if (!entry.CanLearn)
            {
                entry.State = LearnedEntryState.Frozen;
                return false;
            }

            var delta = Math.Abs(entry.MeanLock - lockValue);
            if (entry.SampleCount >= 3)
            {
                var allowedDrift = Math.Max(entry.MeanDeviation * 4f, MinimumOutlierWindow);
                if (delta > allowedDrift)
                {
                    entry.OutlierStreak++;
                    entry.State = LearnedEntryState.Shadow;
                    if (entry.OutlierStreak < ResetOutlierThreshold)
                        return false;

                    entry.MeanLock = lockValue;
                    entry.MeanDeviation = 0.005f;
                    entry.SampleCount = Math.Min(entry.SampleCount, 2);
                    entry.OutlierStreak = 0;
                    entry.LastObservedUnix = now;
                    entry.State = LearnedEntryState.Learning;
                    return true;
                }
            }

            entry.OutlierStreak = 0;

            if (delta < 0.0001f)
            {
                if (entry.SampleCount < 1000)
                    entry.SampleCount++;
                entry.LastObservedUnix = now;
                RefreshState(entry);
                return false;
            }

            entry.SampleCount = Math.Min(entry.SampleCount + 1, 1000);
            entry.MeanLock += (lockValue - entry.MeanLock) / entry.SampleCount;
            entry.MeanDeviation += (delta - entry.MeanDeviation) / entry.SampleCount;
            entry.LastObservedUnix = now;
            RefreshState(entry);
            return true;
        }

        public LockEntry GetEntry(uint actionID, GameContext context)
        {
            if (!Entries.TryGetValue(MakeKey(actionID, context), out var entry))
                return null;

            RefreshState(entry);
            return entry;
        }

        public bool ResetEntry(uint actionID, GameContext context)
            => Entries.Remove(MakeKey(actionID, context));

        public bool SetFrozen(uint actionID, GameContext context, bool frozen)
        {
            if (!Entries.TryGetValue(MakeKey(actionID, context), out var entry))
                return false;

            entry.Frozen = frozen;
            entry.State = frozen ? LearnedEntryState.Frozen : LearnedEntryState.Learning;
            RefreshState(entry);
            return true;
        }

        public void Reset() => Entries.Clear();

        private static void RefreshState(LockEntry entry)
        {
            if (entry.Frozen)
            {
                entry.State = LearnedEntryState.Frozen;
                return;
            }

            if (entry.OutlierStreak > 0)
            {
                entry.State = LearnedEntryState.Shadow;
                return;
            }

            entry.State = entry.Confidence >= 0.6f
                ? LearnedEntryState.Trusted
                : LearnedEntryState.Learning;
        }
    }
}
