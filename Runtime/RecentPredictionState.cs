namespace Tsunippy.Runtime
{
    public sealed class RecentPredictionState
    {
        public ushort Sequence { get; init; }
        public uint ActionId { get; init; }
        public float PredictedLock { get; init; }
        public long CreatedTick { get; init; }
        public ulong ModelEpoch { get; init; }
        public bool IsPendingForSequence { get; init; }
        public string State { get; init; } = "unknown";
        public DecisionOwnership Ownership { get; init; } = DecisionOwnership.Unknown;

        public long AgeMilliseconds(long nowTick)
            => CreatedTick > 0 ? nowTick - CreatedTick : long.MaxValue;
    }
}
