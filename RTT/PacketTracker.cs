using System;

namespace Tsunippy.RTT
{
    /// <summary>
    /// Enhanced packet counting with type classification and graduated RTT weight.
    ///
    /// Improvements over NoClippy:
    /// 1. Graduated 4-level weight scale (1.0/0.5/0.25/0.1) instead of binary (1.0/0.1)
    /// 2. Tracks total vs action-related packets separately
    /// 3. More nuanced spike handling for the Jacobson/Karels estimator
    ///
    /// The rolling window is 5 slots x 10ms = 50ms, same as NoClippy.
    /// </summary>
    public class PacketTracker
    {
        // Rolling window: 5 slots x 10ms = 50ms
        private const int SlotCount = 5;
        private const float SlotDuration = 0.01f; // 10ms per slot
        private const float DefaultActionWindow = 0.12f;
        private const int DefaultActionBudget = 2;

        private readonly int[] totalPackets = new int[SlotCount];
        private readonly int[] actionPackets = new int[SlotCount];
        private int currentIndex = 0;
        private float timer = 0f;
        private float actionWindowRemaining = 0f;
        private int actionPacketBudget = 0;

        /// <summary>Total packets sent in the last 50ms window.</summary>
        public int TotalPacketsSent
        {
            get
            {
                int s = 0;
                for (int i = 0; i < SlotCount; i++) s += totalPackets[i];
                return s;
            }
        }

        /// <summary>Packets likely related to actions in the last 50ms window.</summary>
        public int ActionPacketsSent
        {
            get
            {
                int s = 0;
                for (int i = 0; i < SlotCount; i++) s += actionPackets[i];
                return s;
            }
        }

        /// <summary>
        /// Record an outgoing packet.
        /// </summary>
        /// <param name="packet">Pointer to the packet data (for future type classification).</param>
        public unsafe void RecordPacket(nint packet)
        {
            totalPackets[currentIndex]++;

            if (actionWindowRemaining <= 0 || actionPacketBudget <= 0)
                return;

            actionPackets[currentIndex]++;
            actionPacketBudget--;
        }

        /// <summary>
        /// Mark the next short burst of outgoing packets as action-related.
        /// This is a heuristic window rather than a fragile packet parser.
        /// </summary>
        public void MarkActionIssued(int budget = DefaultActionBudget, float windowSeconds = DefaultActionWindow)
        {
            actionPacketBudget = Math.Max(actionPacketBudget, budget);
            actionWindowRemaining = Math.Max(actionWindowRemaining, windowSeconds);
        }

        /// <summary>
        /// Advance the rolling window based on elapsed time.
        /// Should be called every frame from the Update handler.
        /// </summary>
        /// <param name="deltaTime">Frame delta time in seconds.</param>
        public void Update(float deltaTime)
        {
            timer += deltaTime;
            actionWindowRemaining = Math.Max(actionWindowRemaining - deltaTime, 0f);
            while (timer >= SlotDuration)
            {
                timer -= SlotDuration;
                currentIndex = (currentIndex + 1) % SlotCount;
                totalPackets[currentIndex] = 0;
                actionPackets[currentIndex] = 0;
            }
        }

        /// <summary>
        /// Returns RTT weight for the Jacobson/Karels estimator.
        ///
        /// Multiple simultaneous packets cause server tick queuing delays (~50ms each).
        /// These spikes are not representative of true network conditions, so we dampen
        /// the RTT sample weight proportionally.
        ///
        /// Graduated scale (improvement over NoClippy's binary 0.1/1.0):
        ///   1 packet  -> 1.0  (clean, full trust)
        ///   2 packets -> 0.5  (moderate dampening)
        ///   3 packets -> 0.25 (heavy dampening)
        ///   4+ packets -> 0.1 (burst, minimal trust)
        /// </summary>
        public float GetRTTWeight()
        {
            var sent = ActionPacketsSent > 0 ? ActionPacketsSent : TotalPacketsSent;
            return sent switch
            {
                <= 1 => 1.0f,
                2 => 0.5f,
                3 => 0.25f,
                _ => 0.1f
            };
        }

        /// <summary>Reset the tracker, clearing all counters.</summary>
        public void Reset()
        {
            Array.Clear(totalPackets);
            Array.Clear(actionPackets);
            currentIndex = 0;
            timer = 0f;
            actionWindowRemaining = 0f;
            actionPacketBudget = 0;
        }
    }
}
