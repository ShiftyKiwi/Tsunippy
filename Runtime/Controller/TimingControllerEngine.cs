using System;
using System.Collections.Generic;
using Tsunippy.Database;
using Tsunippy.RTT;
using Tsunippy.Runtime.Trace;

namespace Tsunippy.Runtime.Controller
{
    public sealed class TimingControllerEngine
    {
        private sealed class PendingPrediction
        {
            public ushort Sequence { get; init; }
            public uint ActionId { get; init; }
            public GameContext Context { get; init; }
            public TimingActionKind Kind { get; init; }
            public float PredictedBaseLock { get; init; }
            public float PredictedFullLock { get; init; }
            public float FloorAtIssue { get; init; }
            public float PredictionConfidence { get; init; }
            public bool WasApplied { get; init; }
            public TimingDecisionReason IssueReason { get; init; }
            public string Note { get; init; } = string.Empty;
        }

        private sealed class CastTrackerState
        {
            public CastLifecycleStage Stage { get; set; } = CastLifecycleStage.Idle;
            public ushort Sequence { get; set; }
            public uint ActionId { get; set; }
            public GameContext Context { get; set; } = GameContext.PvE;

            public static CastTrackerState Idle() => new();
        }

        private readonly LockDatabase lockDatabase;
        private readonly CastTaxDatabase castTaxDatabase;
        private readonly JacobsonKarels rttEstimator;
        private readonly DynamicFloor dynamicFloor;
        private readonly PacketTracker packetTracker = new();
        private readonly DecisionJournal decisionJournal;
        private readonly Dictionary<ushort, PendingPrediction> pendingPredictions = new();

        private CastTrackerState castState = CastTrackerState.Idle();
        private TimingControllerProfile profile;
        private bool conflictQuarantine;
        private bool failureQuarantine;
        private long? lastLocalPlayerId;
        private bool wasBetweenAreas;
        private string lastSuppressionReason = string.Empty;
        private string lastRuntimeResetReason = RuntimeResetReason.Enable.ToString();
        private TimingDecisionTrace lastDecision;

        public TimingControllerEngine(TimingControllerProfile profile, LockDatabase lockDatabase, CastTaxDatabase castTaxDatabase, int decisionCapacity = 32)
        {
            this.profile = profile?.Clone() ?? TimingControllerProfile.CreateFrontierDefault();
            decisionJournal = new DecisionJournal(Math.Max(decisionCapacity, 8));
            rttEstimator = new JacobsonKarels
            {
                Alpha = this.profile.JKAlpha,
                Beta = this.profile.JKBeta,
                K = this.profile.JKK,
            };
            dynamicFloor = new DynamicFloor(this.profile.DynamicFloorWindow)
            {
                ScalingFactor = this.profile.DynamicFloorScaling,
            };
            this.lockDatabase = lockDatabase ?? new LockDatabase();
            this.castTaxDatabase = castTaxDatabase ?? new CastTaxDatabase();
        }

        public TimingControllerEngine(TimingControllerProfile profile, TimingKnowledgeSnapshot knowledge, int decisionCapacity = 32)
            : this(profile, CreateLockDatabase(knowledge), CreateCastTaxDatabase(knowledge), decisionCapacity)
        {
        }

        public float LastRTT { get; private set; }
        public float LastCorrection { get; private set; }
        public float LastVarianceBuffer { get; private set; }
        public float LastAdjustedLock { get; private set; }
        public uint LastActionId { get; private set; }
        public float CurrentFloor => dynamicFloor.Floor;
        public float CurrentSRTT => rttEstimator.SmoothedRTT;
        public float CurrentRTTVAR => rttEstimator.RTTVariance;
        public int FloorSampleCount => dynamicFloor.CurrentSampleCount;
        public int RTTSampleCount => rttEstimator.SampleCount;
        public int PacketsSent => packetTracker.TotalPacketsSent;
        public int ActionPacketsSent => packetTracker.ActionPacketsSent;
        public int PendingPredictionCount => pendingPredictions.Count;
        public CastLifecycleStage CastStage => castState.Stage;
        public bool HasTrackedCast => castState.Stage != CastLifecycleStage.Idle;
        public uint TrackedCastActionId => castState.ActionId;
        public ushort TrackedCastSequence => castState.Sequence;
        public GameContext TrackedCastContext => castState.Context;
        public TimingRuntimeMode CurrentMode => GetEffectiveMode();
        public TimingQuality CurrentQuality => ClassifyQuality(lastDecision.PredictionConfidence);
        public TimingDecisionSource LastDecisionSource => lastDecision.Source;
        public TimingDecisionReason LastDecisionReason => lastDecision.Reason;
        public string LastDecisionNote => lastDecision.Note ?? string.Empty;
        public float LastPredictionConfidence => lastDecision.PredictionConfidence;
        public bool ConflictDetected => conflictQuarantine;
        public bool FailureQuarantined => failureQuarantine;
        public string LastSuppressionReason => lastSuppressionReason;
        public string LastRuntimeResetReason => lastRuntimeResetReason;

        public TimingControllerProfile ExportProfile() => profile.Clone();
        public TimingKnowledgeSnapshot ExportKnowledge() => TimingKnowledgeSnapshot.FromDatabases(lockDatabase, castTaxDatabase);
        public TimingDecisionTrace[] GetRecentDecisions() => decisionJournal.SnapshotNewestFirst();

        public void ApplyProfile(TimingControllerProfile nextProfile)
        {
            if (nextProfile == null)
                return;

            profile = nextProfile.Clone();
            rttEstimator.Alpha = profile.JKAlpha;
            rttEstimator.Beta = profile.JKBeta;
            rttEstimator.K = profile.JKK;
            dynamicFloor.ScalingFactor = profile.DynamicFloorScaling;
        }

        public TimingControllerEventResult ResetRuntime(double timelineSeconds, RuntimeResetReason reason, string note, bool clearConflictQuarantine, bool clearFailureQuarantine)
        {
            if (clearConflictQuarantine)
                conflictQuarantine = false;
            if (clearFailureQuarantine)
                failureQuarantine = false;
            if (clearConflictQuarantine || clearFailureQuarantine || reason == RuntimeResetReason.Manual)
                lastSuppressionReason = string.Empty;

            pendingPredictions.Clear();
            castState = CastTrackerState.Idle();
            packetTracker.Reset();
            rttEstimator.Reset();
            dynamicFloor.Reset();
            LastRTT = 0f;
            LastCorrection = 0f;
            LastVarianceBuffer = 0f;
            LastAdjustedLock = 0f;
            LastActionId = 0u;
            lastRuntimeResetReason = reason.ToString();
            lastLocalPlayerId = null;
            wasBetweenAreas = false;

            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.RuntimeControl,
                TimingDecisionReason.RuntimeReset,
                TimingActionKind.Instant,
                0u,
                0,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                note ?? reason.ToString());

            return new TimingControllerEventResult(decision, false, 0f, false, false, 0d, 0ul, false, false, string.Empty);
        }

        public TimingControllerEventResult EnterFailureQuarantine(double timelineSeconds, string note)
        {
            failureQuarantine = true;
            lastSuppressionReason = note ?? "Timing runtime entered failure quarantine.";
            pendingPredictions.Clear();
            castState = CastTrackerState.Idle();
            packetTracker.Reset();
            rttEstimator.Reset();
            dynamicFloor.Reset();
            LastRTT = 0f;
            LastCorrection = 0f;
            LastVarianceBuffer = 0f;
            LastAdjustedLock = 0f;
            LastActionId = 0u;
            lastRuntimeResetReason = RuntimeResetReason.RuntimeFailure.ToString();

            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.RuntimeControl,
                TimingDecisionReason.RuntimeFailure,
                TimingActionKind.Instant,
                0u,
                0,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                note ?? string.Empty);

            return new TimingControllerEventResult(decision, false, 0f, false, false, 0d, 0ul, false, true, lastSuppressionReason);
        }

        public TimingControllerEventResult ProcessActionRequest(double timelineSeconds, uint actionId, ushort sequence, GameContext context, TimingActionKind actionKind, float existingAnimationLock, bool accepted)
        {
            if (!accepted || actionKind != TimingActionKind.Instant)
                return TimingControllerEventResult.None;

            packetTracker.MarkActionIssued();

            var baseLock = GetActionPredictionBaseLock(actionId, context, out var confidence);
            var floor = dynamicFloor.Floor;
            var predictedLock = baseLock + floor;
            var canApply = CurrentMode == TimingRuntimeMode.Active && existingAnimationLock <= profile.ExistingActionLockThreshold;
            var issueReason = canApply
                ? TimingDecisionReason.PredictedInstantLock
                : CurrentMode == TimingRuntimeMode.Active
                    ? TimingDecisionReason.ExistingLockSuppressed
                    : TimingDecisionReason.DryRunSuppressed;
            var note = canApply
                ? "Instant prediction applied."
                : CurrentMode == TimingRuntimeMode.Active
                    ? $"Existing animation lock {existingAnimationLock * 1000f:0} ms suppressed prediction."
                    : $"Prediction retained for observation in mode {CurrentMode}.";

            var pending = new PendingPrediction
            {
                Sequence = sequence,
                ActionId = actionId,
                Context = context,
                Kind = TimingActionKind.Instant,
                PredictedBaseLock = baseLock,
                PredictedFullLock = predictedLock,
                FloorAtIssue = floor,
                PredictionConfidence = confidence,
                WasApplied = canApply,
                IssueReason = issueReason,
                Note = note,
            };

            pendingPredictions[sequence] = pending;
            if (!canApply)
                lastSuppressionReason = note;

            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.InstantPrediction,
                issueReason,
                TimingActionKind.Instant,
                pending.ActionId,
                pending.Sequence,
                pending.PredictedFullLock,
                0f,
                canApply ? pending.PredictedFullLock : 0f,
                0f,
                pending.PredictionConfidence,
                0f,
                floor,
                0f,
                0f,
                existingAnimationLock,
                note);

            return new TimingControllerEventResult(decision, canApply, canApply ? predictedLock : 0f, false, false, 0d, 0ul, false, false, note);
        }

        public void ProcessCastBegin(uint actionId, ushort sequence, GameContext context)
        {
            castState = new CastTrackerState
            {
                Stage = CastLifecycleStage.Casting,
                Sequence = sequence,
                ActionId = actionId,
                Context = context,
            };
        }

        public TimingControllerEventResult ProcessCastInterrupt(double timelineSeconds, uint actionId, ushort sequence)
        {
            if (castState.Stage == CastLifecycleStage.Idle)
                return TimingControllerEventResult.None;

            castState = CastTrackerState.Idle();
            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.RuntimeControl,
                TimingDecisionReason.CastInterrupted,
                TimingActionKind.Cast,
                actionId,
                sequence,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                "Cast interrupted before a correlated response arrived.");

            return new TimingControllerEventResult(decision, false, 0f, false, false, 0d, 0ul, false, false, string.Empty);
        }

        public TimingControllerEventResult ProcessUpdate(
            double timelineSeconds,
            float deltaSeconds,
            bool betweenAreas,
            long? localPlayerId,
            bool hasActiveCast,
            float castRemainingSeconds,
            float currentAnimationLock)
        {
            packetTracker.Update(deltaSeconds);

            if (betweenAreas && !wasBetweenAreas)
            {
                wasBetweenAreas = betweenAreas;
                return ResetRuntime(timelineSeconds, RuntimeResetReason.ZoneTransition, "Zone transition detected.", false, false);
            }

            wasBetweenAreas = betweenAreas;

            if (localPlayerId.HasValue)
            {
                if (lastLocalPlayerId.HasValue && lastLocalPlayerId.Value != localPlayerId.Value)
                {
                    lastLocalPlayerId = localPlayerId;
                    return ResetRuntime(timelineSeconds, RuntimeResetReason.PlayerChanged, "Local player object changed.", false, false);
                }

                lastLocalPlayerId = localPlayerId;
            }
            else
            {
                lastLocalPlayerId = null;
            }

            if (!profile.EnableCastLockPrediction || castState.Stage != CastLifecycleStage.Casting || !hasActiveCast)
                return TimingControllerEventResult.None;

            if (castRemainingSeconds > profile.CastCompletionWindow)
                return TimingControllerEventResult.None;

            packetTracker.MarkActionIssued();

            var baseLock = GetCastPredictionBaseLock(castState.ActionId, castState.Context, out var confidence);
            var floor = dynamicFloor.Floor;
            var predictedLock = baseLock + floor;
            var canApply = CurrentMode == TimingRuntimeMode.Active && currentAnimationLock <= profile.ExistingCastLockThreshold;
            var issueReason = canApply
                ? TimingDecisionReason.PredictedCastLock
                : CurrentMode == TimingRuntimeMode.Active
                    ? TimingDecisionReason.ExistingLockSuppressed
                    : TimingDecisionReason.DryRunSuppressed;
            var note = canApply
                ? "Cast completion prediction applied."
                : CurrentMode == TimingRuntimeMode.Active
                    ? $"Existing residual cast lock {currentAnimationLock * 1000f:0} ms suppressed prediction."
                    : $"Cast prediction retained for observation in mode {CurrentMode}.";

            var pending = new PendingPrediction
            {
                Sequence = castState.Sequence,
                ActionId = castState.ActionId,
                Context = castState.Context,
                Kind = TimingActionKind.Cast,
                PredictedBaseLock = baseLock,
                PredictedFullLock = predictedLock,
                FloorAtIssue = floor,
                PredictionConfidence = confidence,
                WasApplied = canApply,
                IssueReason = issueReason,
                Note = note,
            };

            pendingPredictions[pending.Sequence] = pending;
            castState.Stage = CastLifecycleStage.Predicted;

            if (!canApply)
                lastSuppressionReason = note;

            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.CastPrediction,
                issueReason,
                TimingActionKind.Cast,
                pending.ActionId,
                pending.Sequence,
                pending.PredictedFullLock,
                0f,
                canApply ? pending.PredictedFullLock : 0f,
                0f,
                pending.PredictionConfidence,
                0f,
                floor,
                0f,
                0f,
                currentAnimationLock,
                note);

            return new TimingControllerEventResult(decision, canApply, canApply ? predictedLock : 0f, false, false, 0d, 0ul, false, false, note);
        }

        public TimingControllerEventResult ProcessActionEffect(double timelineSeconds, uint actionId, ushort sequence, GameContext context, float oldLock, float newLock, float headerAnimationLock)
        {
            if (TimingMath.NearlyEqual(oldLock, newLock))
                return TimingControllerEventResult.None;

            pendingPredictions.TryGetValue(sequence, out var correlatedPending);
            var conflictKind = ResolveConflictKind(correlatedPending, sequence, actionId);

            if (!TimingMath.NearlyEqual(newLock, headerAnimationLock))
                return EnterConflictQuarantine(timelineSeconds, "Server animation lock did not match the post-hook lock value.", conflictKind, actionId, sequence, newLock, oldLock, TimingDecisionReason.ResponseMismatch);

            if (LooksExternallyModified(newLock))
                return EnterConflictQuarantine(timelineSeconds, $"Detected a fractional lock pattern at {newLock * 1000f:0} ms.", conflictKind, actionId, sequence, newLock, oldLock, TimingDecisionReason.ConflictDetected);

            LastActionId = actionId;

            var pending = correlatedPending;
            pendingPredictions.Remove(sequence);

            var castMatched = TryConsumeCastState(sequence, actionId);
            if (castMatched && pending == null)
            {
                var learned = LearnCastTax(actionId, newLock, context);
                var decision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    TimingDecisionReason.NoCorrelation,
                    TimingActionKind.Cast,
                    actionId,
                    sequence,
                    0f,
                    newLock,
                    newLock,
                    0f,
                    GetCastPredictionConfidence(actionId, context),
                    0f,
                    0f,
                    0f,
                    0f,
                    oldLock,
                    "Cast response arrived without an active prediction to arbitrate.");

                return new TimingControllerEventResult(decision, false, 0f, learned, false, 0d, 0ul, false, false, string.Empty);
            }

            if (pending == null)
            {
                var learned = LearnAnimationLock(actionId, context, newLock);
                var decision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    TimingDecisionReason.NoCorrelation,
                    TimingActionKind.Instant,
                    actionId,
                    sequence,
                    0f,
                    newLock,
                    newLock,
                    0f,
                    GetActionPredictionConfidence(actionId, context),
                    0f,
                    0f,
                    0f,
                    0f,
                    oldLock,
                    "Local response had no pending prediction; the controller observed but did not rewrite.");

                return new TimingControllerEventResult(decision, false, 0f, learned, false, 0d, 0ul, false, false, string.Empty);
            }

            var learnedDataChanged = pending.Kind == TimingActionKind.Cast
                ? LearnCastTax(pending.ActionId, newLock, pending.Context)
                : LearnAnimationLock(pending.ActionId, pending.Context, newLock);

            return ResolvePendingPrediction(timelineSeconds, pending, oldLock, newLock, learnedDataChanged);
        }

        public void ProcessNetworkPacket(TimingPacketClass packetClass) => packetTracker.RecordPacket(packetClass);

        private TimingControllerEventResult ResolvePendingPrediction(double timelineSeconds, PendingPrediction pending, float oldLock, float newLock, bool learnedDataChanged)
        {
            if (!pending.WasApplied)
            {
                var decision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    pending.IssueReason == TimingDecisionReason.DryRunSuppressed ? TimingDecisionReason.DryRunSuppressed : TimingDecisionReason.LearningOnly,
                    pending.Kind,
                    pending.ActionId,
                    pending.Sequence,
                    pending.PredictedFullLock,
                    newLock,
                    newLock,
                    0f,
                    pending.PredictionConfidence,
                    0f,
                    pending.FloorAtIssue,
                    0f,
                    0f,
                    oldLock,
                    pending.Note);

                return new TimingControllerEventResult(decision, false, 0f, learnedDataChanged, false, 0d, 0ul, false, false, string.Empty);
            }

            if (CurrentMode != TimingRuntimeMode.Active)
            {
                var modeNote = $"Prediction was applied earlier but response was resolved in mode {CurrentMode}; no final rewrite was performed.";
                lastSuppressionReason = modeNote;
                var decision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    TimingDecisionReason.DryRunSuppressed,
                    pending.Kind,
                    pending.ActionId,
                    pending.Sequence,
                    pending.PredictedFullLock,
                    newLock,
                    newLock,
                    0f,
                    pending.PredictionConfidence,
                    0f,
                    pending.FloorAtIssue,
                    0f,
                    0f,
                    oldLock,
                    modeNote);

                return new TimingControllerEventResult(decision, false, 0f, learnedDataChanged, false, 0d, 0ul, false, false, modeNote);
            }

            var measuredRtt = pending.PredictedFullLock - oldLock;
            if (!TimingMath.IsFiniteAndInRange(measuredRtt, TimingMath.MinimumMeasuredRtt, TimingMath.MaximumMeasuredRtt))
            {
                var invalidNote = $"Measured RTT {measuredRtt * 1000f:0} ms was outside the trusted range.";
                lastSuppressionReason = invalidNote;
                var invalidDecision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    TimingDecisionReason.InvalidMeasurement,
                    pending.Kind,
                    pending.ActionId,
                    pending.Sequence,
                    pending.PredictedFullLock,
                    newLock,
                    newLock,
                    measuredRtt,
                    pending.PredictionConfidence,
                    0f,
                    pending.FloorAtIssue,
                    0f,
                    0f,
                    oldLock,
                    invalidNote);

                return new TimingControllerEventResult(invalidDecision, false, 0f, learnedDataChanged, false, 0d, 0ul, false, false, invalidNote);
            }

            LastRTT = measuredRtt;
            dynamicFloor.AddSample(measuredRtt);
            if (measuredRtt <= pending.FloorAtIssue + TimingMath.LockEqualityEpsilon)
            {
                LastCorrection = 0f;
                LastVarianceBuffer = 0f;
                LastAdjustedLock = newLock;
                var floorDecision = RecordDecision(
                    timelineSeconds,
                    TimingDecisionSource.ServerCorrection,
                    TimingDecisionReason.RttBelowFloor,
                    pending.Kind,
                    pending.ActionId,
                    pending.Sequence,
                    pending.PredictedFullLock,
                    newLock,
                    newLock,
                    measuredRtt,
                    pending.PredictionConfidence,
                    0f,
                    pending.FloorAtIssue,
                    0f,
                    0f,
                    oldLock,
                    "Observed RTT was already inside the learned floor; the runtime kept the server lock.");

                return new TimingControllerEventResult(floorDecision, false, 0f, learnedDataChanged, false, 0d, 0ul, false, false, string.Empty);
            }

            var weight = packetTracker.GetRTTWeight();
            rttEstimator.AddSample(measuredRtt, weight);
            var correction = newLock - pending.PredictedBaseLock;
            var varianceBuffer = ComputeVarianceBuffer(pending.PredictionConfidence);
            var confidenceGuard = ComputeConfidenceGuard(pending.FloorAtIssue, pending.PredictionConfidence);
            var adjustedAnimationLock = Math.Max(oldLock + correction + varianceBuffer + confidenceGuard, 0f);

            LastCorrection = correction;
            LastVarianceBuffer = varianceBuffer;
            LastAdjustedLock = adjustedAnimationLock;

            var reduction = Math.Max(newLock - adjustedAnimationLock, 0f);
            var note = $"weight={weight:F2}, floor={pending.FloorAtIssue * 1000f:0} ms, strategy={profile.Strategy}";
            if (confidenceGuard > 0f)
                note += $", guard={confidenceGuard * 1000f:0} ms";

            var correctionDecision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.ServerCorrection,
                pending.Kind == TimingActionKind.Cast ? TimingDecisionReason.AppliedCastCorrection : TimingDecisionReason.AppliedInstantCorrection,
                pending.Kind,
                pending.ActionId,
                pending.Sequence,
                pending.PredictedFullLock,
                newLock,
                adjustedAnimationLock,
                measuredRtt,
                pending.PredictionConfidence,
                correction,
                pending.FloorAtIssue,
                varianceBuffer,
                confidenceGuard,
                oldLock,
                note,
                weight);

            return new TimingControllerEventResult(correctionDecision, true, adjustedAnimationLock, learnedDataChanged, reduction > 0f, reduction, reduction > 0f ? 1ul : 0ul, false, false, note);
        }

        private TimingControllerEventResult EnterConflictQuarantine(double timelineSeconds, string reason, TimingActionKind actionKind, uint actionId, ushort sequence, float serverLock, float oldLock, TimingDecisionReason decisionReason)
        {
            conflictQuarantine = true;
            lastSuppressionReason = reason;
            pendingPredictions.Clear();
            castState = CastTrackerState.Idle();
            packetTracker.Reset();
            rttEstimator.Reset();
            dynamicFloor.Reset();
            LastRTT = 0f;
            LastCorrection = 0f;
            LastVarianceBuffer = 0f;
            LastAdjustedLock = serverLock;
            LastActionId = actionId;
            lastRuntimeResetReason = RuntimeResetReason.ConflictDetected.ToString();

            var decision = RecordDecision(
                timelineSeconds,
                TimingDecisionSource.RuntimeControl,
                decisionReason,
                actionKind,
                actionId,
                sequence,
                0f,
                serverLock,
                serverLock,
                0f,
                0f,
                0f,
                0f,
                0f,
                0f,
                oldLock,
                reason);

            return new TimingControllerEventResult(decision, false, 0f, false, false, 0d, 0ul, true, false, reason);
        }

        private bool LearnAnimationLock(uint actionId, GameContext context, float observedLock)
            => !conflictQuarantine && profile.LearnAnimationLocks && lockDatabase.RecordLock(actionId, context, observedLock);

        private bool LearnCastTax(uint actionId, float observedLock, GameContext? context = null)
            => !conflictQuarantine && profile.LearnCastTax && castTaxDatabase.RecordTax(actionId, context ?? GameContext.PvE, observedLock);

        private TimingActionKind ResolveConflictKind(PendingPrediction pending, ushort sequence, uint actionId)
        {
            if (pending != null)
                return pending.Kind;

            if (castState.Stage != CastLifecycleStage.Idle
                && (castState.Sequence == sequence || castState.ActionId == actionId))
            {
                return TimingActionKind.Cast;
            }

            return TimingActionKind.Instant;
        }

        private float GetActionPredictionBaseLock(uint actionId, GameContext context, out float confidence)
        {
            var entry = lockDatabase.GetEntry(actionId, context);
            confidence = entry != null ? Math.Max(entry.Confidence, profile.MinimumPredictionConfidence) : profile.MinimumPredictionConfidence;
            return lockDatabase.GetLock(actionId, context, profile.DefaultActionLock);
        }

        private float GetCastPredictionBaseLock(uint actionId, GameContext context, out float confidence)
        {
            var entry = castTaxDatabase.GetEntry(actionId, context);
            confidence = entry != null ? Math.Max(entry.Confidence, profile.MinimumPredictionConfidence) : profile.MinimumPredictionConfidence;
            return profile.LearnCastTax ? castTaxDatabase.GetTax(actionId, context, profile.DefaultCasterTax) : profile.DefaultCasterTax;
        }

        private float GetActionPredictionConfidence(uint actionId, GameContext context)
        {
            var entry = lockDatabase.GetEntry(actionId, context);
            return entry != null ? Math.Max(entry.Confidence, profile.MinimumPredictionConfidence) : profile.MinimumPredictionConfidence;
        }

        private float GetCastPredictionConfidence(uint actionId, GameContext context)
        {
            var entry = castTaxDatabase.GetEntry(actionId, context);
            return entry != null ? Math.Max(entry.Confidence, profile.MinimumPredictionConfidence) : profile.MinimumPredictionConfidence;
        }

        private bool TryConsumeCastState(ushort sequence, uint actionId)
        {
            if (castState.Stage == CastLifecycleStage.Idle)
                return false;

            var matched = castState.Sequence == sequence || castState.ActionId == actionId;
            if (matched)
                castState = CastTrackerState.Idle();
            return matched;
        }

        private TimingRuntimeMode GetEffectiveMode()
        {
            if (failureQuarantine)
                return TimingRuntimeMode.FailureQuarantined;
            if (conflictQuarantine)
                return TimingRuntimeMode.ConflictQuarantined;
            if (profile.EnableDryRun)
                return TimingRuntimeMode.DryRunRequested;
            return TimingRuntimeMode.Active;
        }

        private TimingQuality ClassifyQuality(float predictionConfidence)
        {
            if (CurrentMode != TimingRuntimeMode.Active)
                return TimingQuality.Quarantined;
            if (rttEstimator.SampleCount < 5 || predictionConfidence < 0.3f)
                return TimingQuality.Learning;
            if (rttEstimator.RTTVariance <= 0.015f && predictionConfidence >= 0.75f)
                return TimingQuality.Stable;
            if (rttEstimator.RTTVariance <= 0.05f)
                return TimingQuality.Adaptive;
            return TimingQuality.Volatile;
        }

        private float ComputeVarianceBuffer(float confidence)
            => profile.Strategy switch
            {
                TimingControllerStrategy.VarianceOnly => rttEstimator.VarianceBuffer,
                _ => rttEstimator.VarianceBuffer * (1f + (1f - confidence) * 0.5f),
            };

        private float ComputeConfidenceGuard(float floorAtIssue, float confidence)
            => profile.Strategy == TimingControllerStrategy.ConfidenceAdaptive
                ? floorAtIssue * (1f - confidence) * 0.5f
                : 0f;

        private static bool LooksExternallyModified(float lockValue)
            => lockValue % 0.01f is >= 0.0005f and <= 0.0095f;

        private static LockDatabase CreateLockDatabase(TimingKnowledgeSnapshot knowledge)
            => (knowledge ?? new TimingKnowledgeSnapshot()).CreateLockDatabase();

        private static CastTaxDatabase CreateCastTaxDatabase(TimingKnowledgeSnapshot knowledge)
            => (knowledge ?? new TimingKnowledgeSnapshot()).CreateCastTaxDatabase();

        private TimingDecisionTrace RecordDecision(
            double timelineSeconds,
            TimingDecisionSource source,
            TimingDecisionReason reason,
            TimingActionKind kind,
            uint actionId,
            ushort sequence,
            float predictedLock,
            float serverLock,
            float finalLock,
            float measuredRtt,
            float predictionConfidence,
            float correction,
            float floor,
            float varianceBuffer,
            float confidenceGuard,
            float existingLock,
            string note,
            float rttWeight = 0f)
        {
            var trace = new TimingDecisionTrace(
                timelineSeconds,
                source,
                reason,
                CurrentMode,
                ClassifyQuality(predictionConfidence),
                kind,
                actionId,
                sequence,
                predictedLock,
                serverLock,
                finalLock,
                measuredRtt,
                predictionConfidence,
                correction,
                new TimingDecisionInputs(existingLock, floor, varianceBuffer, confidenceGuard, rttWeight),
                note ?? string.Empty);

            lastDecision = trace;
            decisionJournal.Add(trace);
            return trace;
        }
    }
}
