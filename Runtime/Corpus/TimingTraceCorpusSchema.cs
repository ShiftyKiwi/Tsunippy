using System;
using System.Collections.Generic;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Corpus
{
    public static class TimingTraceCorpusSchema
    {
        public const int CurrentVersion = 1;
        public const string DefaultManifestFileName = "timing-corpus.json";
    }

    public enum TimingTraceEquivalenceExpectation : byte
    {
        Strict = 0,
        Approximate = 1,
    }

    public sealed class TimingTraceCorpusDocument
    {
        public int SchemaVersion { get; set; } = TimingTraceCorpusSchema.CurrentVersion;
        public string CorpusId { get; set; } = "first-real-trace-corpus";
        public string Name { get; set; } = "First Real Trace Corpus";
        public string Description { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<TimingTraceCorpusEntry> Entries { get; set; } = new();
    }

    public sealed class TimingTraceCorpusEntry
    {
        public string TraceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TimingTraceScenarioBucket ScenarioBucket { get; set; } = TimingTraceScenarioBucket.Unspecified;
        public string RelativeTracePath { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string ExpectedBehavior { get; set; } = string.Empty;
        public TimingTraceEquivalenceExpectation EquivalenceExpectation { get; set; } = TimingTraceEquivalenceExpectation.Strict;
        public bool DivergenceExpected { get; set; }
        public bool IsGold { get; set; }
        public List<string> Tags { get; set; } = new();
        public string Notes { get; set; } = string.Empty;
        public DateTime? LastCapturedUtc { get; set; }
        public string LastCapturedPluginVersion { get; set; } = string.Empty;
    }
}
