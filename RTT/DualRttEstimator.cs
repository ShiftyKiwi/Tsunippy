using System;
using Tsunippy.Runtime;

namespace Tsunippy.RTT
{
    public sealed class DualRttEstimator
    {
        private readonly JacobsonKarels fast = new()
        {
            Alpha = 0.35f,
            Beta = 0.35f,
            K = 1.5f,
        };

        private readonly JacobsonKarels slow = new()
        {
            Alpha = 0.08f,
            Beta = 0.12f,
            K = 2.0f,
        };

        private int burstSamples;
        private int pathShiftSamples;

        public ConnectionState Classification { get; private set; } = ConnectionState.WarmingUp;
        public float FastSmoothedRTT => fast.SmoothedRTT;
        public float SlowSmoothedRTT => slow.SmoothedRTT;
        public float FastRTTVariance => fast.RTTVariance;
        public float SlowRTTVariance => slow.RTTVariance;

        public void AddSample(float rttSample, float packetWeight)
        {
            fast.AddSample(rttSample, packetWeight);
            slow.AddSample(rttSample, packetWeight);
            UpdateClassification(packetWeight);
        }

        public void Reset()
        {
            fast.Reset();
            slow.Reset();
            burstSamples = 0;
            pathShiftSamples = 0;
            Classification = ConnectionState.WarmingUp;
        }

        private void UpdateClassification(float packetWeight)
        {
            if (!fast.IsWarm || slow.SampleCount < 8)
            {
                Classification = ConnectionState.WarmingUp;
                return;
            }

            var fastRtt = Math.Max(fast.SmoothedRTT, 0.001f);
            var slowRtt = Math.Max(slow.SmoothedRTT, 0.001f);
            var diff = Math.Abs(fastRtt - slowRtt);
            var jitterRatio = Math.Max(fast.RTTVariance, slow.RTTVariance) / Math.Max(slowRtt, 0.001f);

            burstSamples = packetWeight <= 0.25f || jitterRatio >= 0.35f
                ? Math.Min(burstSamples + 1, 6)
                : Math.Max(burstSamples - 1, 0);

            pathShiftSamples = diff >= Math.Max(0.035f, slowRtt * 0.35f)
                ? Math.Min(pathShiftSamples + 1, 6)
                : Math.Max(pathShiftSamples - 1, 0);

            if (pathShiftSamples >= 3)
                Classification = ConnectionState.PathShifted;
            else if (burstSamples >= 3)
                Classification = ConnectionState.Bursty;
            else if (jitterRatio >= 0.18f || diff >= Math.Max(0.018f, slowRtt * 0.18f))
                Classification = ConnectionState.MildlyJittery;
            else
                Classification = ConnectionState.Stable;
        }
    }
}
