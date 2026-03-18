using System;

namespace Tsunippy.Runtime
{
    public sealed class DecisionJournal
    {
        private readonly TimingDecisionTrace[] entries;
        private int nextIndex;
        private int count;

        public DecisionJournal(int capacity)
        {
            entries = new TimingDecisionTrace[Math.Max(capacity, 1)];
        }

        public int Count => count;

        public void Add(in TimingDecisionTrace entry)
        {
            entries[nextIndex] = entry;
            nextIndex = (nextIndex + 1) % entries.Length;
            count = Math.Min(count + 1, entries.Length);
        }

        public TimingDecisionTrace[] SnapshotNewestFirst()
        {
            var snapshot = new TimingDecisionTrace[count];
            for (int i = 0; i < count; i++)
            {
                var index = (nextIndex - 1 - i + entries.Length) % entries.Length;
                snapshot[i] = entries[index];
            }

            return snapshot;
        }

        public void Clear()
        {
            Array.Clear(entries);
            nextIndex = 0;
            count = 0;
        }
    }
}
