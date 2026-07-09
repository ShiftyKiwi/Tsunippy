using System;
using System.IO;
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

        // Jacobson/Karels tuning parameters
        public float JKAlpha = 0.125f;
        public float JKBeta = 0.25f;
        public float JKK = 2.0f;

        // Dynamic floor tuning
        public float DynamicFloorScaling = 0.85f;
        public int DynamicFloorWindow = 100;

        // Context-aware lock database
        public LockDatabase LockDb = new();

        // Lifetime statistics
        public ulong TotalActionsReduced = 0ul;
        public double TotalAnimationLockReduction = 0d;
    }
}

namespace Tsunippy.Modules
{
    public class AnimationLock : Module
    {
        private const float LockEqualityEpsilon = 0.0005f;

        public override bool IsEnabled
        {
            get => Config.EnableAnimLockComp;
            set => Config.EnableAnimLockComp = value;
        }

        public override int DrawOrder => 1;

        private readonly JacobsonKarels rttEstimator = new();
        private readonly DualRttEstimator dualRttEstimator = new();
        private readonly DynamicFloor dynamicFloor;
        private readonly PacketTracker packetTracker = new();
        private readonly ModelEpoch modelEpoch = new();
        private readonly PredictionTracker predictions = new();
        private readonly RecentIssuedActionTracker recentIssuedActions = new();
        private readonly ReplayLog replayLog;

        private bool isCasting;
        private bool enableAnticheat;
        private bool saveLearnedData;
        private bool saveRuntimeStats;
        private bool wasBetweenAreas;
        private bool wasPlayerPresent;
        private bool safeModeActive;
        private float outOfCombatIdleTimer;
        private int pendingLearnedEntries;
        private int predictionMismatchStreak;
        private int observedOutlierStreak;
        private int pathShiftStreak;
        private long lastActionTick;
        private long lastPredictedTick;
        private ushort lastPredictedSequence;
        private uint lastPredictedActionId;
        private float lastPredictedLock;
        private ulong lastPredictedEpoch;
        private uint lastTerritoryType = uint.MaxValue;
        private ProfileSettings profileSettings;
        private string lastPredictionReason = "none";
        private string lastSafeModeReason = "startup";

        private const float LearnedSaveIdleDelay = 15f;
        private const float LearnedBatchSaveIdleDelay = 5f;
        private const float RuntimeStatsSaveIdleDelay = 120f;
        private const int SaveFlushBatchSize = 8;

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
        public bool ConflictDetected => enableAnticheat;
        public bool IsDryRunEnabled => enableAnticheat || Config.EnableDryRun;
        public ulong CurrentEpoch => modelEpoch.Current;
        public string LastEpochResetReason => modelEpoch.LastResetReason;
        public TimeSpan TimeSinceEpochReset => modelEpoch.TimeSinceReset;
        public int StalePredictionsInvalidated => modelEpoch.StalePredictionsInvalidated + predictions.StaleEpochCount;
        public int PendingPredictionCount => predictions.Count;
        public int ExpiredPredictionCount => predictions.ExpiredCount;
        public string LastPredictionReason => lastPredictionReason;
        public bool SafeModeActive => safeModeActive;
        public string LastSafeModeReason => lastSafeModeReason;
        public string EstimatorMaturity => rttEstimator.EstimatorMaturity;
        public float VarianceTrustFactor => rttEstimator.VarianceTrustFactor;
        public bool IsRttWarm => rttEstimator.IsWarm;
        public ConnectionState ConnectionClassification => dualRttEstimator.Classification;
        public TsunippyProfile EffectiveProfile => profileSettings.EffectiveProfile;
        public FloorMode CurrentFloorMode => dynamicFloor.Mode;
        public float RawMinRTT => dynamicFloor.RawMinRTT;
        public string LastFloorAdjustmentReason => dynamicFloor.LastAdjustmentReason;
        public DecisionTrace LastDecision => replayLog.Last;
        public int ReplayRecordCount => replayLog.Count;

        public AnimationLock()
        {
            dynamicFloor = new DynamicFloor(Config.DynamicFloorWindow);
            replayLog = new ReplayLog(Config.ReplayLogCapacity);
            profileSettings = ProfileSettings.From(Config.Profile, ConnectionState.WarmingUp);
            ResetRuntimeState();
        }

        private float GetBaseLock(uint actionID, GameContext context)
            => Config.LockDb.GetLock(actionID, context, Game.DefaultClientAnimationLock);

        private float GetPredictedLock(uint actionID, GameContext context, out float baseLock)
        {
            RefreshRuntimeSettings();
            baseLock = GetBaseLock(actionID, context);
            return baseLock + dynamicFloor.Floor * profileSettings.PredictionAggressiveness;
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

        private unsafe void ApplyPredictedLock(ActionType actionType, uint actionID)
        {
            var existingLock = Game.actionManager->animationLock;
            if (!NearlyEqual(existingLock, Game.DefaultClientAnimationLock))
                return;

            var now = Environment.TickCount64;
            // Fifteen seconds is long enough to avoid ordinary GCD downtime but short
            // enough to avoid carrying timing assumptions across AFK, wipes, or zoning gaps.
            if (lastActionTick != 0 && now - lastActionTick > 15_000)
                AdvanceEpoch("long idle gap before action", resetRttModel: true);

            lastActionTick = now;
            var id = ActionManager.GetSpellIdForAction(actionType, actionID);
            var context = GetCurrentContext();
            var predictedLock = GetPredictedLock(id, context, out var baseLock);
            var sequence = Game.actionManager->currentSequence;

            predictions.Add(new PendingPrediction
            {
                Sequence = sequence,
                ActionId = id,
                IsPvP = context == GameContext.PvP,
                BaseLock = baseLock,
                PredictedLock = predictedLock,
                OriginalLockAtPrediction = existingLock,
                CreatedTick = now,
                ExpiresTick = now + (long)profileSettings.PredictionTtlMilliseconds,
                ModelEpoch = modelEpoch.Current,
                Source = actionType.ToString(),
            });

            lastPredictedTick = now;
            lastPredictedSequence = sequence;
            lastPredictedActionId = id;
            lastPredictedLock = predictedLock;
            lastPredictedEpoch = modelEpoch.Current;
            RecordDecision(sequence, id, context, baseLock, existingLock, predictedLock, predictedLock, existingLock,
                0f, 0f, "pre-applied pending lock", string.Empty, source: actionType.ToString(),
                ownership: DecisionOwnership.PreAppliedPendingLock);

            if (!IsDryRunEnabled)
            {
                Game.actionManager->animationLock = MathF.Max(existingLock, predictedLock);
            }

            packetTracker.MarkActionIssued();
            lastPredictionReason = $"pending {id} seq {sequence}";
            if (Config.EnableLogging)
                DalamudApi.LogDebug($"Applying {F2MS(predictedLock)} ms animation lock for {actionType} {actionID} ({id}), floor={F2MS(dynamicFloor.Floor)} ms, epoch={modelEpoch.Current}");
        }

        private unsafe void UseAction(ActionManager* actionManager, ActionType actionType, uint actionID,
            ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId,
            bool* outOptAreaTargeted, bool ret)
        {
            if (!ret)
                return;

            RecordIssuedLocalAction(actionType, actionID, "UseAction");
            ApplyPredictedLock(actionType, actionID);
        }

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID,
            ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            if (ret == 0)
                return;

            RecordIssuedLocalAction((ActionType)actionType, actionID, "UseActionLocation");
            ApplyPredictedLock((ActionType)actionType, actionID);
        }

        private void CastBegin(uint casterEntityId, nint packetData)
        {
            if (casterEntityId != DalamudApi.ObjectTable.LocalPlayer?.EntityId)
                return;

            isCasting = !IsCastPredictionOwnerActive();
        }

        private void CastInterrupt(nint actionManager)
            => isCasting = false;

        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr,
            Vector3* targetPos, ActionEffectHandler.Header* header,
            ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds,
            float oldLock, float newLock)
        {
            try
            {
                if (NearlyEqual(oldLock, newLock) || (nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address)
                    return;

                var castPrediction = global::Tsunippy.Modules.Modules.GetInstance<CastLockPrediction>();
                if (castPrediction?.ShouldOwnCastResponse(header->SourceSequence, header->SpellId) == true)
                {
                    isCasting = false;
                    lastPredictionReason = "cast response owned by cast predictor";
                    RecordDecision(header->SourceSequence, header->SpellId, GetCurrentContext(), 0f, newLock, 0f,
                        newLock, oldLock, 0f, 0f, "cast prediction owner", string.Empty, source: "cast",
                        ownership: DecisionOwnership.CastPrediction);
                    return;
                }

                if (isCasting && !IsCastPredictionOwnerActive())
                {
                    isCasting = false;
                    newLock += oldLock;

                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");

                    lastPredictionReason = "legacy cast fallback";
                    RecordDecision(header->SourceSequence, header->SpellId, GetCurrentContext(), 0f, newLock, 0f,
                        newLock, oldLock, 0f, 0f, "legacy cast receive", string.Empty, source: "legacy-receive",
                        ownership: DecisionOwnership.LegacyReceive);
                    return;
                }

                if (!NearlyEqual(newLock, header->AnimationLock))
                {
                    observedOutlierStreak++;
                    MaybeResetForRepeatedOutliers("animation lock offset mismatch");
                    PrintError("Mismatched animation lock offset! This can be caused by another plugin affecting the animation lock.");
                    return;
                }

                var isUsingAlexander = newLock % 0.01 is >= 0.0005f and <= 0.0095f;
                if (!enableAnticheat && isUsingAlexander)
                {
                    enableAnticheat = true;
                    PrintError($"Unexpected lock of {F2MS(newLock)} ms, temporary dry run has been enabled. Please disable any other programs or plugins that may be affecting the animation lock.");
                }

                var sequence = header->SourceSequence;
                var actionID = header->SpellId;
                var now = Environment.TickCount64;
                var hadPrediction = predictions.TryConsume(sequence, actionID, modelEpoch.Current, now, out var prediction);
                LastActionID = actionID;

                var context = GetCurrentContext();
                var lockEntryBefore = Config.LockDb.GetEntry(actionID, context);
                var outliersBefore = lockEntryBefore?.OutlierStreak ?? 0;
                if (!enableAnticheat && Config.LearnAnimationLocks && Config.LockDb.RecordLock(actionID, context, newLock))
                    MarkLearnedDataDirty();

                var lockEntry = Config.LockDb.GetEntry(actionID, context);
                if ((lockEntry?.OutlierStreak ?? 0) > outliersBefore)
                {
                    observedOutlierStreak++;
                    MaybeResetForRepeatedOutliers("learned lock outliers");
                }
                else
                {
                    observedOutlierStreak = Math.Max(0, observedOutlierStreak - 1);
                }

                if (!hadPrediction)
                {
                    LastRTT = 0;
                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    lastPredictionReason = predictions.LastRejectionReason;
                    RegisterPredictionMismatch(predictions.LastRejectionReason);
                    RecordDecision(sequence, actionID, context, 0f, newLock, 0f, newLock, oldLock, 0f, 0f,
                        "no compensation", predictions.LastRejectionReason, source: "receive",
                        ownership: DecisionOwnership.RejectedNoCompensation);

                    if (Config.EnableLogging)
                        PrintLog($"Action: {actionID} ({F2MS(newLock)} ms) || No correlated prediction, skipped RTT correction");

                    return;
                }

                predictionMismatchStreak = 0;
                var currentFloor = dynamicFloor.Floor;
                var lastRecordedLock = prediction.BaseLock;

                var correction = newLock - lastRecordedLock;
                var rtt = prediction.PredictedLock - oldLock;
                LastRTT = rtt;

                if (rtt <= 0 || !float.IsFinite(rtt) || rtt > 5f)
                {
                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    lastPredictionReason = "invalid RTT sample";
                    RegisterPredictionMismatch(lastPredictionReason);
                    RecordDecision(sequence, actionID, context, prediction.BaseLock, newLock, prediction.PredictedLock,
                        newLock, oldLock, rtt, 0f, "rejected invalid RTT", lastPredictionReason, source: prediction.Source,
                        ownership: DecisionOwnership.RejectedNoCompensation);
                    return;
                }

                dynamicFloor.AddSample(rtt);
                if (rtt <= currentFloor)
                {
                    if (Config.EnableLogging)
                        PrintLog($"RTT ({F2MS(rtt)} ms) was lower than floor ({F2MS(currentFloor)} ms), no adjustments made");

                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    lastPredictionReason = "RTT below floor";
                    RecordDecision(sequence, actionID, context, prediction.BaseLock, newLock, prediction.PredictedLock,
                        newLock, oldLock, rtt, 0f, "floor guard", lastPredictionReason, source: prediction.Source,
                        ownership: DecisionOwnership.RejectedNoCompensation);
                    return;
                }

                var weight = Math.Clamp(packetTracker.GetRTTWeight() * profileSettings.PacketSpikeStrictness, 0.05f, 1f);
                rttEstimator.AddSample(rtt, weight);
                dualRttEstimator.AddSample(rtt, weight);

                RefreshRuntimeSettings();
                if (MaybeResetForPathShift())
                {
                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    RecordDecision(sequence, actionID, context, prediction.BaseLock, newLock, prediction.PredictedLock,
                        newLock, oldLock, rtt, weight, "epoch reset", "sustained RTT path shift", source: prediction.Source,
                        ownership: DecisionOwnership.RejectedNoCompensation);
                    return;
                }

                var varianceBuffer = rttEstimator.VarianceBuffer;
                LastVarianceBuffer = varianceBuffer;
                LastCorrection = correction;

                var adjustedAnimationLock = Math.Max(oldLock + correction + varianceBuffer, 0);
                LastAdjustedLock = (float)adjustedAnimationLock;
                lastPredictionReason = "accepted";

                if (!IsDryRunEnabled && float.IsFinite((float)adjustedAnimationLock) && adjustedAnimationLock < 10)
                {
                    Game.actionManager->animationLock = (float)adjustedAnimationLock;

                    Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                    Config.TotalActionsReduced++;
                    MarkRuntimeStatsDirty();
                }

                RecordDecision(sequence, actionID, context, prediction.BaseLock, newLock, prediction.PredictedLock,
                    (float)adjustedAnimationLock, oldLock, rtt, weight,
                    safeModeActive ? $"safe mode: {lastSafeModeReason}" : "accepted", string.Empty, hasFormula: true,
                    source: prediction.Source, ownership: DecisionOwnership.AcceptedServerReconciliation);

                if (!Config.EnableLogging)
                    return;

                var sb = new StringBuilder(IsDryRunEnabled ? "[DRY] " : string.Empty)
                    .Append($"Action: {actionID} ")
                    .Append(lastRecordedLock != newLock
                        ? $"({F2MS((float)lastRecordedLock)} > {F2MS(newLock)} ms)"
                        : $"({F2MS(newLock)} ms)")
                    .Append($" || RTT: {F2MS(rtt)} ms (SRTT: {F2MS(rttEstimator.SmoothedRTT)}, VAR: {F2MS(rttEstimator.RTTVariance)})");

                if (enableAnticheat)
                    sb.Append(" [Alexander detected]");

                if (!IsDryRunEnabled)
                    sb.Append($" || Lock: {F2MS(oldLock)} > {F2MS((float)adjustedAnimationLock)} ({F2MS((float)(correction + varianceBuffer)):+0;-#}) ms");

                sb.Append($" || Floor: {F2MS(dynamicFloor.Floor)} ms | Wt: {weight:F2} | Pkts: {packetTracker.TotalPacketsSent}/{packetTracker.ActionPacketsSent}");
                PrintLog(sb.ToString());
            }
            catch (Exception e)
            {
                DalamudApi.LogError($"AnimationLock.ReceiveActionEffect failed for action {LastActionID}: {e}");
                PrintError("Error in AnimationLock Module. Check Dalamud logs for details.");
            }
        }

        private void NetworkMessage(nint packet)
        {
            packetTracker.RecordPacket(packet);
        }

        private void Update()
        {
            var deltaSeconds = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            var inCombat = DalamudApi.Condition[ConditionFlag.InCombat];
            var betweenAreas = DalamudApi.Condition[ConditionFlag.BetweenAreas];
            var playerPresent = DalamudApi.ObjectTable.LocalPlayer != null;
            var territoryType = (uint)DalamudApi.ClientState.TerritoryType;

            if (lastTerritoryType != uint.MaxValue && territoryType != lastTerritoryType)
                AdvanceEpoch("territory changed", resetRttModel: true);
            lastTerritoryType = territoryType;

            if (betweenAreas && !wasBetweenAreas)
                AdvanceEpoch("zoning", resetRttModel: true);
            wasBetweenAreas = betweenAreas;

            if (wasPlayerPresent && !playerPresent)
                AdvanceEpoch("local player unavailable", resetRttModel: true);
            wasPlayerPresent = playerPresent;

            RefreshRuntimeSettings();
            var removed = predictions.Cleanup(Environment.TickCount64, modelEpoch.Current);
            modelEpoch.AddStaleInvalidations(removed);

            outOfCombatIdleTimer = inCombat ? 0f : outOfCombatIdleTimer + deltaSeconds;

            if ((saveLearnedData || saveRuntimeStats) && ShouldFlushConfigSave(inCombat))
            {
                Config.Save(checkModules: false);
                ResetDirtySaveState();
            }

            packetTracker.Update(deltaSeconds);
        }

        public void ResetFloor()
        {
            dynamicFloor.Reset();
            AdvanceEpoch("manual floor reset", resetRttModel: false);
        }

        public void ResetRttModel()
        {
            AdvanceEpoch("manual RTT reset", resetRttModel: true);
        }

        public void Relearn()
        {
            Config.LockDb.Reset();
            Config.CastTaxDb.Reset();
            AdvanceEpoch("manual relearn", resetRttModel: true);
            MarkLearnedDataDirty();
        }

        public string ExportReplay(string format)
        {
            var directory = Path.Combine(DalamudApi.PluginInterface.ConfigDirectory.FullName, "exports");
            return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                ? replayLog.ExportCsv(directory)
                : replayLog.ExportJson(directory);
        }

        public bool TryGetRecentActionForSequence(ushort sequence, TimeSpan maxAge, out uint actionId)
            => TryGetRecentAcceptedActionForSequence(sequence, maxAge, out actionId);

        public bool TryGetRecentAcceptedActionForSequence(ushort sequence, TimeSpan maxAge, out uint actionId)
        {
            if (replayLog.TryFindRecentBySequence(sequence, maxAge,
                    trace => trace.Ownership == DecisionOwnership.AcceptedServerReconciliation
                             && trace.Epoch == modelEpoch.Current, out var trace)
                && trace.ActionId != 0
                && IsActionPredictionSource(trace.Source))
            {
                actionId = trace.ActionId;
                return true;
            }

            actionId = 0;
            return false;
        }

        public bool TryGetRecentIssuedActionForSequence(ushort sequence, TimeSpan maxAge, out RecentIssuedAction action)
            => recentIssuedActions.TryFindBySequence(sequence, maxAge, out action);

        public bool TryGetRecentIssuedActionNearNow(TimeSpan maxAge, out RecentIssuedAction action)
            => recentIssuedActions.TryFindNearNow(maxAge, out action);

        public bool TryGetRecentPredictionState(ushort sequence, TimeSpan maxAge, out RecentPredictionState state)
        {
            var now = Environment.TickCount64;
            if (predictions.TryGetPending(sequence, modelEpoch.Current, now, out var pending))
            {
                state = new RecentPredictionState
                {
                    Sequence = pending.Sequence,
                    ActionId = pending.ActionId,
                    PredictedLock = pending.PredictedLock,
                    CreatedTick = pending.CreatedTick,
                    ModelEpoch = pending.ModelEpoch,
                    IsPendingForSequence = true,
                    State = "pending",
                    Ownership = DecisionOwnership.PreAppliedPendingLock,
                };
                return state.AgeMilliseconds(now) <= maxAge.TotalMilliseconds;
            }

            if (lastPredictedTick != 0
                && lastPredictedSequence == sequence
                && lastPredictedEpoch == modelEpoch.Current
                && now - lastPredictedTick <= maxAge.TotalMilliseconds)
            {
                state = new RecentPredictionState
                {
                    Sequence = lastPredictedSequence,
                    ActionId = lastPredictedActionId,
                    PredictedLock = lastPredictedLock,
                    CreatedTick = lastPredictedTick,
                    ModelEpoch = lastPredictedEpoch,
                    IsPendingForSequence = false,
                    State = "recent",
                    Ownership = DecisionOwnership.PreAppliedPendingLock,
                };
                return true;
            }

            state = null;
            return false;
        }

        public bool TryGetRecentPluginOwnedState(ushort sequence, TimeSpan maxAge, out RecentPredictionState state)
        {
            if (TryGetRecentPredictionState(sequence, maxAge, out state))
                return true;

            if (replayLog.TryFindRecentBySequence(sequence, maxAge, IsPluginOwnedTrace, out var trace))
            {
                var now = DateTimeOffset.UtcNow;
                state = new RecentPredictionState
                {
                    Sequence = trace.Sequence,
                    ActionId = trace.ActionId,
                    PredictedLock = trace.PredictedLock > 0 ? trace.PredictedLock : trace.FinalAppliedLock,
                    CreatedTick = Environment.TickCount64 - Math.Max(0, (long)(now - trace.Timestamp).TotalMilliseconds),
                    ModelEpoch = trace.Epoch,
                    IsPendingForSequence = false,
                    State = TraceOwnershipState(trace.Ownership),
                    Ownership = trace.Ownership,
                };
                return true;
            }

            state = null;
            return false;
        }

        public bool HasRecentDecisionForSequence(ushort sequence, TimeSpan maxAge)
            => replayLog.TryFindRecentBySequence(sequence, maxAge,
                trace => trace.Ownership != DecisionOwnership.PreAppliedPendingLock, out _);

        private static bool IsPluginOwnedTrace(DecisionTrace trace)
            => trace.Ownership is DecisionOwnership.PreAppliedPendingLock
                or DecisionOwnership.AcceptedServerReconciliation
                or DecisionOwnership.RejectedNoCompensation
                or DecisionOwnership.CastPrediction
                or DecisionOwnership.LegacyReceive;

        private static string TraceOwnershipState(DecisionOwnership ownership)
            => ownership switch
            {
                DecisionOwnership.PreAppliedPendingLock => "pending",
                DecisionOwnership.AcceptedServerReconciliation => "reconciled",
                DecisionOwnership.RejectedNoCompensation => "rejected",
                DecisionOwnership.CastPrediction => "cast",
                DecisionOwnership.LegacyReceive => "legacy",
                _ => "unknown",
            };

        private static bool IsActionPredictionSource(string source)
            => !string.IsNullOrWhiteSpace(source)
               && !string.Equals(source, "receive", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(source, "cast", StringComparison.OrdinalIgnoreCase);

        private unsafe void RecordIssuedLocalAction(ActionType actionType, uint actionID, string source)
        {
            var resolvedActionId = ActionManager.GetSpellIdForAction(actionType, actionID);
            if (resolvedActionId == 0)
                return;

            recentIssuedActions.Record(new RecentIssuedAction
            {
                Sequence = Game.actionManager != null ? Game.actionManager->currentSequence : (ushort)0,
                ActionId = resolvedActionId,
                OriginalActionId = actionID,
                Source = source,
                ActionType = actionType,
                CreatedTick = Environment.TickCount64,
                ModelEpoch = modelEpoch.Current,
            });
        }

        private void RefreshRuntimeSettings()
        {
            var settings = ProfileSettings.From(Config.Profile, dualRttEstimator.Classification);
            var safeReason = GetSafeModeReason();
            safeModeActive = safeReason.Length > 0;
            lastSafeModeReason = safeModeActive ? safeReason : "none";

            profileSettings = safeModeActive && settings.EffectiveProfile != TsunippyProfile.Safe
                ? ProfileSettings.From(TsunippyProfile.Safe, dualRttEstimator.Classification)
                : settings;

            rttEstimator.Alpha = Config.JKAlpha;
            rttEstimator.Beta = Config.JKBeta;
            rttEstimator.K = Config.JKK * profileSettings.VarianceMultiplier;
            dynamicFloor.ScalingFactor = Config.DynamicFloorScaling;
            dynamicFloor.Mode = profileSettings.FloorMode;
            dynamicFloor.ConnectionState = dualRttEstimator.Classification;
        }

        private string GetSafeModeReason()
        {
            if (!rttEstimator.IsWarm)
                return "RTT estimator warming up";

            if (modelEpoch.TimeSinceReset.TotalSeconds < 2)
                return "epoch recently reset";

            if (dualRttEstimator.Classification is ConnectionState.Bursty or ConnectionState.PathShifted)
                return $"connection {dualRttEstimator.Classification}";

            if (predictionMismatchStreak >= 2)
                return "prediction mismatch streak";

            if (observedOutlierStreak >= 2)
                return "observed lock outlier streak";

            return string.Empty;
        }

        private void AdvanceEpoch(string reason, bool resetRttModel)
        {
            modelEpoch.Reset(reason);
            modelEpoch.AddStaleInvalidations(predictions.RemoveEpochsBefore(modelEpoch.Current));
            predictionMismatchStreak = 0;
            observedOutlierStreak = 0;
            pathShiftStreak = 0;
            lastPredictionReason = $"epoch reset: {reason}";

            if (resetRttModel)
            {
                rttEstimator.Reset();
                dualRttEstimator.Reset();
                dynamicFloor.Reset();
                packetTracker.Reset();
            }

            RefreshRuntimeSettings();
        }

        private void RegisterPredictionMismatch(string reason)
        {
            if (string.Equals(reason, "no pending prediction", StringComparison.OrdinalIgnoreCase))
                return;

            predictionMismatchStreak++;
            if (predictionMismatchStreak >= 3)
                AdvanceEpoch($"repeated prediction mismatch: {reason}", resetRttModel: true);
        }

        private void MaybeResetForRepeatedOutliers(string reason)
        {
            if (observedOutlierStreak >= 3)
                AdvanceEpoch($"repeated observed lock outliers: {reason}", resetRttModel: true);
        }

        private bool MaybeResetForPathShift()
        {
            if (dualRttEstimator.Classification == ConnectionState.PathShifted)
                pathShiftStreak++;
            else
                pathShiftStreak = Math.Max(0, pathShiftStreak - 1);

            if (pathShiftStreak < 3 || modelEpoch.TimeSinceReset.TotalSeconds < 5)
                return false;

            AdvanceEpoch("sustained RTT path shift", resetRttModel: true);
            return true;
        }

        private static bool IsCastPredictionOwnerActive()
            => global::Tsunippy.Modules.Modules.GetInstance<CastLockPrediction>()?.IsEnabled == true;

        private void RecordDecision(ushort sequence, uint actionID, GameContext context, float baseLock,
            float observedLock, float predictedLock, float finalAppliedLock, float existingLockBeforeWrite,
            float rttSample, float packetWeight, string decisionReason, string rejectionReason, bool hasFormula = false,
            string source = "", DecisionOwnership ownership = DecisionOwnership.Unknown)
        {
            var lockConfidence = Config.LockDb.GetEntry(actionID, context)?.Confidence ?? 0f;
            var castConfidence = Config.CastTaxDb.GetEntry(actionID, context)?.Confidence ?? 0f;
            var correction = baseLock > 0 ? observedLock - baseLock : 0f;
            replayLog.Add(new DecisionTrace
            {
                Epoch = modelEpoch.Current,
                Sequence = sequence,
                ActionId = actionID,
                Source = source,
                Ownership = ownership,
                IsPvP = context == GameContext.PvP,
                BaseLock = baseLock,
                ObservedLock = observedLock,
                PredictedLock = predictedLock,
                FinalAppliedLock = finalAppliedLock,
                ExistingLockBeforeWrite = existingLockBeforeWrite,
                Correction = correction,
                RttSample = rttSample,
                SmoothedRtt = rttEstimator.SmoothedRTT,
                RttVariance = rttEstimator.RTTVariance,
                DynamicFloor = dynamicFloor.Floor,
                VarianceBuffer = rttEstimator.VarianceBuffer,
                PacketWeight = packetWeight,
                HasFormula = hasFormula,
                Profile = $"{Config.Profile}/{profileSettings.EffectiveProfile}",
                ConnectionState = dualRttEstimator.Classification.ToString(),
                EstimatorMaturity = rttEstimator.EstimatorMaturity,
                LockDbConfidence = lockConfidence,
                CastTaxConfidence = castConfidence,
                DecisionReason = decisionReason,
                RejectionReason = rejectionReason,
            });
        }

        public override unsafe void Enable()
        {
            ResetRuntimeState();
            AdvanceEpoch("module enabled", resetRttModel: true);
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
            AdvanceEpoch("module disabled", resetRttModel: true);
            ResetRuntimeState();
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Animation Lock Reduction", ref Config.EnableAnimLockComp))
                Config.Save();
            PluginUI.SetItemTooltip("Modifies the way the game handles animation lock," +
                "\nsimulating low ping using adaptive RTT estimation." +
                "\n\nImprovements over NoClippy:" +
                "\n- Jacobson/Karels RTT estimator (adaptive jitter handling)" +
                "\n- Dynamic RTT floor (adapts to your datacenter)" +
                "\n- Graduated packet weight (nuanced spike handling)" +
                "\n- Context-aware lock database (PvE/PvP separated)");

            if (Config.EnableAnimLockComp)
            {
                ImGui.Columns(2, "AnimlockColumns", false);

                if (ImGui.Checkbox("Enable Logging", ref Config.EnableLogging))
                    Config.Save(checkModules: false);

                ImGui.NextColumn();

                var dryRun = IsDryRunEnabled;
                if (ImGui.Checkbox("Dry Run", ref dryRun))
                {
                    Config.EnableDryRun = dryRun;
                    enableAnticheat = false;
                    Config.Save(checkModules: false);
                }
                PluginUI.SetItemTooltip("The plugin will still log and perform calculations,\nbut no in-game values will be overwritten.");

                ImGui.Columns(1);

                if (ImGui.Checkbox("Learn Animation Locks", ref Config.LearnAnimationLocks))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Learns per-action lock values from live server responses.\nDisable this if you want to freeze the current learned database.");

                var profile = Config.Profile;
                if (ImGui.BeginCombo("Profile", profile.ToString()))
                {
                    foreach (TsunippyProfile option in Enum.GetValues(typeof(TsunippyProfile)))
                    {
                        var selected = option == profile;
                        if (ImGui.Selectable(option.ToString(), selected))
                        {
                            Config.Profile = option;
                            RefreshRuntimeSettings();
                            Config.Save(checkModules: false);
                        }

                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }

                    ImGui.EndCombo();
                }
                PluginUI.SetItemTooltip("Safe is conservative, Balanced is the sane default, Aggressive is tighter,\nand Auto switches locally based on jitter and path-shift classification.");

                if (ImGui.TreeNode("Advanced RTT Settings"))
                {
                    ImGui.TextUnformatted("Jacobson/Karels Parameters");
                    ImGui.Indent();

                    var alpha = Config.JKAlpha;
                    if (ImGui.SliderFloat("Alpha (SRTT smoothing)", ref alpha, 0.01f, 0.5f, "%.3f"))
                    {
                        Config.JKAlpha = alpha;
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Controls how quickly the smoothed RTT adapts to new samples.\nLower = more stable, higher = more responsive.\nDefault: 0.125 (RFC 6298)");

                    var beta = Config.JKBeta;
                    if (ImGui.SliderFloat("Beta (Variance smoothing)", ref beta, 0.01f, 0.5f, "%.3f"))
                    {
                        Config.JKBeta = beta;
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Controls how quickly the RTT variance adapts.\nLower = more stable variance, higher = more reactive to jitter.\nDefault: 0.25 (RFC 6298)");

                    var k = Config.JKK;
                    if (ImGui.SliderFloat("K (Variance multiplier)", ref k, 0.5f, 4.0f, "%.2f"))
                    {
                        Config.JKK = k;
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Multiplier on RTT variance for the safety buffer.\nHigher = more conservative (less clipping risk).\nLower = more aggressive (tighter locks).\nDefault: 2.0");

                    ImGui.Unindent();
                    ImGui.Spacing();
                    ImGui.TextUnformatted("Dynamic Floor Parameters");
                    ImGui.Indent();

                    var scaling = Config.DynamicFloorScaling;
                    if (ImGui.SliderFloat("Floor Scaling", ref scaling, 0.5f, 1.0f, "%.2f"))
                    {
                        Config.DynamicFloorScaling = scaling;
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Floor = MinRTT * ScalingFactor.\nLower = more aggressive (floor drops further below min RTT).\nHigher = more conservative.\nDefault: 0.85");

                    ImGui.Unindent();

                    if (ImGui.Button("Reset to Defaults"))
                    {
                        Config.JKAlpha = 0.125f;
                        Config.JKBeta = 0.25f;
                        Config.JKK = 2.0f;
                        Config.DynamicFloorScaling = 0.85f;
                        ResetRttModel();
                        Config.Save(checkModules: false);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Reset Learned Locks"))
                    {
                        Config.LockDb.Reset();
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Clears the learned per-action lock database.");

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

        private void MarkRuntimeStatsDirty()
        {
            saveRuntimeStats = true;
        }

        private bool ShouldFlushConfigSave(bool inCombat)
        {
            if (DalamudApi.Condition[ConditionFlag.BetweenAreas])
                return true;

            if (inCombat)
                return false;

            if (saveLearnedData)
            {
                var learnedDelay = pendingLearnedEntries >= SaveFlushBatchSize
                    ? LearnedBatchSaveIdleDelay
                    : LearnedSaveIdleDelay;

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

        public void NotifyLearnedDataChanged()
        {
            MarkLearnedDataDirty();
        }

        private void ResetRuntimeState()
        {
            rttEstimator.Reset();
            dualRttEstimator.Reset();
            dynamicFloor.Reset();
            packetTracker.Reset();
            predictions.Clear();
            recentIssuedActions.Clear();
            isCasting = false;
            enableAnticheat = false;
            saveLearnedData = false;
            saveRuntimeStats = false;
            wasBetweenAreas = false;
            wasPlayerPresent = false;
            safeModeActive = true;
            outOfCombatIdleTimer = 0f;
            pendingLearnedEntries = 0;
            predictionMismatchStreak = 0;
            observedOutlierStreak = 0;
            pathShiftStreak = 0;
            lastActionTick = 0;
            lastPredictedTick = 0;
            lastPredictedSequence = 0;
            lastPredictedActionId = 0;
            lastPredictedLock = 0f;
            lastPredictedEpoch = 0;
            lastTerritoryType = uint.MaxValue;
            profileSettings = ProfileSettings.From(Config.Profile, ConnectionState.WarmingUp);
            lastPredictionReason = "reset";
            lastSafeModeReason = "runtime reset";
            LastRTT = 0f;
            LastCorrection = 0f;
            LastVarianceBuffer = 0f;
            LastAdjustedLock = 0f;
            LastActionID = 0u;
        }

        private static bool NearlyEqual(float left, float right)
            => Math.Abs(left - right) <= LockEqualityEpsilon;
    }
}
