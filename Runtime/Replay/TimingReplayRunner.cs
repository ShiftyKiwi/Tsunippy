using System;
using Tsunippy.Runtime.Controller;
using Tsunippy.Runtime.Evaluation;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Replay
{
    public static class TimingReplayRunner
    {
        public static TimingReplayRunResult Replay(TimingTraceDocument trace, TimingControllerProfile overrideProfile = null)
        {
            if (trace == null)
                throw new ArgumentNullException(nameof(trace));

            var profile = overrideProfile?.Clone() ?? trace.CapturedProfile?.Clone() ?? TimingControllerProfile.CreateFrontierDefault();
            var engine = new TimingControllerEngine(profile, trace.CapturedKnowledge);
            var evaluator = new TimingReplayEvaluator();

            foreach (var traceEvent in trace.Events)
            {
                evaluator.ObserveEvent();
                var result = Dispatch(engine, traceEvent);
                if (result.Decision.HasValue)
                    evaluator.ObserveDecision(result.Decision.Value);
            }

            return evaluator.Build(profile.Name);
        }

        public static TimingReplayAnalysisResult Analyze(TimingTraceDocument trace, TimingControllerProfile overrideProfile = null)
        {
            var replay = Replay(trace, overrideProfile);
            var equivalence = trace?.ObservedDecisions?.Count > 0
                ? TimingReplayEquivalence.Compare(trace.ObservedDecisions, replay.Decisions)
                : null;

            return new TimingReplayAnalysisResult
            {
                Replay = replay,
                Equivalence = equivalence,
            };
        }

        private static TimingControllerEventResult Dispatch(TimingControllerEngine engine, TimingTraceEvent traceEvent)
            => traceEvent switch
            {
                TimingAdvanceTraceEvent advance => engine.ProcessUpdate(
                    advance.TimelineSeconds,
                    advance.DeltaSeconds,
                    advance.BetweenAreas,
                    advance.LocalPlayerId,
                    advance.HasActiveCast,
                    advance.CastRemainingSeconds,
                    advance.CurrentAnimationLock),

                TimingActionRequestTraceEvent actionRequest => engine.ProcessActionRequest(
                    actionRequest.TimelineSeconds,
                    actionRequest.ActionId,
                    actionRequest.Sequence,
                    actionRequest.Context,
                    actionRequest.ActionKind,
                    actionRequest.ExistingAnimationLock,
                    actionRequest.Accepted),

                TimingCastBeginTraceEvent castBegin => DispatchCastBegin(engine, castBegin),
                TimingCastInterruptTraceEvent castInterrupt => engine.ProcessCastInterrupt(castInterrupt.TimelineSeconds, castInterrupt.ActionId, castInterrupt.Sequence),
                TimingActionEffectTraceEvent actionEffect => engine.ProcessActionEffect(actionEffect.TimelineSeconds, actionEffect.ActionId, actionEffect.Sequence, actionEffect.Context, actionEffect.OldLock, actionEffect.NewLock, actionEffect.HeaderAnimationLock),
                TimingNetworkPacketTraceEvent packet => DispatchPacket(engine, packet),
                TimingRuntimeResetTraceEvent reset => DispatchReset(engine, reset),
                TimingNoteTraceEvent => TimingControllerEventResult.None,
                _ => TimingControllerEventResult.None,
            };

        private static TimingControllerEventResult DispatchCastBegin(TimingControllerEngine engine, TimingCastBeginTraceEvent castBegin)
        {
            engine.ProcessCastBegin(castBegin.ActionId, castBegin.Sequence, castBegin.Context);
            return TimingControllerEventResult.None;
        }

        private static TimingControllerEventResult DispatchPacket(TimingControllerEngine engine, TimingNetworkPacketTraceEvent packet)
        {
            engine.ProcessNetworkPacket(packet.PacketClass);
            return TimingControllerEventResult.None;
        }

        private static TimingControllerEventResult DispatchReset(TimingControllerEngine engine, TimingRuntimeResetTraceEvent reset)
        {
            if (reset.Reason == RuntimeResetReason.RuntimeFailure)
                return engine.EnterFailureQuarantine(reset.TimelineSeconds, reset.Note);

            return engine.ResetRuntime(
                reset.TimelineSeconds,
                reset.Reason,
                reset.Note,
                reset.Semantics.ClearConflictQuarantine,
                reset.Semantics.ClearFailureQuarantine);
        }
    }
}
