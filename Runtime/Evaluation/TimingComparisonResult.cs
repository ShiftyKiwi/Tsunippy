using System.Collections.Generic;

namespace Tsunippy.Runtime.Evaluation
{
    public sealed class TimingComparisonResult
    {
        public TimingReplayRunResult Left { get; set; } = new();
        public TimingReplayRunResult Right { get; set; } = new();
        public double CorrectionDeltaMs { get; set; }
        public double DisagreementDeltaMs { get; set; }
        public double ReductionDeltaMs { get; set; }
        public int DryRunDelta { get; set; }
        public int QuarantineDelta { get; set; }
        public int PredictionDelta { get; set; }
        public int CorrectionCountDelta { get; set; }
        public List<TimingDecisionMismatch> Divergences { get; set; } = new();
    }

    public sealed class TimingDecisionMismatch
    {
        public int Index { get; set; }
        public TimingDecisionTrace? Left { get; set; }
        public TimingDecisionTrace? Right { get; set; }
    }
}
