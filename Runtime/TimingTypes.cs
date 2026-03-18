using System;

namespace Tsunippy.Runtime
{
    public enum TimingRuntimeMode
    {
        Active = 0,
        DryRunRequested = 1,
        ConflictQuarantined = 2,
        FailureQuarantined = 3,
    }

    public enum TimingActionKind
    {
        Instant = 0,
        Cast = 1,
    }

    public enum CastLifecycleStage
    {
        Idle = 0,
        Casting = 1,
        Predicted = 2,
    }

    public enum TimingDecisionSource
    {
        InstantPrediction = 0,
        CastPrediction = 1,
        ServerCorrection = 2,
        RuntimeControl = 3,
    }

    public enum TimingDecisionReason
    {
        PredictedInstantLock = 0,
        PredictedCastLock = 1,
        AppliedInstantCorrection = 2,
        AppliedCastCorrection = 3,
        DryRunSuppressed = 4,
        ExistingLockSuppressed = 5,
        RttBelowFloor = 6,
        NoCorrelation = 7,
        ResponseMismatch = 8,
        ConflictDetected = 9,
        RuntimeFailure = 10,
        RuntimeReset = 11,
        InvalidMeasurement = 12,
        CastInterrupted = 13,
        LearningOnly = 14,
    }

    public enum TimingQuality
    {
        Unknown = 0,
        Learning = 1,
        Stable = 2,
        Adaptive = 3,
        Volatile = 4,
        Quarantined = 5,
    }

    public enum RuntimeResetReason
    {
        Enable = 0,
        Disable = 1,
        Manual = 2,
        ZoneTransition = 3,
        PlayerChanged = 4,
        RuntimeFailure = 5,
        ConflictRecovery = 6,
        ModuleStateChange = 7,
        ConflictDetected = 8,
    }

    public readonly record struct TimingDecisionTrace(
        DateTime TimestampUtc,
        TimingDecisionSource Source,
        TimingDecisionReason Reason,
        TimingRuntimeMode Mode,
        TimingQuality Quality,
        TimingActionKind ActionKind,
        uint ActionId,
        ushort Sequence,
        float PredictedLock,
        float ServerLock,
        float FinalLock,
        float RTT,
        float PredictionConfidence,
        string Note);

    public static class TimingMath
    {
        public const float LockEqualityEpsilon = 0.0005f;
        public const float MinimumMeasuredRtt = 0.001f;
        public const float MaximumMeasuredRtt = 2.5f;
        public const float MaximumReasonableLock = 10f;
        public const float CastCompletionWindow = 0.05f;

        public static bool NearlyEqual(float a, float b, float epsilon = LockEqualityEpsilon)
            => Math.Abs(a - b) <= epsilon;

        public static bool IsFiniteAndInRange(float value, float minInclusive, float maxInclusive)
            => float.IsFinite(value) && value >= minInclusive && value <= maxInclusive;
    }
}
