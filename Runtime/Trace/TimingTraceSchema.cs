using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Tsunippy.Database;
using Tsunippy.Runtime.Controller;

namespace Tsunippy.Runtime.Trace
{
    public static class TimingTraceSchema
    {
        public const int CurrentVersion = 3;
    }

    public enum TimingTraceScenarioBucket : byte
    {
        Unspecified = 0,
        InstantBaseline = 1,
        CastBaseline = 2,
        ConflictRecovery = 3,
        MessyGameplay = 4,
    }

    public sealed class TimingTraceDocument
    {
        public int SchemaVersion { get; set; } = TimingTraceSchema.CurrentVersion;
        public TimingTraceMetadata Metadata { get; set; } = new();
        public TimingControllerProfile CapturedProfile { get; set; } = TimingControllerProfile.CreateFrontierDefault();
        public TimingKnowledgeSnapshot CapturedKnowledge { get; set; } = new();
        public List<TimingTraceEvent> Events { get; set; } = new();
        public List<TimingDecisionTrace> ObservedDecisions { get; set; } = new();
    }

    public sealed class TimingTraceMetadata
    {
        public string TraceId { get; set; } = Guid.NewGuid().ToString("N");
        public string CorpusTraceId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Source { get; set; } = "live-capture";
        public string PluginVersion { get; set; } = string.Empty;
        public TimingTraceScenarioBucket ScenarioBucket { get; set; } = TimingTraceScenarioBucket.Unspecified;
        public string Purpose { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class TimingKnowledgeSnapshot
    {
        public List<TimingKnowledgeEntrySnapshot> ActionLocks { get; set; } = new();
        public List<TimingKnowledgeEntrySnapshot> CastTaxes { get; set; } = new();

        public static TimingKnowledgeSnapshot FromDatabases(LockDatabase lockDb, CastTaxDatabase castDb)
        {
            var snapshot = new TimingKnowledgeSnapshot();
            if (lockDb != null)
                snapshot.ActionLocks.AddRange(lockDb.Entries.Select(entry => ToSnapshot(entry.Key, entry.Value)));
            if (castDb != null)
                snapshot.CastTaxes.AddRange(castDb.Entries.Select(entry => ToSnapshot(entry.Key, entry.Value)));
            return snapshot;
        }

        public LockDatabase CreateLockDatabase()
        {
            var database = new LockDatabase();
            foreach (var entry in ActionLocks)
                database.Entries[MakeKey(entry.ActionId, entry.Context)] = entry.ToLockEntry();
            return database;
        }

        public CastTaxDatabase CreateCastTaxDatabase()
        {
            var database = new CastTaxDatabase();
            foreach (var entry in CastTaxes)
                database.Entries[MakeKey(entry.ActionId, entry.Context)] = entry.ToLockEntry();
            return database;
        }

        private static TimingKnowledgeEntrySnapshot ToSnapshot(string key, LockEntry entry)
        {
            var parts = key.Split('_');
            var actionId = parts.Length > 0 && uint.TryParse(parts[0], out var parsedActionId) ? parsedActionId : 0;
            var context = parts.Length > 1 && byte.TryParse(parts[1], out var parsedContext)
                ? (GameContext)parsedContext
                : GameContext.PvE;

            return new TimingKnowledgeEntrySnapshot
            {
                ActionId = actionId,
                Context = context,
                MeanLock = entry.MeanLock,
                MeanDeviation = entry.MeanDeviation,
                SampleCount = entry.SampleCount,
                OutlierStreak = entry.OutlierStreak,
                LastObservedUnix = entry.LastObservedUnix,
            };
        }

        private static string MakeKey(uint actionId, GameContext context) => $"{actionId}_{(byte)context}";
    }

    public sealed class TimingKnowledgeEntrySnapshot
    {
        public uint ActionId { get; set; }
        public GameContext Context { get; set; }
        public float MeanLock { get; set; }
        public float MeanDeviation { get; set; }
        public int SampleCount { get; set; }
        public int OutlierStreak { get; set; }
        public long LastObservedUnix { get; set; }

        public LockEntry ToLockEntry()
            => new()
            {
                MeanLock = MeanLock,
                MeanDeviation = MeanDeviation,
                SampleCount = SampleCount,
                OutlierStreak = OutlierStreak,
                LastObservedUnix = LastObservedUnix,
            };
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(TimingAdvanceTraceEvent), "advance")]
    [JsonDerivedType(typeof(TimingActionRequestTraceEvent), "action-request")]
    [JsonDerivedType(typeof(TimingCastBeginTraceEvent), "cast-begin")]
    [JsonDerivedType(typeof(TimingCastInterruptTraceEvent), "cast-interrupt")]
    [JsonDerivedType(typeof(TimingActionEffectTraceEvent), "action-effect")]
    [JsonDerivedType(typeof(TimingNetworkPacketTraceEvent), "network-packet")]
    [JsonDerivedType(typeof(TimingRuntimeResetTraceEvent), "runtime-reset")]
    [JsonDerivedType(typeof(TimingNoteTraceEvent), "note")]
    public abstract record TimingTraceEvent(double TimelineSeconds);

    public sealed record TimingAdvanceTraceEvent(
        double TimelineSeconds,
        float DeltaSeconds,
        bool InCombat,
        bool BetweenAreas,
        long? LocalPlayerId,
        GameContext Context,
        bool HasActiveCast,
        float CastRemainingSeconds,
        float CurrentAnimationLock)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingActionRequestTraceEvent(
        double TimelineSeconds,
        uint ActionId,
        ushort Sequence,
        GameContext Context,
        TimingActionKind ActionKind,
        float ExistingAnimationLock,
        bool Accepted)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingCastBeginTraceEvent(
        double TimelineSeconds,
        uint ActionId,
        ushort Sequence,
        GameContext Context)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingCastInterruptTraceEvent(
        double TimelineSeconds,
        uint ActionId,
        ushort Sequence,
        GameContext Context)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingActionEffectTraceEvent(
        double TimelineSeconds,
        uint ActionId,
        ushort Sequence,
        GameContext Context,
        float OldLock,
        float NewLock,
        float HeaderAnimationLock)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingNetworkPacketTraceEvent(
        double TimelineSeconds,
        TimingPacketClass PacketClass)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingRuntimeResetTraceEvent(
        double TimelineSeconds,
        RuntimeResetReason Reason,
        TimingResetSemantics Semantics,
        string Note)
        : TimingTraceEvent(TimelineSeconds);

    public sealed record TimingNoteTraceEvent(
        double TimelineSeconds,
        string Note)
        : TimingTraceEvent(TimelineSeconds);
}
