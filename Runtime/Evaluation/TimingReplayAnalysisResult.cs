namespace Tsunippy.Runtime.Evaluation
{
    public sealed class TimingReplayAnalysisResult
    {
        public TimingReplayRunResult Replay { get; set; } = new();
        public TimingReplayEquivalenceReport Equivalence { get; set; }
    }
}
