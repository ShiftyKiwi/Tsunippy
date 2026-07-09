using System;

namespace Tsunippy.Runtime
{
    public enum TsunippyProfile
    {
        Balanced = 0,
        Safe = 1,
        Aggressive = 2,
        Auto = 3,
    }

    public enum FloorMode
    {
        Aggressive = 0,
        Balanced = 1,
        Safe = 2,
        Auto = 3,
    }

    public enum ConnectionState
    {
        WarmingUp = 0,
        Stable = 1,
        MildlyJittery = 2,
        Bursty = 3,
        PathShifted = 4,
    }

    public readonly record struct ProfileSettings(
        TsunippyProfile EffectiveProfile,
        FloorMode FloorMode,
        float VarianceMultiplier,
        float PredictionAggressiveness,
        float PacketSpikeStrictness,
        float OutlierSensitivity,
        float StalePredictionTtlSeconds,
        float CastSafetyMarginSeconds,
        bool ConservativeWarmup)
    {
        public static ProfileSettings From(TsunippyProfile configuredProfile, ConnectionState connectionState)
        {
            var effectiveProfile = configuredProfile;
            if (configuredProfile == TsunippyProfile.Auto)
            {
                effectiveProfile = connectionState switch
                {
                    ConnectionState.WarmingUp or ConnectionState.PathShifted or ConnectionState.Bursty => TsunippyProfile.Safe,
                    ConnectionState.MildlyJittery => TsunippyProfile.Balanced,
                    _ => TsunippyProfile.Balanced,
                };
            }

            return effectiveProfile switch
            {
                TsunippyProfile.Safe => new ProfileSettings(
                    effectiveProfile,
                    configuredProfile == TsunippyProfile.Auto ? FloorMode.Auto : FloorMode.Safe,
                    1.35f,
                    0.70f,
                    0.75f,
                    0.75f,
                    1.25f,
                    0.030f,
                    true),

                TsunippyProfile.Aggressive => new ProfileSettings(
                    effectiveProfile,
                    FloorMode.Aggressive,
                    0.75f,
                    1.0f,
                    1.15f,
                    1.35f,
                    0.75f,
                    0.015f,
                    false),

                _ => new ProfileSettings(
                    effectiveProfile,
                    configuredProfile == TsunippyProfile.Auto ? FloorMode.Auto : FloorMode.Balanced,
                    1.0f,
                    0.88f,
                    1.0f,
                    1.0f,
                    1.0f,
                    0.020f,
                    true),
            };
        }

        public float PredictionTtlMilliseconds => Math.Clamp(StalePredictionTtlSeconds, 0.25f, 3.0f) * 1000f;
    }
}
