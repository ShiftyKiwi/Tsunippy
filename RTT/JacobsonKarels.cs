using System;

namespace Tsunippy.RTT
{
    /// <summary>
    /// TCP-style RTT estimator based on the Jacobson/Karels algorithm (RFC 6298).
    /// Separately tracks smoothed RTT and RTT variance for adaptive network buffering.
    ///
    /// Key advantage over NoClippy's simple EWMA:
    /// - Tracks jitter (variance) separately from average RTT
    /// - Tight locks on stable connections, expands during jitter
    /// - Weighted samples allow dampening of multi-packet spike artifacts
    ///
    /// Formula:
    ///   SRTT = (1 - alpha) * SRTT + alpha * sample
    ///   RTTVAR = (1 - beta) * RTTVAR + beta * |sample - SRTT|
    ///   PredictedBuffer = SRTT + K * RTTVAR
    /// </summary>
    public class JacobsonKarels
    {
        // Configurable parameters (RFC 6298 defaults)
        public float Alpha { get; set; } = 0.125f;     // SRTT smoothing factor (1/8)
        public float Beta { get; set; } = 0.25f;        // Variance smoothing factor (1/4)
        public float K { get; set; } = 2.0f;            // Variance multiplier for buffer

        // State
        public float SmoothedRTT { get; private set; } = -1f;
        public float RTTVariance { get; private set; } = 0f;
        public int SampleCount { get; private set; } = 0;
        public int CleanSampleCount { get; private set; } = 0;

        /// <summary>Whether at least one RTT sample has been recorded.</summary>
        public bool IsInitialized => SmoothedRTT > 0;

        /// <summary>Whether enough clean samples have arrived for full variance trust.</summary>
        public bool IsWarm => CleanSampleCount >= 10;

        /// <summary>Human-readable maturity bucket for diagnostics and safe-mode decisions.</summary>
        public string EstimatorMaturity => CleanSampleCount switch
        {
            <= 0 => "cold",
            < 3 => "early",
            < 6 => "warming",
            < 10 => "nearly warm",
            _ => "warm",
        };

        /// <summary>
        /// Startup variance can be exaggerated because RFC 6298 initializes RTTVAR at
        /// half the first sample. Trust it gradually so the first few clean samples do
        /// correction work without creating a huge safety buffer.
        /// </summary>
        public float VarianceTrustFactor => CleanSampleCount switch
        {
            <= 0 => 0f,
            < 3 => 0.15f,
            < 6 => 0.45f,
            < 10 => 0.75f,
            _ => 1f,
        };

        /// <summary>
        /// The predicted network buffer: SRTT + K * RTTVAR.
        /// This is the recommended amount of time to buffer for network variability.
        /// On stable connections this is tight (close to SRTT).
        /// On jittery connections this expands proportionally to observed variance.
        /// </summary>
        public float PredictedBuffer => IsInitialized
            ? Math.Max(SmoothedRTT + VarianceBuffer, 0.001f)
            : 0f;

        /// <summary>
        /// The variance component only: K * RTTVAR.
        /// Used as the "network variation" buffer added to lock corrections.
        /// </summary>
        public float VarianceBuffer => IsInitialized
            ? K * RTTVariance * VarianceTrustFactor
            : 0f;

        public float RawVarianceBuffer => IsInitialized
            ? K * RTTVariance
            : 0f;

        /// <summary>
        /// Add a new RTT sample to the estimator.
        /// </summary>
        /// <param name="rttSample">Measured round-trip time in seconds.</param>
        /// <param name="weight">Weight multiplier (0..1). Use less than 1.0 to dampen
        /// samples taken during multi-packet bursts where RTT spikes are expected.
        /// Default 1.0 = full weight.</param>
        public void AddSample(float rttSample, float weight = 1.0f)
        {
            if (rttSample <= 0 || !float.IsFinite(rttSample)) return;

            SampleCount++;
            if (weight >= 0.75f)
                CleanSampleCount++;

            if (!IsInitialized)
            {
                // First sample: initialize directly per RFC 6298
                SmoothedRTT = rttSample;
                RTTVariance = rttSample / 2f;
                return;
            }

            // Apply weight to dampening factors
            // When weight < 1.0, we trust this sample less (multi-packet burst scenario)
            var effectiveAlpha = Alpha * weight;
            var effectiveBeta = Beta * weight;

            // Jacobson/Karels update
            var err = rttSample - SmoothedRTT;
            SmoothedRTT += effectiveAlpha * err;
            RTTVariance = (1 - effectiveBeta) * RTTVariance + effectiveBeta * Math.Abs(err);

            // Safety clamp
            if (SmoothedRTT < 0.001f) SmoothedRTT = 0.001f;
            if (RTTVariance < 0) RTTVariance = 0;
        }

        /// <summary>Reset the estimator to uninitialized state.</summary>
        public void Reset()
        {
            SmoothedRTT = -1f;
            RTTVariance = 0f;
            SampleCount = 0;
            CleanSampleCount = 0;
        }
    }
}
