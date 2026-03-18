using System.Collections.Generic;
using Tsunippy.Database;
using Tsunippy.Runtime.Controller;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Replay
{
    public static class SyntheticTimingTraceFactory
    {
        public static TimingTraceDocument CreateSample()
            => AttachObservedDecisions(new TimingTraceDocument
            {
                Metadata = new TimingTraceMetadata
                {
                    Label = "synthetic-frontier-sample",
                    Source = "synthetic",
                    PluginVersion = "lab",
                    Notes = "Deterministic synthetic trace covering instant prediction, correction, cast prediction, and replay equivalence.",
                },
                CapturedProfile = TimingControllerProfile.CreateFrontierDefault(),
                CapturedKnowledge = new TimingKnowledgeSnapshot(),
                Events = new List<TimingTraceEvent>
                {
                    new TimingRuntimeResetTraceEvent(0.000, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Synthetic capture start."),
                    new TimingAdvanceTraceEvent(0.016, 0.016f, false, false, 1001, GameContext.PvE, false, 0f, 0f),
                    new TimingActionRequestTraceEvent(0.020, 149, 1, GameContext.PvE, TimingActionKind.Instant, 0f, true),
                    new TimingNetworkPacketTraceEvent(0.025, TimingPacketClass.Unknown),
                    new TimingActionEffectTraceEvent(0.310, 149, 1, GameContext.PvE, 0.200f, 0.600f, 0.600f),
                    new TimingAdvanceTraceEvent(0.500, 0.190f, true, false, 1001, GameContext.PvE, false, 0f, 0f),
                    new TimingActionRequestTraceEvent(0.540, 16495, 2, GameContext.PvE, TimingActionKind.Instant, 0f, true),
                    new TimingNetworkPacketTraceEvent(0.545, TimingPacketClass.Unknown),
                    new TimingActionEffectTraceEvent(0.920, 16495, 2, GameContext.PvE, 0.160f, 0.680f, 0.680f),
                    new TimingCastBeginTraceEvent(1.500, 152, 3, GameContext.PvE),
                    new TimingAdvanceTraceEvent(1.540, 0.620f, true, false, 1001, GameContext.PvE, true, 0.180f, 0f),
                    new TimingAdvanceTraceEvent(1.690, 0.150f, true, false, 1001, GameContext.PvE, true, 0.040f, 0f),
                    new TimingNetworkPacketTraceEvent(1.695, TimingPacketClass.Unknown),
                    new TimingActionEffectTraceEvent(1.980, 152, 3, GameContext.PvE, 0.050f, 0.100f, 0.100f),
                    new TimingNoteTraceEvent(2.000, "Synthetic trace complete."),
                },
            });

        public static TimingTraceDocument CreateTrustworthinessSample()
            => AttachObservedDecisions(new TimingTraceDocument
            {
                Metadata = new TimingTraceMetadata
                {
                    Label = "synthetic-trustworthiness-sample",
                    Source = "synthetic",
                    PluginVersion = "lab",
                    Notes = "Exercises failure reset semantics, cast interrupt fidelity, and conflict classification.",
                },
                CapturedProfile = TimingControllerProfile.CreateFrontierDefault(),
                CapturedKnowledge = new TimingKnowledgeSnapshot(),
                Events = new List<TimingTraceEvent>
                {
                    new TimingRuntimeResetTraceEvent(0.000, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Trust trace start."),
                    new TimingRuntimeResetTraceEvent(0.050, RuntimeResetReason.RuntimeFailure, new TimingResetSemantics(false, false), "Synthetic failure quarantine."),
                    new TimingActionRequestTraceEvent(0.080, 777, 11, GameContext.PvE, TimingActionKind.Instant, 0f, true),
                    new TimingRuntimeResetTraceEvent(0.120, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Recovered from failure."),
                    new TimingCastBeginTraceEvent(0.200, 9001, 12, GameContext.PvE),
                    new TimingCastInterruptTraceEvent(0.260, 9001, 12, GameContext.PvE),
                    new TimingActionRequestTraceEvent(0.320, 1818, 13, GameContext.PvE, TimingActionKind.Instant, 0f, true),
                    new TimingActionEffectTraceEvent(0.500, 1818, 13, GameContext.PvE, 0.180f, 0.600f, 0.550f),
                    new TimingActionRequestTraceEvent(0.560, 1819, 14, GameContext.PvE, TimingActionKind.Instant, 0f, true),
                    new TimingNoteTraceEvent(0.600, "Trustworthiness trace complete."),
                },
            });

        public static IReadOnlyList<TimingTraceDocument> CreateRegressionSet()
            => new[]
            {
                CreateSample(),
                CreateTrustworthinessSample(),
            };

        private static TimingTraceDocument AttachObservedDecisions(TimingTraceDocument trace)
        {
            var replay = TimingReplayRunner.Replay(trace);
            trace.ObservedDecisions = new List<TimingDecisionTrace>(replay.Decisions);
            return trace;
        }
    }
}
