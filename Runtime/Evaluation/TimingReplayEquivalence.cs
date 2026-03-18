using System;
using System.Collections.Generic;

namespace Tsunippy.Runtime.Evaluation
{
    public sealed class TimingReplayEquivalenceReport
    {
        public int ObservedDecisionCount { get; set; }
        public int ReplayDecisionCount { get; set; }
        public int MatchedDecisionCount { get; set; }
        public bool IsEquivalent { get; set; }
        public string ObservedFingerprint { get; set; } = string.Empty;
        public string ReplayFingerprint { get; set; } = string.Empty;
        public List<TimingReplayEquivalenceDiff> Divergences { get; set; } = new();
    }

    public sealed class TimingReplayEquivalenceDiff
    {
        public int Index { get; set; }
        public List<string> Reasons { get; set; } = new();
        public TimingDecisionTrace? Observed { get; set; }
        public TimingDecisionTrace? Replay { get; set; }
    }

    public readonly record struct TimingReplayEquivalenceOptions(
        double TimelineToleranceSeconds,
        float LockTolerance,
        float ConfidenceTolerance)
    {
        public static TimingReplayEquivalenceOptions Default
            => new(0.0005d, TimingMath.LockEqualityEpsilon, 0.0005f);
    }

    public static class TimingReplayEquivalence
    {
        public static TimingReplayEquivalenceReport Compare(
            IReadOnlyList<TimingDecisionTrace> observed,
            IReadOnlyList<TimingDecisionTrace> replayed,
            TimingReplayEquivalenceOptions? options = null,
            int maxDivergences = 16)
        {
            var resolvedOptions = options ?? TimingReplayEquivalenceOptions.Default;
            var report = new TimingReplayEquivalenceReport
            {
                ObservedDecisionCount = observed?.Count ?? 0,
                ReplayDecisionCount = replayed?.Count ?? 0,
                ObservedFingerprint = TimingReplayEvaluator.ComputeFingerprint(observed ?? Array.Empty<TimingDecisionTrace>()),
                ReplayFingerprint = TimingReplayEvaluator.ComputeFingerprint(replayed ?? Array.Empty<TimingDecisionTrace>()),
            };

            var max = Math.Max(report.ObservedDecisionCount, report.ReplayDecisionCount);
            for (int index = 0; index < max; index++)
            {
                TimingDecisionTrace? observedDecision = index < report.ObservedDecisionCount ? observed[index] : null;
                TimingDecisionTrace? replayDecision = index < report.ReplayDecisionCount ? replayed[index] : null;

                var diff = CompareDecision(index, observedDecision, replayDecision, resolvedOptions);
                if (diff == null)
                {
                    report.MatchedDecisionCount++;
                    continue;
                }

                if (report.Divergences.Count < maxDivergences)
                    report.Divergences.Add(diff);
            }

            report.IsEquivalent = report.MatchedDecisionCount == max
                                  && string.Equals(report.ObservedFingerprint, report.ReplayFingerprint, StringComparison.Ordinal);
            return report;
        }

        private static TimingReplayEquivalenceDiff CompareDecision(
            int index,
            TimingDecisionTrace? observed,
            TimingDecisionTrace? replayed,
            TimingReplayEquivalenceOptions options)
        {
            if (observed == null || replayed == null)
            {
                return new TimingReplayEquivalenceDiff
                {
                    Index = index,
                    Observed = observed,
                    Replay = replayed,
                    Reasons = new List<string> { observed == null ? "MissingObservedDecision" : "MissingReplayDecision" },
                };
            }

            var reasons = new List<string>();
            if (Math.Abs(observed.Value.TimelineSeconds - replayed.Value.TimelineSeconds) > options.TimelineToleranceSeconds)
                reasons.Add("Timeline");
            if (observed.Value.Source != replayed.Value.Source)
                reasons.Add("Source");
            if (observed.Value.Reason != replayed.Value.Reason)
                reasons.Add("Reason");
            if (observed.Value.Mode != replayed.Value.Mode)
                reasons.Add("Mode");
            if (observed.Value.Quality != replayed.Value.Quality)
                reasons.Add("Quality");
            if (observed.Value.ActionKind != replayed.Value.ActionKind)
                reasons.Add("ActionKind");
            if (observed.Value.ActionId != replayed.Value.ActionId)
                reasons.Add("ActionId");
            if (observed.Value.Sequence != replayed.Value.Sequence)
                reasons.Add("Sequence");
            if (!TimingMath.NearlyEqual(observed.Value.PredictedLock, replayed.Value.PredictedLock, options.LockTolerance))
                reasons.Add("PredictedLock");
            if (!TimingMath.NearlyEqual(observed.Value.ServerLock, replayed.Value.ServerLock, options.LockTolerance))
                reasons.Add("ServerLock");
            if (!TimingMath.NearlyEqual(observed.Value.FinalLock, replayed.Value.FinalLock, options.LockTolerance))
                reasons.Add("FinalLock");
            if (!TimingMath.NearlyEqual(observed.Value.RTT, replayed.Value.RTT, options.LockTolerance))
                reasons.Add("RTT");
            if (!TimingMath.NearlyEqual(observed.Value.PredictionConfidence, replayed.Value.PredictionConfidence, options.ConfidenceTolerance))
                reasons.Add("PredictionConfidence");
            if (!TimingMath.NearlyEqual(observed.Value.Correction, replayed.Value.Correction, options.LockTolerance))
                reasons.Add("Correction");

            if (reasons.Count == 0)
                return null;

            return new TimingReplayEquivalenceDiff
            {
                Index = index,
                Reasons = reasons,
                Observed = observed,
                Replay = replayed,
            };
        }
    }
}
