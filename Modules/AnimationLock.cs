using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Tsunippy.Database;
using Tsunippy.RTT;
using Tsunippy.Runtime;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableAnimLockComp = true;
        public bool EnableLogging = false;
        public bool EnableDryRun = false;
        public bool LearnAnimationLocks = true;
        public float JKAlpha = 0.125f;
        public float JKBeta = 0.25f;
        public float JKK = 2.0f;
        public float DynamicFloorScaling = 0.85f;
        public int DynamicFloorWindow = 100;
        public LockDatabase LockDb = new();
        public ulong TotalActionsReduced = 0ul;
        public double TotalAnimationLockReduction = 0d;
    }
}

namespace Tsunippy.Modules
{
    public class AnimationLock : Module
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
            public string Note { get; init; }
        }

        private sealed class CastTrackerState
        {
            public CastLifecycleStage Stage { get; set; } = CastLifecycleStage.Idle;
            public ushort Sequence { get; set; }
            public uint ActionId { get; set; }
            public GameContext Context { get; set; } = GameContext.PvE;

            public static CastTrackerState Idle() => new();
        }

        private const float LearnedSaveIdleDelay = 15f;
        private const float LearnedBatchSaveIdleDelay = 5f;
        private const float RuntimeStatsSaveIdleDelay = 120f;
        private const int SaveFlushBatchSize = 8;
        private const float ExistingActionLockThreshold = Game.DefaultClientAnimationLock + TimingMath.LockEqualityEpsilon;
        private const float ExistingCastLockThreshold = TimingMath.LockEqualityEpsilon;
        private const float MinimumPredictionConfidence = 0.15f;

        private readonly JacobsonKarels rttEstimator = new();
        private readonly DynamicFloor dynamicFloor;
        private readonly PacketTracker packetTracker = new();
        private readonly DecisionJournal decisionJournal = new(12);
        private readonly Dictionary<ushort, PendingPrediction> pendingPredictions = new();

        private CastTrackerState castState = CastTrackerState.Idle();
        private bool conflictQuarantine;
        private bool failureQuarantine;
        private bool saveLearnedData;
        private bool saveRuntimeStats;
        private float outOfCombatIdleTimer;
        private int pendingLearnedEntries;
        private long? lastLocalPlayerId;
        private bool wasBetweenAreas;
        private int hotPathFailureCount;
        private string lastHotPathFailure = string.Empty;
        private string lastSuppressionReason = string.Empty;
        private string lastRuntimeResetReason = RuntimeResetReason.Enable.ToString();
        private TimingDecisionTrace lastDecision;

        public override bool DisableOnRuntimeFailure => false;
        public override bool IsEnabled { get => Config.EnableAnimLockComp; set => Config.EnableAnimLockComp = value; }
        public override int DrawOrder => 1;

        public float LastRTT { get; private set; }
        public float LastCorrection { get; private set; }
        public float LastVarianceBuffer { get; private set; }
        public float LastAdjustedLock { get; private set; }
        public uint LastActionID { get; private set; }
        public float CurrentFloor => dynamicFloor.Floor;
        public float CurrentSRTT => rttEstimator.SmoothedRTT;
        public float CurrentRTTVAR => rttEstimator.RTTVariance;
        public int FloorSampleCount => dynamicFloor.CurrentSampleCount;
        public int RTTSampleCount => rttEstimator.SampleCount;
        public int PacketsSent => packetTracker.TotalPacketsSent;
        public int ActionPacketsSent => packetTracker.ActionPacketsSent;
        public int PendingLearnedEntries => pendingLearnedEntries;
        public int PendingPredictionCount => pendingPredictions.Count;
        public int HotPathFailureCount => hotPathFailureCount;
        public CastLifecycleStage CastStage => castState.Stage;
        public TimingRuntimeMode CurrentMode => GetEffectiveMode();
        public TimingQuality CurrentQuality => ClassifyQuality(lastDecision.PredictionConfidence);
        public TimingDecisionSource LastDecisionSource => lastDecision.Source;
        public TimingDecisionReason LastDecisionReason => lastDecision.Reason;
        public string LastDecisionNote => lastDecision.Note ?? string.Empty;
        public float LastPredictionConfidence => lastDecision.PredictionConfidence;
        public bool ConflictDetected => conflictQuarantine;
        public bool FailureQuarantined => failureQuarantine;
        public string LastHotPathFailure => lastHotPathFailure;
        public string LastSuppressionReason => lastSuppressionReason;
        public string LastRuntimeResetReason => lastRuntimeResetReason;
        public bool IsDryRunEnabled => CurrentMode != TimingRuntimeMode.Active;

        public AnimationLock() => dynamicFloor = new DynamicFloor(Config.DynamicFloorWindow);

        public TimingDecisionTrace[] GetRecentDecisions() => decisionJournal.SnapshotNewestFirst();

        public override void ResetRuntime(RuntimeResetReason reason)
        {
            var clearConflict = reason is RuntimeResetReason.Enable or RuntimeResetReason.Manual or RuntimeResetReason.ConflictRecovery or RuntimeResetReason.ModuleStateChange;
            var clearFailure = reason is RuntimeResetReason.Enable or RuntimeResetReason.Manual or RuntimeResetReason.ModuleStateChange;
            ResetRuntimeState(reason, clearConflict, clearFailure, reason.ToString());
        }

        public override unsafe void Enable()
        {
            Game.OnUseAction += UseAction;
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
            Game.OnReceiveActionEffect += ReceiveActionEffect;
            Game.OnUpdate += Update;
            Game.OnNetworkMessageDelegate += NetworkMessage;
        }

        public override unsafe void Disable()
        {
            Game.OnUseAction -= UseAction;
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
            Game.OnReceiveActionEffect -= ReceiveActionEffect;
            Game.OnUpdate -= Update;
            Game.OnNetworkMessageDelegate -= NetworkMessage;
        }

        private unsafe void UseAction(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted, bool ret)
            => GuardHotPath(nameof(UseAction), () => { if (ret) RegisterInstantPrediction(actionType, actionID); });

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret)
            => GuardHotPath(nameof(UseActionLocation), () => { if (ret != 0) RegisterInstantPrediction((ActionType)actionType, actionID); });

        private unsafe void CastBegin(ulong objectID, nint packetData)
            => GuardHotPath(nameof(CastBegin), () =>
            {
                var actionManager = Game.actionManager;
                if (actionManager == null || actionManager->castActionId == 0)
                {
                    castState = CastTrackerState.Idle();
                    return;
                }

                castState = new CastTrackerState
                {
                    Stage = CastLifecycleStage.Casting,
                    Sequence = actionManager->currentSequence,
                    ActionId = ActionManager.GetSpellIdForAction(actionManager->castActionType, actionManager->castActionId),
                    Context = GetCurrentContext(),
                };
            });

        private void CastInterrupt(nint actionManager)
            => GuardHotPath(nameof(CastInterrupt), () =>
            {
                if (castState.Stage == CastLifecycleStage.Idle)
                    return;

                RecordDecision(TimingDecisionSource.RuntimeControl, TimingDecisionReason.CastInterrupted, TimingActionKind.Cast, castState.ActionId, castState.Sequence, 0, 0, 0, 0, 0, "Cast interrupted before a correlated response arrived.");
                castState = CastTrackerState.Idle();
            });

        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds, float oldLock, float newLock)
            => GuardHotPath(nameof(ReceiveActionEffect), () => HandleReceiveActionEffect(casterPtr, header, oldLock, newLock));

        private void NetworkMessage(nint packet)
            => GuardHotPath(nameof(NetworkMessage), () => packetTracker.RecordPacket(packet));

        private void Update()
            => GuardHotPath(nameof(Update), HandleUpdate);

        private unsafe void HandleReceiveActionEffect(Character* casterPtr, ActionEffectHandler.Header* header, float oldLock, float newLock)
        {
            if ((nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address || TimingMath.NearlyEqual(oldLock, newLock))
                return;

            if (!TimingMath.NearlyEqual(newLock, header->AnimationLock))
            {
                EnterConflictQuarantine("Server animation lock did not match the post-hook lock value.", header->SpellId, header->SourceSequence, newLock, oldLock, TimingDecisionReason.ResponseMismatch);
                return;
            }

            if (LooksExternallyModified(newLock))
            {
                EnterConflictQuarantine($"Detected a fractional lock pattern at {F2MS(newLock)} ms.", header->SpellId, header->SourceSequence, newLock, oldLock, TimingDecisionReason.ConflictDetected);
                return;
            }

            var actionId = header->SpellId;
            var sequence = header->SourceSequence;
            LastActionID = actionId;

            pendingPredictions.TryGetValue(sequence, out var pending);
            pendingPredictions.Remove(sequence);

            var castMatched = TryConsumeCastState(sequence, actionId);
            if (castMatched && pending == null)
            {
                LearnCastTax(actionId, newLock);
                RecordDecision(TimingDecisionSource.ServerCorrection, TimingDecisionReason.NoCorrelation, TimingActionKind.Cast, actionId, sequence, 0, newLock, newLock, 0, GetCastPredictionConfidence(actionId, GetCurrentContext()), "Cast response arrived without an active prediction to arbitrate.");
                return;
            }

            if (pending == null)
            {
                LearnAnimationLock(actionId, GetCurrentContext(), newLock);
                RecordDecision(TimingDecisionSource.ServerCorrection, TimingDecisionReason.NoCorrelation, TimingActionKind.Instant, actionId, sequence, 0, newLock, newLock, 0, GetActionPredictionConfidence(actionId, GetCurrentContext()), "Local response had no pending prediction; the controller observed but did not rewrite.");
                return;
            }

            if (pending.Kind == TimingActionKind.Cast)
                LearnCastTax(pending.ActionId, newLock, pending.Context);
            else
                LearnAnimationLock(pending.ActionId, pending.Context, newLock);

            ResolvePendingPrediction(pending, oldLock, newLock);
        }

        private void HandleUpdate()
        {
            ApplyRuntimeConfig();

            var deltaSeconds = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            packetTracker.Update(deltaSeconds);

            var inCombat = DalamudApi.Condition[ConditionFlag.InCombat];
            outOfCombatIdleTimer = inCombat ? 0f : outOfCombatIdleTimer + deltaSeconds;
            if ((saveLearnedData || saveRuntimeStats) && ShouldFlushConfigSave(inCombat))
            {
                Config.Save(checkModules: false);
                ResetDirtySaveState();
            }

            EvaluateContextTransitions();
            UpdateCastPrediction();
        }

        private unsafe void RegisterInstantPrediction(ActionType actionType, uint actionID)
        {
            packetTracker.MarkActionIssued();

            var actionManager = Game.actionManager;
            if (actionManager == null)
                return;

            var resolvedActionId = ActionManager.GetSpellIdForAction(actionType, actionID);
            var context = GetCurrentContext();
            var baseLock = GetActionPredictionBaseLock(resolvedActionId, context, out var confidence);
            var floor = dynamicFloor.Floor;
            var predictedLock = baseLock + floor;
            var canApply = CurrentMode == TimingRuntimeMode.Active && actionManager->animationLock <= ExistingActionLockThreshold;
            var issueReason = canApply ? TimingDecisionReason.PredictedInstantLock : CurrentMode == TimingRuntimeMode.Active ? TimingDecisionReason.ExistingLockSuppressed : TimingDecisionReason.DryRunSuppressed;
            var note = canApply ? "Instant prediction applied." : CurrentMode == TimingRuntimeMode.Active ? $"Existing animation lock {F2MS(actionManager->animationLock)} ms suppressed prediction." : $"Prediction retained for observation in mode {CurrentMode}.";

            var pending = new PendingPrediction
            {
                Sequence = actionManager->currentSequence,
                ActionId = resolvedActionId,
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

            pendingPredictions[pending.Sequence] = pending;
            if (canApply)
                actionManager->animationLock = predictedLock;
            else
                lastSuppressionReason = note;

            RecordDecision(TimingDecisionSource.InstantPrediction, issueReason, TimingActionKind.Instant, pending.ActionId, pending.Sequence, pending.PredictedFullLock, 0, canApply ? pending.PredictedFullLock : 0, 0, pending.PredictionConfidence, note);
        }

        private unsafe void UpdateCastPrediction()
        {
            if (!Config.EnableCastLockPrediction || castState.Stage != CastLifecycleStage.Casting)
                return;

            var actionManager = Game.actionManager;
            if (actionManager == null || actionManager->castActionId == 0)
                return;

            var remaining = actionManager->castTime - actionManager->elapsedCastTime;
            if (remaining > TimingMath.CastCompletionWindow)
                return;

            packetTracker.MarkActionIssued();

            var baseLock = GetCastPredictionBaseLock(castState.ActionId, castState.Context, out var confidence);
            var floor = dynamicFloor.Floor;
            var predictedLock = baseLock + floor;
            var canApply = CurrentMode == TimingRuntimeMode.Active && actionManager->animationLock <= ExistingCastLockThreshold;
            var issueReason = canApply ? TimingDecisionReason.PredictedCastLock : CurrentMode == TimingRuntimeMode.Active ? TimingDecisionReason.ExistingLockSuppressed : TimingDecisionReason.DryRunSuppressed;
            var note = canApply ? "Cast completion prediction applied." : CurrentMode == TimingRuntimeMode.Active ? $"Existing residual cast lock {F2MS(actionManager->animationLock)} ms suppressed prediction." : $"Cast prediction retained for observation in mode {CurrentMode}.";

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

            if (canApply)
                actionManager->animationLock = predictedLock;
            else
                lastSuppressionReason = note;

            RecordDecision(TimingDecisionSource.CastPrediction, issueReason, TimingActionKind.Cast, pending.ActionId, pending.Sequence, pending.PredictedFullLock, 0, canApply ? pending.PredictedFullLock : 0, 0, pending.PredictionConfidence, note);
        }

        private unsafe void ResolvePendingPrediction(PendingPrediction pending, float oldLock, float newLock)
        {
            if (!pending.WasApplied)
            {
                RecordDecision(TimingDecisionSource.ServerCorrection, pending.IssueReason == TimingDecisionReason.DryRunSuppressed ? TimingDecisionReason.DryRunSuppressed : TimingDecisionReason.LearningOnly, pending.Kind, pending.ActionId, pending.Sequence, pending.PredictedFullLock, newLock, newLock, 0, pending.PredictionConfidence, pending.Note);
                return;
            }

            if (CurrentMode != TimingRuntimeMode.Active)
            {
                var modeNote = $"Prediction was applied earlier but response was resolved in mode {CurrentMode}; no final rewrite was performed.";
                lastSuppressionReason = modeNote;
                RecordDecision(TimingDecisionSource.ServerCorrection, TimingDecisionReason.DryRunSuppressed, pending.Kind, pending.ActionId, pending.Sequence, pending.PredictedFullLock, newLock, newLock, 0, pending.PredictionConfidence, modeNote);
                return;
            }

            var measuredRtt = pending.PredictedFullLock - oldLock;
            if (!TimingMath.IsFiniteAndInRange(measuredRtt, TimingMath.MinimumMeasuredRtt, TimingMath.MaximumMeasuredRtt))
            {
                var invalidNote = $"Measured RTT {F2MS(measuredRtt)} ms was outside the trusted range.";
                lastSuppressionReason = invalidNote;
                RecordDecision(TimingDecisionSource.ServerCorrection, TimingDecisionReason.InvalidMeasurement, pending.Kind, pending.ActionId, pending.Sequence, pending.PredictedFullLock, newLock, newLock, measuredRtt, pending.PredictionConfidence, invalidNote);
                return;
            }

            LastRTT = measuredRtt;
            dynamicFloor.AddSample(measuredRtt);
            if (measuredRtt <= pending.FloorAtIssue + TimingMath.LockEqualityEpsilon)
            {
                LastCorrection = 0;
                LastVarianceBuffer = 0;
                LastAdjustedLock = newLock;
                RecordDecision(TimingDecisionSource.ServerCorrection, TimingDecisionReason.RttBelowFloor, pending.Kind, pending.ActionId, pending.Sequence, pending.PredictedFullLock, newLock, newLock, measuredRtt, pending.PredictionConfidence, "Observed RTT was already inside the learned floor; the runtime kept the server lock.");
                return;
            }

            var weight = packetTracker.GetRTTWeight();
            rttEstimator.AddSample(measuredRtt, weight);
            var correction = newLock - pending.PredictedBaseLock;
            var adaptiveVarianceBuffer = rttEstimator.VarianceBuffer * (1f + (1f - pending.PredictionConfidence) * 0.5f);
            var confidenceGuard = pending.FloorAtIssue * (1f - pending.PredictionConfidence) * 0.5f;
            var adjustedAnimationLock = Math.Max(oldLock + correction + adaptiveVarianceBuffer + confidenceGuard, 0f);

            LastCorrection = correction;
            LastVarianceBuffer = adaptiveVarianceBuffer;
            LastAdjustedLock = adjustedAnimationLock;

            Game.actionManager->animationLock = adjustedAnimationLock;
            if (newLock > adjustedAnimationLock)
            {
                Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                Config.TotalActionsReduced++;
                MarkRuntimeStatsDirty();
            }

            var finalNote = $"weight={weight:F2}, confidence={pending.PredictionConfidence:P0}";
            if (confidenceGuard > 0)
                finalNote += $", guard={F2MS(confidenceGuard)} ms";

            RecordDecision(TimingDecisionSource.ServerCorrection, pending.Kind == TimingActionKind.Cast ? TimingDecisionReason.AppliedCastCorrection : TimingDecisionReason.AppliedInstantCorrection, pending.Kind, pending.ActionId, pending.Sequence, pending.PredictedFullLock, newLock, adjustedAnimationLock, measuredRtt, pending.PredictionConfidence, finalNote);
        }

        private void LearnAnimationLock(uint actionId, GameContext context, float observedLock)
        {
            if (conflictQuarantine || !Config.LearnAnimationLocks)
                return;

            if (Config.LockDb.RecordLock(actionId, context, observedLock))
                MarkLearnedDataDirty();
        }

        private void LearnCastTax(uint actionId, float observedLock, GameContext? context = null)
        {
            if (conflictQuarantine || !Config.LearnCastTax)
                return;

            if (Config.CastTaxDb.RecordTax(actionId, context ?? GetCurrentContext(), observedLock))
                MarkLearnedDataDirty();
        }

        private void GuardHotPath(string source, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                HandleHotPathFailure(source, exception);
            }
        }

        private void HandleHotPathFailure(string source, Exception exception)
        {
            hotPathFailureCount++;
            lastHotPathFailure = $"{source}: {exception.GetType().Name}: {exception.Message}";
            DalamudApi.LogError($"Timing controller failure in {source}", exception);

            failureQuarantine = true;
            lastSuppressionReason = $"Timing runtime entered failure quarantine after {source}.";
            ResetRuntimeState(RuntimeResetReason.RuntimeFailure, false, false, lastHotPathFailure);

            DalamudApi.ShowNotification("Tsunippy timing runtime entered failure quarantine. Diagnostics now report the last failure.", Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
        }

        private void EnterConflictQuarantine(string reason, uint actionId, ushort sequence, float serverLock, float oldLock, TimingDecisionReason decisionReason)
        {
            conflictQuarantine = true;
            lastSuppressionReason = reason;
            ResetRuntimeState(RuntimeResetReason.ConflictDetected, false, false, reason);
            RecordDecision(TimingDecisionSource.RuntimeControl, decisionReason, castState.Stage != CastLifecycleStage.Idle ? TimingActionKind.Cast : TimingActionKind.Instant, actionId, sequence, 0, serverLock, serverLock, 0, 0, reason);
            PrintError($"{reason} The timing controller switched into quarantine mode.");
        }

        private void ResetRuntimeState(RuntimeResetReason reason, bool clearConflictQuarantine, bool clearFailureQuarantine, string note)
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
            LastRTT = 0;
            LastCorrection = 0;
            LastVarianceBuffer = 0;
            LastAdjustedLock = 0;
            LastActionID = 0;
            lastRuntimeResetReason = reason.ToString();

            if (reason != RuntimeResetReason.RuntimeFailure)
            {
                hotPathFailureCount = 0;
                if (clearFailureQuarantine)
                    lastHotPathFailure = string.Empty;
            }

            RecordDecision(TimingDecisionSource.RuntimeControl, TimingDecisionReason.RuntimeReset, TimingActionKind.Instant, 0, 0, 0, 0, 0, 0, 0, $"{reason}: {note}");
        }

        private void EvaluateContextTransitions()
        {
            var betweenAreas = DalamudApi.Condition[ConditionFlag.BetweenAreas];
            if (betweenAreas && !wasBetweenAreas)
                ResetRuntimeState(RuntimeResetReason.ZoneTransition, false, false, "Zone transition detected.");
            wasBetweenAreas = betweenAreas;

            var localPlayerId = DalamudApi.ObjectTable.LocalPlayer?.GameObjectId;
            if (localPlayerId != null)
            {
                var currentId = (long)localPlayerId.Value;
                if (lastLocalPlayerId.HasValue && lastLocalPlayerId.Value != currentId)
                    ResetRuntimeState(RuntimeResetReason.PlayerChanged, false, false, "Local player object changed.");
                lastLocalPlayerId = currentId;
            }
            else
            {
                lastLocalPlayerId = null;
            }
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

        private void ApplyRuntimeConfig()
        {
            rttEstimator.Alpha = Config.JKAlpha;
            rttEstimator.Beta = Config.JKBeta;
            rttEstimator.K = Config.JKK;
            dynamicFloor.ScalingFactor = Config.DynamicFloorScaling;
        }

        private float GetActionPredictionBaseLock(uint actionId, GameContext context, out float confidence)
        {
            var entry = Config.LockDb.GetEntry(actionId, context);
            confidence = entry != null ? Math.Max(entry.Confidence, MinimumPredictionConfidence) : MinimumPredictionConfidence;
            return Config.LockDb.GetLock(actionId, context, Game.DefaultClientAnimationLock);
        }

        private float GetCastPredictionBaseLock(uint actionId, GameContext context, out float confidence)
        {
            var entry = Config.CastTaxDb.GetEntry(actionId, context);
            confidence = entry != null ? Math.Max(entry.Confidence, MinimumPredictionConfidence) : MinimumPredictionConfidence;
            return Config.LearnCastTax ? Config.CastTaxDb.GetTax(actionId, context, Config.DefaultCasterTax) : Config.DefaultCasterTax;
        }

        private float GetActionPredictionConfidence(uint actionId, GameContext context)
        {
            var entry = Config.LockDb.GetEntry(actionId, context);
            return entry != null ? Math.Max(entry.Confidence, MinimumPredictionConfidence) : MinimumPredictionConfidence;
        }

        private float GetCastPredictionConfidence(uint actionId, GameContext context)
        {
            var entry = Config.CastTaxDb.GetEntry(actionId, context);
            return entry != null ? Math.Max(entry.Confidence, MinimumPredictionConfidence) : MinimumPredictionConfidence;
        }

        private TimingRuntimeMode GetEffectiveMode()
        {
            if (failureQuarantine)
                return TimingRuntimeMode.FailureQuarantined;
            if (conflictQuarantine)
                return TimingRuntimeMode.ConflictQuarantined;
            if (Config.EnableDryRun)
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

        private static GameContext GetCurrentContext()
        {
            try
            {
                return DalamudApi.ClientState.IsPvP ? GameContext.PvP : GameContext.PvE;
            }
            catch
            {
                return GameContext.PvE;
            }
        }

        private static bool LooksExternallyModified(float lockValue)
            => lockValue % 0.01f is >= 0.0005f and <= 0.0095f;

        private void RecordDecision(TimingDecisionSource source, TimingDecisionReason reason, TimingActionKind kind, uint actionId, ushort sequence, float predictedLock, float serverLock, float finalLock, float measuredRtt, float predictionConfidence, string note)
        {
            var trace = new TimingDecisionTrace(DateTime.UtcNow, source, reason, CurrentMode, ClassifyQuality(predictionConfidence), kind, actionId, sequence, predictedLock, serverLock, finalLock, measuredRtt, predictionConfidence, note ?? string.Empty);
            lastDecision = trace;
            decisionJournal.Add(trace);

            if (!Config.EnableLogging)
                return;

            var log = new StringBuilder().Append(trace.Mode).Append(" | ").Append(trace.Source).Append(" | ").Append(trace.Reason).Append(" | ").Append(trace.ActionKind).Append(" ").Append(trace.ActionId).Append(" seq ").Append(trace.Sequence);
            if (trace.PredictedLock > 0)
                log.Append($" | pred {F2MS(trace.PredictedLock)} ms");
            if (trace.ServerLock > 0)
                log.Append($" | server {F2MS(trace.ServerLock)} ms");
            if (trace.FinalLock > 0)
                log.Append($" | final {F2MS(trace.FinalLock)} ms");
            if (trace.RTT > 0)
                log.Append($" | rtt {F2MS(trace.RTT)} ms");
            log.Append($" | conf {trace.PredictionConfidence:P0}");
            if (!string.IsNullOrEmpty(trace.Note))
                log.Append(" | ").Append(trace.Note);
            PrintLog(log.ToString());
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Animation Lock Reduction", ref Config.EnableAnimLockComp))
                Config.Save();
            PluginUI.SetItemTooltip("Authoritative timing controller for instant and cast lock prediction, correction, learning, and runtime quarantine.");

            if (Config.EnableAnimLockComp)
            {
                ImGui.Columns(2, "AnimlockColumns", false);
                if (ImGui.Checkbox("Enable Logging", ref Config.EnableLogging))
                    Config.Save(checkModules: false);

                ImGui.NextColumn();
                var dryRun = Config.EnableDryRun;
                if (ImGui.Checkbox("Dry Run", ref dryRun))
                {
                    Config.EnableDryRun = dryRun;
                    Config.Save(checkModules: false);
                }
                PluginUI.SetItemTooltip("Keeps the timing controller running without writing animation locks.");
                ImGui.Columns(1);

                if (ImGui.Checkbox("Learn Animation Locks", ref Config.LearnAnimationLocks))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Learns per-action animation lock values from live responses.");

                ImGui.TextUnformatted($"Runtime Mode: {CurrentMode}");
                ImGui.TextUnformatted($"Decision Quality: {CurrentQuality}");
                if (!string.IsNullOrEmpty(lastSuppressionReason))
                    ImGui.TextWrapped($"Last Suppression: {lastSuppressionReason}");

                if (ImGui.Button("Reset Runtime State"))
                    ResetRuntimeState(RuntimeResetReason.Manual, true, true, "Manual runtime reset.");
                PluginUI.SetItemTooltip("Clears transient predictions, estimators, packet windows, and quarantine state without wiping learned data.");

                if (ImGui.TreeNode("Advanced RTT Settings"))
                {
                    var alpha = Config.JKAlpha;
                    if (ImGui.SliderFloat("Alpha (SRTT smoothing)", ref alpha, 0.01f, 0.5f, "%.3f"))
                    {
                        Config.JKAlpha = alpha;
                        Config.Save(checkModules: false);
                    }

                    var beta = Config.JKBeta;
                    if (ImGui.SliderFloat("Beta (Variance smoothing)", ref beta, 0.01f, 0.5f, "%.3f"))
                    {
                        Config.JKBeta = beta;
                        Config.Save(checkModules: false);
                    }

                    var k = Config.JKK;
                    if (ImGui.SliderFloat("K (Variance multiplier)", ref k, 0.5f, 4.0f, "%.2f"))
                    {
                        Config.JKK = k;
                        Config.Save(checkModules: false);
                    }

                    var scaling = Config.DynamicFloorScaling;
                    if (ImGui.SliderFloat("Floor Scaling", ref scaling, 0.5f, 1.0f, "%.2f"))
                    {
                        Config.DynamicFloorScaling = scaling;
                        Config.Save(checkModules: false);
                    }

                    if (ImGui.Button("Reset to Defaults"))
                    {
                        Config.JKAlpha = 0.125f;
                        Config.JKBeta = 0.25f;
                        Config.JKK = 2.0f;
                        Config.DynamicFloorScaling = 0.85f;
                        ResetRuntimeState(RuntimeResetReason.Manual, true, true, "Advanced parameters reset to defaults.");
                        Config.Save(checkModules: false);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Reset Learned Locks"))
                    {
                        Config.LockDb.Reset();
                        Config.Save(checkModules: false);
                    }

                    ImGui.TreePop();
                }
            }

            ImGui.TextUnformatted($"Reduced a total time of {TimeSpan.FromSeconds(Config.TotalAnimationLockReduction):d\\:hh\\:mm\\:ss} from {Config.TotalActionsReduced} actions");
        }

        private void MarkLearnedDataDirty()
        {
            saveLearnedData = true;
            pendingLearnedEntries++;
        }

        private void MarkRuntimeStatsDirty() => saveRuntimeStats = true;

        private bool ShouldFlushConfigSave(bool inCombat)
        {
            if (DalamudApi.Condition[ConditionFlag.BetweenAreas])
                return true;
            if (inCombat)
                return false;

            if (saveLearnedData)
            {
                var learnedDelay = pendingLearnedEntries >= SaveFlushBatchSize ? LearnedBatchSaveIdleDelay : LearnedSaveIdleDelay;
                if (outOfCombatIdleTimer >= learnedDelay)
                    return true;
            }

            return saveRuntimeStats && outOfCombatIdleTimer >= RuntimeStatsSaveIdleDelay;
        }

        private void ResetDirtySaveState()
        {
            saveLearnedData = false;
            saveRuntimeStats = false;
            outOfCombatIdleTimer = 0f;
            pendingLearnedEntries = 0;
        }

        public void NotifyLearnedDataChanged() => MarkLearnedDataDirty();
    }
}
