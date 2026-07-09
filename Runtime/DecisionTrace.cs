using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Tsunippy.Runtime;

namespace Tsunippy.Runtime
{
    public enum DecisionOwnership
    {
        Unknown,
        PreAppliedPendingLock,
        AcceptedServerReconciliation,
        RejectedNoCompensation,
        CastPrediction,
        LegacyReceive,
    }

    public sealed class DecisionTrace
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public ulong Epoch { get; init; }
        public ushort Sequence { get; init; }
        public uint ActionId { get; init; }
        public string ActionName { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public DecisionOwnership Ownership { get; init; } = DecisionOwnership.Unknown;
        public bool IsPvP { get; init; }
        public float BaseLock { get; init; }
        public float ObservedLock { get; init; }
        public float PredictedLock { get; init; }
        public float FinalAppliedLock { get; init; }
        public float ExistingLockBeforeWrite { get; init; }
        public float Correction { get; init; }
        public float RttSample { get; init; }
        public float SmoothedRtt { get; init; }
        public float RttVariance { get; init; }
        public float DynamicFloor { get; init; }
        public float VarianceBuffer { get; init; }
        public float PacketWeight { get; init; }
        public bool HasFormula { get; init; }
        public string Profile { get; init; } = string.Empty;
        public string ConnectionState { get; init; } = string.Empty;
        public string EstimatorMaturity { get; init; } = string.Empty;
        public float LockDbConfidence { get; init; }
        public float CastTaxConfidence { get; init; }
        public string DecisionReason { get; init; } = string.Empty;
        public string RejectionReason { get; init; } = string.Empty;
    }

    public sealed class ReplayLog
    {
        private readonly DecisionTrace[] records;
        private int head;
        private int count;

        public ReplayLog(int capacity = 512)
        {
            records = new DecisionTrace[Math.Clamp(capacity, 32, 4096)];
        }

        public int Count => count;
        public DecisionTrace Last { get; private set; }

        public void Add(DecisionTrace trace)
        {
            records[head] = trace;
            head = (head + 1) % records.Length;
            if (count < records.Length)
                count++;
            Last = trace;
        }

        public IReadOnlyList<DecisionTrace> Snapshot()
        {
            var snapshot = new List<DecisionTrace>(count);
            var start = (head - count + records.Length) % records.Length;
            for (var i = 0; i < count; i++)
            {
                var record = records[(start + i) % records.Length];
                if (record != null)
                    snapshot.Add(record);
            }

            return snapshot;
        }

        public bool TryFindRecentBySequence(ushort sequence, TimeSpan maxAge, out DecisionTrace trace)
            => TryFindRecentBySequence(sequence, maxAge, _ => true, out trace);

        public bool TryFindRecentBySequence(ushort sequence, TimeSpan maxAge, Func<DecisionTrace, bool> filter, out DecisionTrace trace)
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < count; i++)
            {
                var index = (head - 1 - i + records.Length) % records.Length;
                var record = records[index];
                if (record == null)
                    continue;

                if (now - record.Timestamp > maxAge)
                    break;

                if (record.Sequence == sequence && filter(record))
                {
                    trace = record;
                    return true;
                }
            }

            trace = null;
            return false;
        }

        public string ExportJson(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"tsunippy-decisions-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot(), options));
            return path;
        }

        public string ExportCsv(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"tsunippy-decisions-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine("timestamp,epoch,sequence,actionId,source,ownership,isPvP,baseLock,observedLock,predictedLock,finalAppliedLock,existingLockBeforeWrite,correction,rttSample,srtt,rttvar,dynamicFloor,varianceBuffer,packetWeight,hasFormula,profile,connectionState,estimatorMaturity,lockDbConfidence,castTaxConfidence,decisionReason,rejectionReason");

            foreach (var record in Snapshot())
            {
                writer.WriteLine(string.Join(",",
                    Escape(record.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    record.Epoch.ToString(CultureInfo.InvariantCulture),
                    record.Sequence.ToString(CultureInfo.InvariantCulture),
                    record.ActionId.ToString(CultureInfo.InvariantCulture),
                    Escape(record.Source),
                    Escape(record.Ownership.ToString()),
                    record.IsPvP ? "true" : "false",
                    F(record.BaseLock),
                    F(record.ObservedLock),
                    F(record.PredictedLock),
                    F(record.FinalAppliedLock),
                    F(record.ExistingLockBeforeWrite),
                    F(record.Correction),
                    F(record.RttSample),
                    F(record.SmoothedRtt),
                    F(record.RttVariance),
                    F(record.DynamicFloor),
                    F(record.VarianceBuffer),
                    F(record.PacketWeight),
                    record.HasFormula ? "true" : "false",
                    Escape(record.Profile),
                    Escape(record.ConnectionState),
                    Escape(record.EstimatorMaturity),
                    F(record.LockDbConfidence),
                    F(record.CastTaxConfidence),
                    Escape(record.DecisionReason),
                    Escape(record.RejectionReason)));
            }

            return path;
        }

        private static string F(float value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

        private static string Escape(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? value
                : $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
