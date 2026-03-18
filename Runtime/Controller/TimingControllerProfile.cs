using System;

namespace Tsunippy.Runtime.Controller
{
    public sealed class TimingControllerProfile
    {
        public string Name { get; set; } = "captured";
        public TimingControllerStrategy Strategy { get; set; } = TimingControllerStrategy.ConfidenceAdaptive;
        public bool EnableCastLockPrediction { get; set; } = true;
        public bool EnableDryRun { get; set; }
        public bool LearnAnimationLocks { get; set; } = true;
        public bool LearnCastTax { get; set; } = true;
        public float JKAlpha { get; set; } = 0.125f;
        public float JKBeta { get; set; } = 0.25f;
        public float JKK { get; set; } = 2.0f;
        public float DynamicFloorScaling { get; set; } = 0.85f;
        public int DynamicFloorWindow { get; set; } = 100;
        public float DefaultActionLock { get; set; } = TimingMath.DefaultActionAnimationLock;
        public float DefaultCasterTax { get; set; } = 0.1f;
        public float ExistingActionLockThreshold { get; set; } = TimingMath.DefaultActionAnimationLock + TimingMath.LockEqualityEpsilon;
        public float ExistingCastLockThreshold { get; set; } = TimingMath.LockEqualityEpsilon;
        public float MinimumPredictionConfidence { get; set; } = 0.15f;
        public float CastCompletionWindow { get; set; } = TimingMath.CastCompletionWindow;

        public TimingControllerProfile Clone()
            => new()
            {
                Name = Name,
                Strategy = Strategy,
                EnableCastLockPrediction = EnableCastLockPrediction,
                EnableDryRun = EnableDryRun,
                LearnAnimationLocks = LearnAnimationLocks,
                LearnCastTax = LearnCastTax,
                JKAlpha = JKAlpha,
                JKBeta = JKBeta,
                JKK = JKK,
                DynamicFloorScaling = DynamicFloorScaling,
                DynamicFloorWindow = DynamicFloorWindow,
                DefaultActionLock = DefaultActionLock,
                DefaultCasterTax = DefaultCasterTax,
                ExistingActionLockThreshold = ExistingActionLockThreshold,
                ExistingCastLockThreshold = ExistingCastLockThreshold,
                MinimumPredictionConfidence = MinimumPredictionConfidence,
                CastCompletionWindow = CastCompletionWindow,
            };

        public static TimingControllerProfile CreateFrontierDefault()
            => new()
            {
                Name = "frontier",
                Strategy = TimingControllerStrategy.ConfidenceAdaptive,
            };

        public static TimingControllerProfile CreateBaseline()
            => new()
            {
                Name = "baseline",
                Strategy = TimingControllerStrategy.VarianceOnly,
                EnableCastLockPrediction = true,
                EnableDryRun = false,
                LearnAnimationLocks = true,
                LearnCastTax = true,
                JKAlpha = 0.125f,
                JKBeta = 0.25f,
                JKK = 2.5f,
                DynamicFloorScaling = 0.9f,
                DynamicFloorWindow = 120,
                DefaultActionLock = TimingMath.DefaultActionAnimationLock,
                DefaultCasterTax = 0.1f,
                ExistingActionLockThreshold = TimingMath.DefaultActionAnimationLock + TimingMath.LockEqualityEpsilon,
                ExistingCastLockThreshold = TimingMath.LockEqualityEpsilon,
                MinimumPredictionConfidence = 0.25f,
                CastCompletionWindow = TimingMath.CastCompletionWindow,
            };

        public override string ToString() => $"{Name} ({Strategy})";
    }
}
