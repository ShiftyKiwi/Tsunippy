using System;
using Tsunippy.Runtime;

namespace Tsunippy.RTT
{
    /// <summary>
    /// Tracks the minimum observed RTT over a sliding window to compute a dynamic floor.
    ///
    /// Replaces NoClippy's hardcoded simulatedRTT = 0.04f (40ms).
    ///
    /// The floor = MinRTT * ScalingFactor, which adapts to:
    /// - Different datacenters (NA vs EU vs JP)
    /// - Time of day (off-peak vs peak server load)
    /// - Instance vs overworld server processing differences
    ///
    /// A player with 20ms min RTT to a fast datacenter gets floor ~17ms instead of 40ms.
    /// Falls back to 0.04f until sufficient samples are collected.
    /// </summary>
    public class DynamicFloor
    {
        private readonly float[] samples;
        private readonly float[] scratch;
        private int head = 0;
        private int count = 0;
        private float cachedMin = float.MaxValue;
        private float cachedLowPercentile = float.MaxValue;
        private float cachedFloor = DefaultFloor;
        private bool dirty = true;
        private FloorMode mode = FloorMode.Balanced;
        private ConnectionState connectionState = ConnectionState.WarmingUp;

        /// <summary>
        /// Scaling factor applied to MinRTT to compute the floor.
        /// 0.85 means floor = 85% of the lowest observed RTT.
        /// This provides a small safety margin below the absolute minimum.
        /// </summary>
        public float ScalingFactor { get; set; } = 0.85f;
        public FloorMode Mode
        {
            get => mode;
            set
            {
                if (mode == value) return;
                mode = value;
                dirty = true;
            }
        }

        public ConnectionState ConnectionState
        {
            get => connectionState;
            set
            {
                if (connectionState == value) return;
                connectionState = value;
                dirty = true;
            }
        }
        public string LastAdjustmentReason { get; private set; } = "default floor";

        /// <summary>The size of the sliding window (number of RTT samples retained).</summary>
        public int WindowSize => samples.Length;

        /// <summary>The default floor used before sufficient data is collected.</summary>
        public const float DefaultFloor = 0.04f;

        /// <summary>Minimum allowed floor to prevent unreasonably aggressive values.</summary>
        public const float MinimumFloor = 0.01f;

        /// <summary>Maximum allowed floor. High-latency paths can rise above NoClippy's 40ms floor, but stay bounded.</summary>
        public const float MaximumFloor = 0.12f;

        /// <summary>
        /// Create a new DynamicFloor tracker.
        /// </summary>
        /// <param name="windowSize">Number of RTT samples to retain. Default 100 gives
        /// a good balance between adaptiveness and stability.</param>
        public DynamicFloor(int windowSize = 100)
        {
            samples = new float[Math.Max(windowSize, 10)];
            scratch = new float[samples.Length];
        }

        /// <summary>Add a new RTT sample to the sliding window.</summary>
        public void AddSample(float rtt)
        {
            if (rtt <= 0 || !float.IsFinite(rtt)) return;

            samples[head] = rtt;
            head = (head + 1) % samples.Length;
            if (count < samples.Length) count++;
            dirty = true;
        }

        /// <summary>The minimum RTT observed in the current sliding window.</summary>
        public float MinRTT
        {
            get
            {
                if (!dirty) return cachedMin;
                Recompute();
                return cachedMin;
            }
        }

        public float RawMinRTT => HasSufficientData ? MinRTT : 0f;
        public float EffectiveFloor => Floor;

        /// <summary>
        /// The computed dynamic floor: MinRTT * ScalingFactor.
        /// Falls back to DefaultFloor (40ms) if insufficient data.
        /// Clamped to MinimumFloor (10ms) to prevent dangerously low values.
        /// </summary>
        public float Floor
        {
            get
            {
                if (!HasSufficientData)
                {
                    cachedFloor = DefaultFloor;
                    LastAdjustmentReason = "warming up";
                    return DefaultFloor;
                }

                if (dirty)
                    Recompute();

                return cachedFloor;
            }
        }

        /// <summary>Whether enough samples have been collected for reliable floor estimation.</summary>
        public bool HasSufficientData => count >= 5;

        /// <summary>Number of samples currently in the window.</summary>
        public int CurrentSampleCount => count;

        /// <summary>Reset the tracker, clearing all samples.</summary>
        public void Reset()
        {
            head = 0;
            count = 0;
            cachedMin = float.MaxValue;
            cachedLowPercentile = float.MaxValue;
            cachedFloor = DefaultFloor;
            dirty = true;
            LastAdjustmentReason = "reset";
        }

        private void Recompute()
        {
            cachedMin = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var sample = samples[i];
                scratch[i] = sample;
                if (sample < cachedMin)
                    cachedMin = sample;
            }

            Array.Sort(scratch, 0, count);
            var percentileIndex = Math.Clamp((int)MathF.Round((count - 1) * GetPercentile()), 0, count - 1);
            cachedLowPercentile = scratch[percentileIndex];

            var effectiveMode = Mode == FloorMode.Auto ? GetAutoMode() : Mode;
            var previous = cachedFloor;
            var candidate = effectiveMode switch
            {
                FloorMode.Aggressive => cachedMin * ScalingFactor,
                FloorMode.Safe => Math.Max(cachedMin * 0.95f, cachedLowPercentile * 0.92f),
                _ => Math.Max(cachedMin * ScalingFactor, cachedLowPercentile * 0.82f),
            };

            cachedFloor = Math.Clamp(candidate, MinimumFloor, MaximumFloor);
            LastAdjustmentReason = Math.Abs(cachedFloor - previous) switch
            {
                < 0.001f => $"{effectiveMode}: unchanged",
                _ when cachedFloor > previous => $"{effectiveMode}: sustained RTT floor rose",
                _ => $"{effectiveMode}: lower bound improved",
            };

            dirty = false;
        }

        private float GetPercentile()
        {
            var effectiveMode = Mode == FloorMode.Auto ? GetAutoMode() : Mode;
            return effectiveMode switch
            {
                FloorMode.Aggressive => 0.05f,
                FloorMode.Safe => 0.30f,
                _ => 0.20f,
            };
        }

        private FloorMode GetAutoMode()
        {
            return ConnectionState switch
            {
                ConnectionState.WarmingUp or ConnectionState.Bursty or ConnectionState.PathShifted => FloorMode.Safe,
                ConnectionState.Stable => FloorMode.Balanced,
                _ => FloorMode.Balanced,
            };
        }
    }
}
