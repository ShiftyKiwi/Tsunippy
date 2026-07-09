using System;
using System.Collections.Generic;

namespace Tsunippy.Database
{
    /// <summary>
    /// Represents the game context for keying lock database entries.
    /// PvP and PvE can have different animation lock values for the same action.
    /// </summary>
    public enum GameContext : byte
    {
        PvE = 0,
        PvP = 1,
    }

    public enum LearnedEntryState : byte
    {
        // Learning: samples are being collected, but active prediction uses the safer fallback.
        Learning = 0,
        // Trusted: enough clean samples have been accepted for active prediction.
        Trusted = 1,
        // Shadow: recent outliers made this entry suspicious; keep learning, but do not predict from it.
        Shadow = 2,
        // Frozen: do not learn new samples. A frozen entry is only usable if it is already strict-enough.
        Frozen = 3,
    }

    /// <summary>
    /// A single entry in the lock database tracking the observed animation lock
    /// for a specific (actionID, context) pair.
    /// </summary>
    [Serializable]
    public class LockEntry
    {
        /// <summary>The mean observed animation lock value in seconds.</summary>
        public float MeanLock { get; set; }

        /// <summary>Mean absolute deviation of learned samples.</summary>
        public float MeanDeviation { get; set; }

        /// <summary>Number of times this lock has been observed.</summary>
        public int SampleCount { get; set; }

        /// <summary>Consecutive outliers seen against the current learned mean.</summary>
        public int OutlierStreak { get; set; }

        /// <summary>Unix timestamp of the last accepted sample.</summary>
        public long LastObservedUnix { get; set; }

        /// <summary>Learning/trust state used to avoid trusting fresh or suspicious entries too early.</summary>
        public LearnedEntryState State { get; set; } = LearnedEntryState.Learning;

        /// <summary>Frozen entries can still be used when trusted, but do not accept new samples.</summary>
        public bool Frozen { get; set; }

        /// <summary>
        /// Confidence level (0..1) based on sample count.
        /// Reaches 1.0 at 10 samples. Used to determine whether to trust
        /// this entry or fall back to the default lock value.
        /// </summary>
        public float Confidence => Math.Min(SampleCount / 10f, 1f);

        public bool CanLearn => !Frozen;

        public bool IsTrusted => State == LearnedEntryState.Trusted;

        public bool CanUseForPrediction(float minimumValue, float maximumValue, float minimumConfidence = 0.6f)
        {
            if (!float.IsFinite(MeanLock)
                || MeanLock < minimumValue
                || MeanLock > maximumValue
                || Confidence < minimumConfidence
                || OutlierStreak != 0)
                return false;

            return State switch
            {
                LearnedEntryState.Trusted => true,
                LearnedEntryState.Frozen => true,
                LearnedEntryState.Learning => false,
                LearnedEntryState.Shadow => false,
                _ => false,
            };
        }
    }

    /// <summary>
    /// Context-aware animation lock database with confidence tracking.
    ///
    /// Improvements over NoClippy's Dictionary&lt;uint, float&gt;:
    /// 1. Keyed by (actionID, PvE/PvP context) — separate lock values per mode
    /// 2. Tracks sample count for confidence estimation
    /// 3. Uses incremental mean calculation for stability
    /// 4. Low-confidence entries fall back to defaults
    /// 5. Caps sample count at 1000 to allow eventual adaptation to game patches
    /// </summary>
    [Serializable]
    public class LockDatabase
    {
        private const float MinimumLock = 0.5f;
        private const float MaximumLock = 2.5f;
        private const float MinimumOutlierWindow = 0.03f;
        private const int ResetOutlierThreshold = 3;

        /// <summary>
        /// The database entries, keyed by "{actionID}_{contextByte}".
        /// String keys are used for JSON serialization compatibility.
        /// </summary>
        public Dictionary<string, LockEntry> Entries { get; set; } = new();

        private static string MakeKey(uint actionID, GameContext ctx) => $"{actionID}_{(byte)ctx}";
        public static bool TryParseKey(string key, out uint actionID, out GameContext context)
        {
            actionID = 0;
            context = GameContext.PvE;
            var split = key?.Split('_');
            if (split is not { Length: 2 })
                return false;

            if (!uint.TryParse(split[0], out actionID) || !byte.TryParse(split[1], out var contextByte))
                return false;

            context = (GameContext)contextByte;
            return true;
        }

        /// <summary>
        /// Get the known animation lock for an action in a given context.
        /// Returns defaultLock if the action is unknown or has insufficient confidence.
        /// </summary>
        /// <param name="actionID">The resolved spell ID.</param>
        /// <param name="context">PvE or PvP.</param>
        /// <param name="defaultLock">Fallback value (default 0.5s, the game's default).</param>
        /// <returns>The predicted animation lock in seconds.</returns>
        public float GetLock(uint actionID, GameContext context, float defaultLock = 0.5f)
        {
            var key = MakeKey(actionID, context);
            if (!Entries.TryGetValue(key, out var entry))
                return defaultLock;

            // Sanity check: animation locks should never be below 0.5s
            if (!float.IsFinite(entry.MeanLock) || entry.MeanLock < MinimumLock || entry.MeanLock > MaximumLock)
                return defaultLock;

            RefreshState(entry);

            // Shadow/learning entries keep collecting data but use the safer game default.
            return entry.CanUseForPrediction(MinimumLock, MaximumLock) ? entry.MeanLock : defaultLock;
        }

        /// <summary>
        /// Record a new animation lock observation from a server response.
        /// Updates the entry using incremental mean calculation.
        /// </summary>
        /// <param name="actionID">The resolved spell ID.</param>
        /// <param name="context">PvE or PvP.</param>
        /// <param name="lockValue">The server-reported animation lock in seconds.</param>
        /// <returns>True if this was a new or changed value, false if unchanged.</returns>
        public bool RecordLock(uint actionID, GameContext context, float lockValue)
        {
            if (!float.IsFinite(lockValue) || lockValue < MinimumLock || lockValue > MaximumLock)
                return false;

            var key = MakeKey(actionID, context);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!Entries.TryGetValue(key, out var entry))
            {
                // New entry
                Entries[key] = new LockEntry
                {
                    MeanLock = lockValue,
                    MeanDeviation = 0.01f,
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

                    // Repeated outliers likely indicate a real shift after a patch or balance change.
                    entry.MeanLock = lockValue;
                    entry.MeanDeviation = 0.01f;
                    entry.SampleCount = Math.Min(entry.SampleCount, 2);
                    entry.OutlierStreak = 0;
                    entry.LastObservedUnix = now;
                    entry.State = LearnedEntryState.Learning;
                    return true;
                }
            }

            entry.OutlierStreak = 0;

            // Same value — just bump count for confidence
            if (delta < 0.0001f)
            {
                if (entry.SampleCount < 1000)
                    entry.SampleCount++;
                entry.LastObservedUnix = now;
                RefreshState(entry);
                return false;
            }

            // Different value — incremental mean update
            // Cap at 1000 samples so the mean can eventually adapt to game patches
            entry.SampleCount = Math.Min(entry.SampleCount + 1, 1000);
            entry.MeanLock += (lockValue - entry.MeanLock) / entry.SampleCount;
            entry.MeanDeviation += (delta - entry.MeanDeviation) / entry.SampleCount;
            entry.LastObservedUnix = now;
            RefreshState(entry);
            return true;
        }

        /// <summary>
        /// Check whether we have a confident entry for this action.
        /// </summary>
        public bool HasConfidentEntry(uint actionID, GameContext context)
        {
            var key = MakeKey(actionID, context);
            if (!Entries.TryGetValue(key, out var entry))
                return false;

            RefreshState(entry);
            return entry.CanUseForPrediction(MinimumLock, MaximumLock);
        }

        /// <summary>
        /// Get the entry for diagnostics display. May return null.
        /// </summary>
        public LockEntry GetEntry(uint actionID, GameContext context)
        {
            var key = MakeKey(actionID, context);
            if (!Entries.TryGetValue(key, out var entry))
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
