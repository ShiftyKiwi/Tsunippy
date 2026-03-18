using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tsunippy.Runtime.Controller;
using Tsunippy.Runtime.Evaluation;
using Tsunippy.Runtime.Replay;

namespace Tsunippy.Runtime.Corpus
{
    public enum TimingTraceEquivalenceOutcome : byte
    {
        MissingObservedStream = 0,
        StrictEquivalent = 1,
        ApproximateEquivalent = 2,
        Divergent = 3,
    }

    public sealed class TimingTraceCorpusEntryResult
    {
        public TimingTraceCorpusEntry Entry { get; set; }
        public string TracePath { get; set; } = string.Empty;
        public bool TraceExists { get; set; }
        public bool Deterministic { get; set; }
        public TimingTraceEquivalenceOutcome EquivalenceOutcome { get; set; }
        public bool ExpectationSatisfied { get; set; }
        public bool Passed { get; set; }
        public TimingReplayAnalysisResult FirstAnalysis { get; set; }
        public TimingReplayAnalysisResult SecondAnalysis { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public sealed class TimingTraceCorpusValidationResult
    {
        public string CorpusRoot { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public TimingTraceCorpusDocument Corpus { get; set; } = new();
        public List<TimingTraceCorpusEntryResult> Entries { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int MissingTraceCount { get; set; }
        public int GoldTraceCount { get; set; }
        public int GoldFailureCount { get; set; }
        public int PassCount { get; set; }
        public bool Passed { get; set; }
        public bool GoldPassed { get; set; }
    }

    public static class TimingTraceCorpusValidation
    {
        public static TimingTraceCorpusValidationResult Validate(string corpusPathOrManifest, TimingControllerProfile overrideProfile = null)
        {
            var manifestPath = TimingTraceCorpusPaths.ResolveManifestPath(corpusPathOrManifest);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Corpus manifest not found.", manifestPath);

            var corpusRoot = TimingTraceCorpusPaths.ResolveCorpusRoot(manifestPath);
            var corpus = TimingTraceCorpusJson.Load(manifestPath);
            var result = new TimingTraceCorpusValidationResult
            {
                CorpusRoot = corpusRoot,
                ManifestPath = manifestPath,
                Corpus = corpus,
            };

            ValidateManifest(corpus, result.Warnings);

            foreach (var extraTracePath in EnumerateExtraTracePaths(corpusRoot, corpus))
                result.Warnings.Add($"Unmanifested trace file: {Path.GetRelativePath(corpusRoot, extraTracePath)}");

            foreach (var entry in corpus.Entries)
            {
                var tracePath = TimingTraceCorpusPaths.ResolveTracePath(corpusRoot, entry);
                var entryResult = ValidateEntry(entry, tracePath, overrideProfile);
                result.Entries.Add(entryResult);
                if (entry.IsGold)
                    result.GoldTraceCount++;
                if (!entryResult.TraceExists)
                    result.MissingTraceCount++;
                if (entryResult.Passed)
                    result.PassCount++;
                else if (entry.IsGold)
                    result.GoldFailureCount++;
            }

            result.Passed = result.PassCount == result.Entries.Count && result.Warnings.Count == 0;
            result.GoldPassed = result.GoldFailureCount == 0;
            return result;
        }

        private static TimingTraceCorpusEntryResult ValidateEntry(TimingTraceCorpusEntry entry, string tracePath, TimingControllerProfile overrideProfile)
        {
            var result = new TimingTraceCorpusEntryResult
            {
                Entry = entry,
                TracePath = tracePath,
                TraceExists = File.Exists(tracePath),
            };

            if (!result.TraceExists)
            {
                result.EquivalenceOutcome = TimingTraceEquivalenceOutcome.MissingObservedStream;
                result.ExpectationSatisfied = false;
                result.Passed = false;
                result.Notes.Add("Trace file is missing.");
                return result;
            }

            var trace = Trace.TimingTraceJson.Load(tracePath);
            var profile = overrideProfile?.Clone()
                          ?? trace.CapturedProfile?.Clone()
                          ?? TimingControllerProfile.CreateFrontierDefault();

            var first = TimingReplayRunner.Analyze(trace, profile);
            var second = TimingReplayRunner.Analyze(trace, profile);
            var deterministic = string.Equals(first.Replay.DecisionFingerprint, second.Replay.DecisionFingerprint, StringComparison.Ordinal);
            var outcome = DetermineEquivalenceOutcome(first.Equivalence);
            var metadataBound = ValidateTraceMetadataBinding(entry, trace, result.Notes);
            var satisfied = metadataBound && IsExpectationSatisfied(entry, outcome);

            result.FirstAnalysis = first;
            result.SecondAnalysis = second;
            result.Deterministic = deterministic;
            result.EquivalenceOutcome = outcome;
            result.ExpectationSatisfied = satisfied;
            result.Passed = deterministic && satisfied;

            if (!deterministic)
                result.Notes.Add("Replay fingerprint changed between repeated runs.");
            if (first.Equivalence == null)
                result.Notes.Add("Trace has no observed-decision stream.");
            else if (first.Equivalence.Divergences.Count > 0)
                result.Notes.Add($"First divergence: {string.Join(',', first.Equivalence.Divergences[0].Reasons)}");

            return result;
        }

        private static bool ValidateTraceMetadataBinding(TimingTraceCorpusEntry entry, Trace.TimingTraceDocument trace, List<string> notes)
        {
            var metadata = trace?.Metadata;
            if (metadata == null)
            {
                notes.Add("Trace metadata is missing.");
                return false;
            }

            var valid = true;
            if (string.IsNullOrWhiteSpace(metadata.CorpusTraceId))
            {
                notes.Add("Trace metadata is not bound to a corpus trace id.");
                valid = false;
            }
            else if (!string.Equals(metadata.CorpusTraceId, entry.TraceId, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"Trace metadata corpus id '{metadata.CorpusTraceId}' does not match manifest id '{entry.TraceId}'.");
                valid = false;
            }

            if (metadata.ScenarioBucket != Trace.TimingTraceScenarioBucket.Unspecified && metadata.ScenarioBucket != entry.ScenarioBucket)
            {
                notes.Add($"Trace metadata scenario bucket '{metadata.ScenarioBucket}' does not match manifest bucket '{entry.ScenarioBucket}'.");
                valid = false;
            }

            return valid;
        }

        private static TimingTraceEquivalenceOutcome DetermineEquivalenceOutcome(TimingReplayEquivalenceReport report)
        {
            if (report == null)
                return TimingTraceEquivalenceOutcome.MissingObservedStream;
            if (report.IsEquivalent)
                return TimingTraceEquivalenceOutcome.StrictEquivalent;
            if (IsApproximatelyEquivalent(report))
                return TimingTraceEquivalenceOutcome.ApproximateEquivalent;
            return TimingTraceEquivalenceOutcome.Divergent;
        }

        private static bool IsExpectationSatisfied(TimingTraceCorpusEntry entry, TimingTraceEquivalenceOutcome outcome)
        {
            if (entry.DivergenceExpected)
                return outcome == TimingTraceEquivalenceOutcome.Divergent;

            return entry.EquivalenceExpectation switch
            {
                TimingTraceEquivalenceExpectation.Strict => outcome == TimingTraceEquivalenceOutcome.StrictEquivalent,
                TimingTraceEquivalenceExpectation.Approximate => outcome is TimingTraceEquivalenceOutcome.StrictEquivalent or TimingTraceEquivalenceOutcome.ApproximateEquivalent,
                _ => false,
            };
        }

        private static bool IsApproximatelyEquivalent(TimingReplayEquivalenceReport report)
        {
            if (report == null)
                return false;
            if (report.ObservedDecisionCount != report.ReplayDecisionCount)
                return false;

            foreach (var divergence in report.Divergences)
            {
                foreach (var reason in divergence.Reasons)
                {
                    if (IsStructuralReason(reason))
                        return false;
                }
            }

            return true;
        }

        private static bool IsStructuralReason(string reason)
            => reason is "MissingObservedDecision"
                or "MissingReplayDecision"
                or "Source"
                or "Reason"
                or "Mode"
                or "Quality"
                or "ActionKind"
                or "ActionId"
                or "Sequence";

        private static IEnumerable<string> EnumerateExtraTracePaths(string corpusRoot, TimingTraceCorpusDocument corpus)
        {
            var knownPaths = new HashSet<string>(
                corpus.Entries.Select(entry => Path.GetFullPath(TimingTraceCorpusPaths.ResolveTracePath(corpusRoot, entry))),
                StringComparer.OrdinalIgnoreCase);

            return Directory.Exists(corpusRoot)
                ? Directory.EnumerateFiles(corpusRoot, "*.timing-trace.json", SearchOption.AllDirectories)
                    .Where(path => !knownPaths.Contains(Path.GetFullPath(path)))
                : Array.Empty<string>();
        }

        private static void ValidateManifest(TimingTraceCorpusDocument corpus, List<string> warnings)
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in corpus.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.TraceId))
                    warnings.Add("Corpus entry is missing traceId.");
                else if (!seenIds.Add(entry.TraceId))
                    warnings.Add($"Duplicate traceId '{entry.TraceId}'.");

                if (string.IsNullOrWhiteSpace(entry.RelativeTracePath))
                    warnings.Add($"Corpus entry '{entry.TraceId}' is missing relativeTracePath.");
                else if (!seenPaths.Add(entry.RelativeTracePath))
                    warnings.Add($"Duplicate relativeTracePath '{entry.RelativeTracePath}'.");

                if (string.IsNullOrWhiteSpace(entry.Purpose))
                    warnings.Add($"Corpus entry '{entry.TraceId}' is missing a purpose.");
                if (string.IsNullOrWhiteSpace(entry.ExpectedBehavior))
                    warnings.Add($"Corpus entry '{entry.TraceId}' is missing expected behavior notes.");
            }
        }
    }
}
