namespace Tsunippy.Runtime.Controller
{
    public readonly record struct TimingControllerEventResult(
        TimingDecisionTrace? Decision,
        bool ShouldWriteAnimationLock,
        float AnimationLockToWrite,
        bool LearnedDataChanged,
        bool RuntimeStatsChanged,
        double AnimationLockReductionAdded,
        ulong ActionsReducedAdded,
        bool EnteredConflictQuarantine,
        bool EnteredFailureQuarantine,
        string UserMessage)
    {
        public static TimingControllerEventResult None => new(null, false, 0f, false, false, 0d, 0ul, false, false, string.Empty);
    }
}
