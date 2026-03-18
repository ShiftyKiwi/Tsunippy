using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tsunippy.Runtime.Evaluation
{
    public sealed class TimingReplayEvaluator
    {
        private readonly List<TimingDecisionTrace> decisions = new();
        private readonly Dictionary<string, int> reasonCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> sourceCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> modeCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> qualityCounts = new(StringComparer.Ordinal);

        private double absoluteCorrectionSum;
        private double absoluteDisagreementSum;
        private double reductionSum;
        private double confidenceSum;

        public int EventCount { get; private set; }
        public int PredictionCount { get; private set; }
        public int CorrectionCount { get; private set; }
        public int ResetCount { get; private set; }
        public int QuarantineCount { get; private set; }
        public int DryRunCount { get; private set; }
        public int InstantDecisionCount { get; private set; }
        public int CastDecisionCount { get; private set; }
        public int OvershootCount { get; private set; }
        public int UndershootCount { get; private set; }
        public int NeutralCount { get; private set; }

        public void ObserveEvent() => EventCount++;

        public void ObserveDecision(in TimingDecisionTrace decision)
        {
            decisions.Add(decision);
            Increment(reasonCounts, decision.Reason.ToString());
            Increment(sourceCounts, decision.Source.ToString());
            Increment(modeCounts, decision.Mode.ToString());
            Increment(qualityCounts, decision.Quality.ToString());

            confidenceSum += decision.PredictionConfidence;
            absoluteCorrectionSum += Math.Abs(decision.Correction) * 1000d;
            absoluteDisagreementSum += Math.Abs(decision.ServerLock - decision.FinalLock) * 1000d;
            reductionSum += Math.Max(decision.ServerLock - decision.FinalLock, 0f) * 1000d;

            if (decision.ActionKind == TimingActionKind.Instant)
                InstantDecisionCount++;
            else
                CastDecisionCount++;

            if (decision.Source is TimingDecisionSource.InstantPrediction or TimingDecisionSource.CastPrediction)
                PredictionCount++;

            if (decision.Reason is TimingDecisionReason.AppliedInstantCorrection or TimingDecisionReason.AppliedCastCorrection)
                CorrectionCount++;

            if (decision.Reason == TimingDecisionReason.RuntimeReset)
                ResetCount++;

            if (decision.Mode is TimingRuntimeMode.ConflictQuarantined or TimingRuntimeMode.FailureQuarantined)
                QuarantineCount++;

            if (decision.Mode == TimingRuntimeMode.DryRunRequested || decision.Reason == TimingDecisionReason.DryRunSuppressed)
                DryRunCount++;

            var disagreement = decision.FinalLock - decision.ServerLock;
            if (TimingMath.NearlyEqual(disagreement, 0f))
                NeutralCount++;
            else if (disagreement > 0f)
                OvershootCount++;
            else
                UndershootCount++;
        }

        public TimingReplayRunResult Build(string profileName)
            => new()
            {
                ProfileName = profileName,
                EventCount = EventCount,
                DecisionCount = decisions.Count,
                PredictionCount = PredictionCount,
                CorrectionCount = CorrectionCount,
                ResetCount = ResetCount,
                QuarantineCount = QuarantineCount,
                DryRunCount = DryRunCount,
                InstantDecisionCount = InstantDecisionCount,
                CastDecisionCount = CastDecisionCount,
                OvershootCount = OvershootCount,
                UndershootCount = UndershootCount,
                NeutralCount = NeutralCount,
                AverageAbsoluteCorrectionMs = decisions.Count > 0 ? absoluteCorrectionSum / decisions.Count : 0d,
                AverageAbsoluteDisagreementMs = decisions.Count > 0 ? absoluteDisagreementSum / decisions.Count : 0d,
                AverageReductionMs = decisions.Count > 0 ? reductionSum / decisions.Count : 0d,
                AverageConfidence = decisions.Count > 0 ? confidenceSum / decisions.Count : 0d,
                DecisionFingerprint = ComputeFingerprint(decisions),
                ReasonCounts = new Dictionary<string, int>(reasonCounts, StringComparer.Ordinal),
                SourceCounts = new Dictionary<string, int>(sourceCounts, StringComparer.Ordinal),
                ModeCounts = new Dictionary<string, int>(modeCounts, StringComparer.Ordinal),
                QualityCounts = new Dictionary<string, int>(qualityCounts, StringComparer.Ordinal),
                Decisions = new List<TimingDecisionTrace>(decisions),
            };

        public static TimingComparisonResult Compare(TimingReplayRunResult left, TimingReplayRunResult right, int maxDivergences = 12)
        {
            var result = new TimingComparisonResult
            {
                Left = left,
                Right = right,
                CorrectionDeltaMs = right.AverageAbsoluteCorrectionMs - left.AverageAbsoluteCorrectionMs,
                DisagreementDeltaMs = right.AverageAbsoluteDisagreementMs - left.AverageAbsoluteDisagreementMs,
                ReductionDeltaMs = right.AverageReductionMs - left.AverageReductionMs,
                DryRunDelta = right.DryRunCount - left.DryRunCount,
                QuarantineDelta = right.QuarantineCount - left.QuarantineCount,
                PredictionDelta = right.PredictionCount - left.PredictionCount,
                CorrectionCountDelta = right.CorrectionCount - left.CorrectionCount,
            };

            var max = Math.Max(left.Decisions.Count, right.Decisions.Count);
            for (int index = 0; index < max && result.Divergences.Count < maxDivergences; index++)
            {
                TimingDecisionTrace? leftDecision = index < left.Decisions.Count ? left.Decisions[index] : null;
                TimingDecisionTrace? rightDecision = index < right.Decisions.Count ? right.Decisions[index] : null;

                if (leftDecision == null || rightDecision == null)
                {
                    result.Divergences.Add(new TimingDecisionMismatch { Index = index, Left = leftDecision, Right = rightDecision });
                    continue;
                }

                if (leftDecision.Value.Reason != rightDecision.Value.Reason
                    || !TimingMath.NearlyEqual(leftDecision.Value.FinalLock, rightDecision.Value.FinalLock)
                    || !TimingMath.NearlyEqual(leftDecision.Value.PredictedLock, rightDecision.Value.PredictedLock))
                {
                    result.Divergences.Add(new TimingDecisionMismatch { Index = index, Left = leftDecision, Right = rightDecision });
                }
            }

            return result;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out var value))
                counts[key] = value + 1;
            else
                counts[key] = 1;
        }

        public static string ComputeFingerprint(IEnumerable<TimingDecisionTrace> decisions)
        {
            var builder = new StringBuilder();
            foreach (var decision in decisions)
            {
                builder
                    .Append(decision.TimelineSeconds.ToString("F6"))
                    .Append('|').Append(decision.Source)
                    .Append('|').Append(decision.Reason)
                    .Append('|').Append(decision.ActionId)
                    .Append('|').Append(decision.Sequence)
                    .Append('|').Append(decision.PredictedLock.ToString("F6"))
                    .Append('|').Append(decision.FinalLock.ToString("F6"))
                    .Append('|').Append(decision.RTT.ToString("F6"))
                    .AppendLine();
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(bytes);
        }
    }
}
