using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Tsunippy.Runtime
{
    public readonly struct RecentIssuedAction
    {
        public ushort Sequence { get; init; }
        public uint ActionId { get; init; }
        public uint OriginalActionId { get; init; }
        public string Source { get; init; }
        public ActionType ActionType { get; init; }
        public long CreatedTick { get; init; }
        public ulong ModelEpoch { get; init; }
        public bool IsGcdRelevant { get; init; }

        public bool IsValid => ActionId != 0 && CreatedTick > 0;

        public long AgeMilliseconds(long nowTick)
            => CreatedTick > 0 ? nowTick - CreatedTick : long.MaxValue;
    }

    public sealed class RecentIssuedActionTracker
    {
        private readonly RecentIssuedAction[] records;
        private int head;
        private int count;

        public RecentIssuedActionTracker(int capacity = 96)
        {
            records = new RecentIssuedAction[Math.Clamp(capacity, 16, 512)];
        }

        public void Record(RecentIssuedAction action)
        {
            if (!action.IsValid)
                return;

            records[head] = action;
            head = (head + 1) % records.Length;
            if (count < records.Length)
                count++;
        }

        public void Clear()
        {
            Array.Clear(records, 0, records.Length);
            head = 0;
            count = 0;
        }

        public bool TryFindBySequence(ushort sequence, TimeSpan maxAge, out RecentIssuedAction action)
        {
            var now = Environment.TickCount64;
            var maxAgeMilliseconds = maxAge.TotalMilliseconds;
            for (var i = 0; i < count; i++)
            {
                var index = (head - 1 - i + records.Length) % records.Length;
                var record = records[index];
                if (!record.IsValid)
                    continue;

                if (record.AgeMilliseconds(now) > maxAgeMilliseconds)
                    break;

                if (record.Sequence == sequence)
                {
                    action = record;
                    return true;
                }
            }

            action = default;
            return false;
        }

        public bool TryFindNearNow(TimeSpan maxAge, out RecentIssuedAction action)
        {
            var now = Environment.TickCount64;
            var maxAgeMilliseconds = maxAge.TotalMilliseconds;
            for (var i = 0; i < count; i++)
            {
                var index = (head - 1 - i + records.Length) % records.Length;
                var record = records[index];
                if (!record.IsValid)
                    continue;

                if (record.AgeMilliseconds(now) > maxAgeMilliseconds)
                    break;

                action = record;
                return true;
            }

            action = default;
            return false;
        }
    }
}
