namespace Tsunippy.Runtime
{
    public sealed class PendingPrediction
    {
        public ushort Sequence { get; init; }
        public uint ActionId { get; init; }
        public bool IsPvP { get; init; }
        public float BaseLock { get; init; }
        public float PredictedLock { get; init; }
        public float OriginalLockAtPrediction { get; init; }
        public long CreatedTick { get; init; }
        public long ExpiresTick { get; init; }
        public ulong ModelEpoch { get; init; }
        public string Source { get; init; } = string.Empty;
    }
}
