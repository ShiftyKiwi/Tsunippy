using System.Collections.Generic;
using System.IO;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Corpus
{
    public static class TimingTraceCorpusFactory
    {
        public static string ScaffoldRecommendedV1(string rootDirectory, bool overwrite = false)
        {
            var corpusRoot = TimingTraceCorpusPaths.ResolveCorpusRoot(rootDirectory);
            var manifestPath = TimingTraceCorpusPaths.ResolveManifestPath(corpusRoot);
            if (File.Exists(manifestPath) && !overwrite)
                throw new IOException($"Corpus manifest already exists at {manifestPath}.");

            var corpus = CreateRecommendedV1();
            Directory.CreateDirectory(corpusRoot);
            foreach (var entry in corpus.Entries)
            {
                var traceDirectory = Path.GetDirectoryName(TimingTraceCorpusPaths.ResolveTracePath(corpusRoot, entry));
                if (!string.IsNullOrEmpty(traceDirectory))
                    Directory.CreateDirectory(traceDirectory);
            }

            TimingTraceCorpusJson.Save(manifestPath, corpus);
            return manifestPath;
        }

        public static TimingTraceCorpusDocument CreateRecommendedV1()
            => new()
            {
                CorpusId = "first-real-trace-corpus-v1",
                Name = "First Real Trace Corpus",
                Description = "A small, disciplined corpus of real controller traces. Each trace exists to answer one clear replay-fidelity question.",
                Notes = "Start with the gold traces, keep captures short and repeatable, and rerun this corpus after any controller-core change.",
                Entries = new List<TimingTraceCorpusEntry>
                {
                    CreateEntry(
                        "instant-repeated-single-gcd",
                        "Repeated Single Instant GCD",
                        TimingTraceScenarioBucket.InstantBaseline,
                        "repeated-single-instant-gcd",
                        "Measure the clean instant-GCD prediction/correction loop on one repeated action.",
                        "Replay should deterministically reproduce the observed single-action prediction and correction pattern.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "instant", "baseline", "gcd", "gold"),
                    CreateEntry(
                        "instant-repeated-weave",
                        "Repeated Instant Weave",
                        TimingTraceScenarioBucket.InstantBaseline,
                        "repeated-instant-weave",
                        "Stress repeated weave timing with short, repeatable intervals.",
                        "Replay should preserve repeated instant prediction cadence without dropping decisions.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "instant", "weave", "baseline", "gold"),
                    CreateEntry(
                        "instant-latency-variation",
                        "Instant Under Mild Latency Variation",
                        TimingTraceScenarioBucket.InstantBaseline,
                        "same-instant-under-mild-latency-variation",
                        "Capture one instant action under slightly different feel to expose estimator sensitivity.",
                        "Replay should preserve the observed controller decisions across mild RTT variation.",
                        TimingTraceEquivalenceExpectation.Strict,
                        false,
                        "instant", "rtt", "baseline"),
                    CreateEntry(
                        "cast-short-complete",
                        "Short Cast Complete",
                        TimingTraceScenarioBucket.CastBaseline,
                        "short-cast-complete",
                        "Validate a clean short cast completion path.",
                        "Replay should preserve cast prediction and completion correction exactly.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "cast", "baseline", "gold"),
                    CreateEntry(
                        "cast-long-complete",
                        "Long Cast Complete",
                        TimingTraceScenarioBucket.CastBaseline,
                        "long-cast-complete",
                        "Validate cast completion after a longer active cast window.",
                        "Replay should preserve cast state ownership across multiple update ticks before completion.",
                        TimingTraceEquivalenceExpectation.Strict,
                        false,
                        "cast", "baseline"),
                    CreateEntry(
                        "cast-intentional-interrupt",
                        "Intentional Cast Interrupt",
                        TimingTraceScenarioBucket.CastBaseline,
                        "intentional-cast-interrupt",
                        "Validate interrupted-cast fidelity with real action and sequence identity.",
                        "Replay should preserve the interrupted cast identity and emit the same interrupt rationale.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "cast", "interrupt", "gold"),
                    CreateEntry(
                        "conflict-response-mismatch",
                        "Prediction/Response Mismatch",
                        TimingTraceScenarioBucket.ConflictRecovery,
                        "prediction-response-mismatch",
                        "Exercise mismatch handling when the server response does not align with the predicted lock state.",
                        "Replay should re-enter the same conflict rationale and quarantine behavior.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "conflict", "mismatch", "gold"),
                    CreateEntry(
                        "conflict-failure-recovery",
                        "Failure Quarantine Recovery",
                        TimingTraceScenarioBucket.ConflictRecovery,
                        "failure-quarantine-recovery",
                        "Capture a controller failure followed by explicit recovery.",
                        "Replay should preserve failure quarantine and only resume normal behavior after the recovery reset.",
                        TimingTraceEquivalenceExpectation.Strict,
                        true,
                        "failure", "recovery", "gold"),
                    CreateEntry(
                        "conflict-reset-semantics",
                        "Reset Semantics Case",
                        TimingTraceScenarioBucket.ConflictRecovery,
                        "reset-semantics-case",
                        "Validate that runtime reset intent is preserved through capture and replay.",
                        "Replay should honor the same reset semantics and not flatten them into a generic reset.",
                        TimingTraceEquivalenceExpectation.Strict,
                        false,
                        "reset", "semantics"),
                    CreateEntry(
                        "messy-mixed-segment",
                        "Mixed Instant/Cast Segment",
                        TimingTraceScenarioBucket.MessyGameplay,
                        "mixed-instant-cast-short-combat-segment",
                        "Capture a short combat segment with both instant and cast actions.",
                        "Replay should stay structurally aligned to the observed decision stream across mixed action kinds.",
                        TimingTraceEquivalenceExpectation.Approximate,
                        false,
                        "messy", "mixed", "combat"),
                    CreateEntry(
                        "messy-movement-cancel",
                        "Movement/Cancel Heavy Segment",
                        TimingTraceScenarioBucket.MessyGameplay,
                        "movement-cancel-heavy-segment",
                        "Capture movement- and cancel-heavy gameplay where controller state turns over quickly.",
                        "Replay should preserve the major decision reasons even if the numeric values are only approximately aligned.",
                        TimingTraceEquivalenceExpectation.Approximate,
                        false,
                        "messy", "movement", "cancel"),
                    CreateEntry(
                        "messy-dense-burst",
                        "Dense Burst Timing Segment",
                        TimingTraceScenarioBucket.MessyGameplay,
                        "dense-burst-timing-segment",
                        "Stress dense burst windows with many closely spaced timing decisions.",
                        "Replay should remain deterministic and structurally faithful through a dense decision window.",
                        TimingTraceEquivalenceExpectation.Approximate,
                        true,
                        "messy", "burst", "gold"),
                },
            };

        private static TimingTraceCorpusEntry CreateEntry(
            string traceId,
            string name,
            TimingTraceScenarioBucket bucket,
            string slug,
            string purpose,
            string expectedBehavior,
            TimingTraceEquivalenceExpectation expectation,
            bool isGold,
            params string[] tags)
            => new()
            {
                TraceId = traceId,
                Name = name,
                ScenarioBucket = bucket,
                RelativeTracePath = $"{TimingTraceCorpusPaths.GetBucketDirectory(bucket)}/{slug}.timing-trace.json",
                Purpose = purpose,
                ExpectedBehavior = expectedBehavior,
                EquivalenceExpectation = expectation,
                IsGold = isGold,
                Tags = new List<string>(tags ?? new string[0]),
            };
    }
}
