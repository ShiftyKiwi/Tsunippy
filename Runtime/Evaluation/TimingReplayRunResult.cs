using System.Collections.Generic;

namespace Tsunippy.Runtime.Evaluation
{
    public sealed class TimingReplayRunResult
    {
        public string ProfileName { get; set; } = string.Empty;
        public int EventCount { get; set; }
        public int DecisionCount { get; set; }
        public int PredictionCount { get; set; }
        public int CorrectionCount { get; set; }
        public int ResetCount { get; set; }
        public int QuarantineCount { get; set; }
        public int DryRunCount { get; set; }
        public int InstantDecisionCount { get; set; }
        public int CastDecisionCount { get; set; }
        public int OvershootCount { get; set; }
        public int UndershootCount { get; set; }
        public int NeutralCount { get; set; }
        public double AverageAbsoluteCorrectionMs { get; set; }
        public double AverageAbsoluteDisagreementMs { get; set; }
        public double AverageReductionMs { get; set; }
        public double AverageConfidence { get; set; }
        public string DecisionFingerprint { get; set; } = string.Empty;
        public Dictionary<string, int> ReasonCounts { get; set; } = new();
        public Dictionary<string, int> SourceCounts { get; set; } = new();
        public Dictionary<string, int> ModeCounts { get; set; } = new();
        public Dictionary<string, int> QualityCounts { get; set; } = new();
        public List<TimingDecisionTrace> Decisions { get; set; } = new();
    }
}
