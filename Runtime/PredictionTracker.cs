using System;
using System.Collections.Generic;

namespace Tsunippy.Runtime
{
    public sealed class PredictionTracker
    {
        private readonly Dictionary<ushort, PendingPrediction> predictions = new();
        private long nextCleanupTick;

        public int Count => predictions.Count;
        public int ExpiredCount { get; private set; }
        public int StaleEpochCount { get; private set; }
        public string LastRejectionReason { get; private set; } = "none";

        public void Add(PendingPrediction prediction)
        {
            predictions[prediction.Sequence] = prediction;
        }

        public bool TryGetPending(ushort sequence, ulong currentEpoch, long nowTick, out PendingPrediction prediction)
        {
            if (!predictions.TryGetValue(sequence, out prediction))
                return false;

            if (prediction.ModelEpoch != currentEpoch || prediction.ExpiresTick < nowTick)
            {
                prediction = null;
                return false;
            }

            return true;
        }

        public bool TryConsume(ushort sequence, uint actionId, ulong currentEpoch, long nowTick, out PendingPrediction prediction)
        {
            prediction = null;
            if (!predictions.TryGetValue(sequence, out var candidate))
            {
                LastRejectionReason = "no pending prediction";
                return false;
            }

            predictions.Remove(sequence);

            if (candidate.ModelEpoch != currentEpoch)
            {
                StaleEpochCount++;
                LastRejectionReason = "stale epoch";
                return false;
            }

            if (candidate.ExpiresTick < nowTick)
            {
                ExpiredCount++;
                LastRejectionReason = "expired";
                return false;
            }

            if (actionId != 0 && candidate.ActionId != 0 && actionId != candidate.ActionId)
            {
                LastRejectionReason = "action mismatch";
                return false;
            }

            prediction = candidate;
            LastRejectionReason = "accepted";
            return true;
        }

        public int Cleanup(long nowTick, ulong currentEpoch, int maxScans = 16)
        {
            if (nowTick < nextCleanupTick || predictions.Count == 0)
                return 0;

            nextCleanupTick = nowTick + 250;
            var removed = 0;
            var scanned = 0;
            Span<ushort> removeKeys = stackalloc ushort[Math.Min(maxScans, 64)];

            foreach (var (sequence, prediction) in predictions)
            {
                if (scanned >= maxScans || removed >= removeKeys.Length)
                    break;

                scanned++;
                if (prediction.ExpiresTick >= nowTick && prediction.ModelEpoch == currentEpoch)
                    continue;

                removeKeys[removed++] = sequence;
                if (prediction.ModelEpoch != currentEpoch)
                    StaleEpochCount++;
                else
                    ExpiredCount++;
            }

            for (var i = 0; i < removed; i++)
                predictions.Remove(removeKeys[i]);

            return removed;
        }

        public int RemoveEpochsBefore(ulong currentEpoch)
        {
            if (predictions.Count == 0)
                return 0;

            var keys = new List<ushort>();
            foreach (var (sequence, prediction) in predictions)
            {
                if (prediction.ModelEpoch != currentEpoch)
                    keys.Add(sequence);
            }

            foreach (var key in keys)
                predictions.Remove(key);

            StaleEpochCount += keys.Count;
            return keys.Count;
        }

        public void Clear()
        {
            predictions.Clear();
        }
    }
}
