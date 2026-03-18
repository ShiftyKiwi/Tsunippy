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
        private readonly DynamicFloor dynamicFloor;
        private readonly PacketTracker packetTracker = new();

        private bool isCasting;
        private bool enableAnticheat;
        private bool saveLearnedData;
        private bool saveRuntimeStats;
        private float outOfCombatIdleTimer;
        private int pendingLearnedEntries;
        private readonly Dictionary<ushort, float> appliedAnimationLocks = new();

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

        public AnimationLock()
        {
            dynamicFloor = new DynamicFloor(Config.DynamicFloorWindow);
            ResetRuntimeState();
        }

        private float GetPredictedLock(uint actionID)
        {
            var context = GetCurrentContext();
            var baseLock = Config.LockDb.GetLock(actionID, context, Game.DefaultClientAnimationLock);
            return baseLock + dynamicFloor.Floor;
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
            if (!NearlyEqual(Game.actionManager->animationLock, Game.DefaultClientAnimationLock))
                return;

            var id = ActionManager.GetSpellIdForAction(actionType, actionID);
            var predictedLock = GetPredictedLock(id);

            appliedAnimationLocks[Game.actionManager->currentSequence] = predictedLock;

            if (!IsDryRunEnabled)
            {
                Game.actionManager->animationLock = predictedLock;
            }

            packetTracker.MarkActionIssued();
            DalamudApi.LogDebug($"Applying {F2MS(predictedLock)} ms animation lock for {actionType} {actionID} ({id}), floor={F2MS(dynamicFloor.Floor)} ms");
        }

        private unsafe void UseAction(ActionManager* actionManager, ActionType actionType, uint actionID,
            ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId,
            bool* outOptAreaTargeted, bool ret)
        {
            if (!ret)
                return;

            ApplyPredictedLock(actionType, actionID);
        }

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID,
            ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            if (ret == 0)
                return;

            ApplyPredictedLock((ActionType)actionType, actionID);
        }

        private void CastBegin(ulong objectID, nint packetData)
            => isCasting = true;

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

                if (isCasting)
                {
                    isCasting = false;
                    newLock += oldLock;

                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");

                    return;
                }

                if (!NearlyEqual(newLock, header->AnimationLock))
                {
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
                var hadPrediction = appliedAnimationLocks.TryGetValue(sequence, out var appliedLock);
                LastActionID = actionID;

                appliedAnimationLocks.Remove(sequence);

                var context = GetCurrentContext();
                if (!enableAnticheat && Config.LearnAnimationLocks && Config.LockDb.RecordLock(actionID, context, newLock))
                    MarkLearnedDataDirty();

                if (!hadPrediction)
                {
                    LastRTT = 0;
                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Action: {actionID} ({F2MS(newLock)} ms) || No correlated prediction, skipped RTT correction");

                    return;
                }

                var currentFloor = dynamicFloor.Floor;
                var lastRecordedLock = appliedLock - currentFloor;

                var correction = newLock - lastRecordedLock;
                var rtt = appliedLock - oldLock;
                LastRTT = rtt;

                dynamicFloor.AddSample(rtt);
                if (rtt <= currentFloor)
                {
                    if (Config.EnableLogging)
                        PrintLog($"RTT ({F2MS(rtt)} ms) was lower than floor ({F2MS(currentFloor)} ms), no adjustments made");

                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    return;
                }

                var weight = packetTracker.GetRTTWeight();
                rttEstimator.AddSample(rtt, weight);

                rttEstimator.Alpha = Config.JKAlpha;
                rttEstimator.Beta = Config.JKBeta;
                rttEstimator.K = Config.JKK;
                dynamicFloor.ScalingFactor = Config.DynamicFloorScaling;

                var varianceBuffer = rttEstimator.VarianceBuffer;
                LastVarianceBuffer = varianceBuffer;
                LastCorrection = correction;

                var adjustedAnimationLock = Math.Max(oldLock + correction + varianceBuffer, 0);
                LastAdjustedLock = (float)adjustedAnimationLock;

                if (!IsDryRunEnabled && float.IsFinite((float)adjustedAnimationLock) && adjustedAnimationLock < 10)
                {
                    Game.actionManager->animationLock = (float)adjustedAnimationLock;

                    Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                    Config.TotalActionsReduced++;
                    MarkRuntimeStatsDirty();
                }

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
            outOfCombatIdleTimer = inCombat ? 0f : outOfCombatIdleTimer + deltaSeconds;

            if ((saveLearnedData || saveRuntimeStats) && ShouldFlushConfigSave(inCombat))
            {
                Config.Save(checkModules: false);
                ResetDirtySaveState();
            }

            packetTracker.Update(deltaSeconds);
        }

        public override unsafe void Enable()
        {
            ResetRuntimeState();
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
                        rttEstimator.Reset();
                        dynamicFloor.Reset();
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
            dynamicFloor.Reset();
            packetTracker.Reset();
            isCasting = false;
            enableAnticheat = false;
            saveLearnedData = false;
            saveRuntimeStats = false;
            outOfCombatIdleTimer = 0f;
            pendingLearnedEntries = 0;
            appliedAnimationLocks.Clear();
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
