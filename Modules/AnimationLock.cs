using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Bindings.ImGui;
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
        // ==================== ARCHITECTURE NOTES ====================
        // This module is the core of Tsunippy. It improves on NoClippy's AnimationLock with:
        //
        // 1. Jacobson/Karels RTT estimator (RFC 6298) instead of simple EWMA
        //    - Separately tracks smoothed RTT and RTT variance
        //    - PredictedBuffer = SRTT + K*RTTVAR provides dynamic network buffering
        //    - Tight on stable connections, expands during jitter
        //
        // 2. Dynamic RTT floor instead of hardcoded 40ms
        //    - Tracks minimum observed RTT over a sliding window
        //    - Floor = MinRTT * 0.85 (adapts per-datacenter, per-time-of-day)
        //    - Falls back to 40ms until sufficient samples collected
        //
        // 3. Graduated packet weight (1.0/0.5/0.25/0.1) instead of binary (1.0/0.1)
        //    - More nuanced spike handling for multi-packet bursts
        //
        // 4. Context-aware lock database keyed by (actionID, PvE/PvP)
        //    - Confidence tracking per entry
        //    - PvP and PvE locks stored separately
        //
        // The overall flow remains the same as NoClippy:
        //   Action used -> Pre-apply predicted lock -> Server responds -> Correct lock
        // But every component in the correction pipeline is upgraded.
        // ============================================================

        public override bool IsEnabled
        {
            get => Config.EnableAnimLockComp;
            set => Config.EnableAnimLockComp = value;
        }

        public override int DrawOrder => 1;

        // RTT infrastructure (Tsunippy improvements)
        private readonly JacobsonKarels rttEstimator = new();
        private readonly DynamicFloor dynamicFloor;
        private readonly PacketTracker packetTracker = new();

        // State tracking (same pattern as NoClippy)
        private bool isCasting = false;
        private bool enableAnticheat = false;
        private bool saveConfig = false;
        private readonly Dictionary<ushort, float> appliedAnimationLocks = new();

        // Diagnostics state (exposed for Diagnostics module)
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

        public bool IsDryRunEnabled => enableAnticheat || Config.EnableDryRun;

        public AnimationLock()
        {
            dynamicFloor = new DynamicFloor(Config.DynamicFloorWindow);
        }

        // ==================== Lock Prediction ====================

        /// <summary>
        /// Get the predicted animation lock for an action.
        /// Uses the context-aware database + dynamic floor instead of NoClippy's
        /// hardcoded dictionary + 40ms constant.
        /// </summary>
        private float GetPredictedLock(uint actionID)
        {
            var context = GetCurrentContext();
            var baseLock = Config.LockDb.GetLock(actionID, context, Game.DefaultClientAnimationLock);
            return baseLock + dynamicFloor.Floor;
        }

        /// <summary>Detect current game context (PvE vs PvP).</summary>
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

        // ==================== Hook Handlers ====================

        /// <summary>
        /// Called when an action is used. Pre-applies the predicted animation lock
        /// immediately instead of waiting for the server response.
        /// </summary>
        private unsafe void ApplyPredictedLock(ActionType actionType, uint actionID)
        {
            if (Game.actionManager->animationLock != Game.DefaultClientAnimationLock) return;

            // Resolve the canonical spell ID (handles job-specific action mapping)
            var id = ActionManager.GetSpellIdForAction(actionType, actionID);
            var predictedLock = GetPredictedLock(id);

            if (!IsDryRunEnabled)
            {
                Game.actionManager->animationLock = predictedLock;
                appliedAnimationLocks[Game.actionManager->currentSequence] = predictedLock;
            }

            DalamudApi.LogDebug($"Applying {F2MS(predictedLock)} ms animation lock for {actionType} {actionID} ({id}), floor={F2MS(dynamicFloor.Floor)} ms");
        }

        private unsafe void UseAction(ActionManager* actionManager, ActionType actionType, uint actionID,
            ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId,
            bool* outOptAreaTargeted, bool ret)
        {
            if (!ret) return;
            ApplyPredictedLock(actionType, actionID);
        }

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID,
            ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            if (ret == 0) return;
            ApplyPredictedLock((ActionType)actionType, actionID);
        }

        private void CastBegin(ulong objectID, nint packetData) => isCasting = true;
        private void CastInterrupt(nint actionManager) => isCasting = false;

        /// <summary>
        /// Called when the server sends an action effect response.
        /// This is where the core correction logic lives.
        ///
        /// The key insight: by the time the server responds, the predicted lock has been
        /// counting down for exactly RTT seconds. So:
        ///   actualRTT = appliedLock - oldLock
        ///   correction = serverLock - predictedBaseLock
        ///   varianceBuffer = K * RTTVAR (from Jacobson/Karels)
        ///   adjustedLock = oldLock + correction + varianceBuffer
        /// </summary>
        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr,
            Vector3* targetPos, ActionEffectHandler.Header* header,
            ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds,
            float oldLock, float newLock)
        {
            try
            {
                // Skip if lock didn't change or not local player
                if (oldLock == newLock || (nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address) return;

                // ---- Cast lock handling ----
                // Cast actions (caster tax, teleport, LB) are handled differently.
                // The CastLockPrediction module handles pre-application for these.
                // Here we just do the basic NoClippy-style handling as a fallback.
                if (isCasting)
                {
                    isCasting = false;

                    // The old lock should always be 0 at cast completion, but high ping
                    // can cause the packet to arrive late. Add any remaining lock.
                    newLock += oldLock;
                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");
                    return;
                }

                // ---- Mismatch detection ----
                if (newLock != header->AnimationLock)
                {
                    PrintError("Mismatched animation lock offset! This can be caused by another plugin affecting the animation lock.");
                    return;
                }

                // ---- XivAlexander conflict detection ----
                // Alexander adjusts locks to fractional ms values the game never produces naturally.
                var isUsingAlexander = newLock % 0.01 is >= 0.0005f and <= 0.0095f;
                if (!enableAnticheat && isUsingAlexander)
                {
                    enableAnticheat = true;
                    PrintError($"Unexpected lock of {F2MS(newLock)} ms, temporary dry run has been enabled. Please disable any other programs or plugins that may be affecting the animation lock.");
                }

                // ---- Retrieve prediction state ----
                var sequence = header->SourceSequence;
                var actionID = header->SpellId;
                var appliedLock = appliedAnimationLocks.GetValueOrDefault(sequence, 0.5f);
                LastActionID = actionID;

                appliedAnimationLocks.Remove(sequence);

                // The lock we predicted from the database (without the floor buffer)
                var currentFloor = dynamicFloor.Floor;
                var lastRecordedLock = IsDryRunEnabled ? newLock : appliedLock - currentFloor;

                // ---- Update lock database ----
                var context = GetCurrentContext();
                if (!enableAnticheat)
                    Config.LockDb.RecordLock(actionID, context, newLock);

                // ---- Compute RTT ----
                // appliedLock was set at action use time, oldLock is what remains.
                // The difference is exactly how long the round trip took.
                var correction = newLock - lastRecordedLock;
                var rtt = appliedLock - oldLock;
                LastRTT = rtt;

                // ---- Feed RTT to infrastructure ----
                dynamicFloor.AddSample(rtt);

                // Check if RTT is already below the dynamic floor (no adjustment needed)
                if (rtt <= currentFloor)
                {
                    if (Config.EnableLogging)
                        PrintLog($"RTT ({F2MS(rtt)} ms) was lower than floor ({F2MS(currentFloor)} ms), no adjustments made");
                    LastCorrection = 0;
                    LastVarianceBuffer = 0;
                    LastAdjustedLock = newLock;
                    return;
                }

                // ---- Jacobson/Karels RTT update ----
                var weight = packetTracker.GetRTTWeight();
                rttEstimator.AddSample(rtt, weight);

                // Sync estimator parameters from config (allows live tuning)
                rttEstimator.Alpha = Config.JKAlpha;
                rttEstimator.Beta = Config.JKBeta;
                rttEstimator.K = Config.JKK;
                dynamicFloor.ScalingFactor = Config.DynamicFloorScaling;

                // ---- Compute variance buffer ----
                // This replaces NoClippy's `simulatedRTT * (max(rtt/average, 1) - 1)` formula.
                // Instead we use the Jacobson/Karels variance component: K * RTTVAR.
                // This is more principled: on stable connections RTTVAR is small (tight locks),
                // on jittery connections RTTVAR is large (safe buffer).
                var varianceBuffer = rttEstimator.VarianceBuffer;
                LastVarianceBuffer = varianceBuffer;
                LastCorrection = correction;

                // ---- Final lock calculation ----
                var adjustedAnimationLock = Math.Max(oldLock + correction + varianceBuffer, 0);
                LastAdjustedLock = (float)adjustedAnimationLock;

                // ---- Write to game memory ----
                if (!IsDryRunEnabled && float.IsFinite((float)adjustedAnimationLock) && adjustedAnimationLock < 10)
                {
                    Game.actionManager->animationLock = (float)adjustedAnimationLock;

                    Config.TotalAnimationLockReduction += newLock - adjustedAnimationLock;
                    Config.TotalActionsReduced++;

                    if (!saveConfig && DalamudApi.Condition[ConditionFlag.InCombat])
                        saveConfig = true;
                }

                // ---- Logging ----
                if (!Config.EnableLogging) return;

                var sb = new StringBuilder(IsDryRunEnabled ? "[DRY] " : string.Empty)
                    .Append($"Action: {actionID} ")
                    .Append(lastRecordedLock != newLock ? $"({F2MS((float)lastRecordedLock)} > {F2MS(newLock)} ms)" : $"({F2MS(newLock)} ms)")
                    .Append($" || RTT: {F2MS(rtt)} ms (SRTT: {F2MS(rttEstimator.SmoothedRTT)}, VAR: {F2MS(rttEstimator.RTTVariance)})");

                if (enableAnticheat)
                    sb.Append($" [Alexander detected]");

                if (!IsDryRunEnabled)
                    sb.Append($" || Lock: {F2MS(oldLock)} > {F2MS((float)adjustedAnimationLock)} ({F2MS((float)(correction + varianceBuffer)):+0;-#}) ms");

                sb.Append($" || Floor: {F2MS(dynamicFloor.Floor)} ms | Wt: {weight:F2} | Pkts: {packetTracker.TotalPacketsSent}");

                PrintLog(sb.ToString());
            }
            catch (Exception e)
            {
                DalamudApi.LogError($"AnimationLock.ReceiveActionEffect failed for action {LastActionID}: {e}");
                PrintError("Error in AnimationLock Module. Check Dalamud logs for details.");
            }
        }

        /// <summary>
        /// Called for every outgoing network packet.
        /// Feeds the packet tracker for RTT weight calculation.
        /// </summary>
        private void NetworkMessage(nint packet)
        {
            packetTracker.RecordPacket(packet);
        }

        /// <summary>
        /// Called every frame. Advances the packet tracker window and handles deferred saves.
        /// </summary>
        private void Update()
        {
            // Deferred config save — only writes to disk during zone transitions
            if (saveConfig && DalamudApi.Condition[ConditionFlag.BetweenAreas])
            {
                Config.Save(checkModules: false);
                saveConfig = false;
            }

            // Advance the packet counting window
            packetTracker.Update((float)DalamudApi.Framework.UpdateDelta.TotalSeconds);
        }

        // ==================== Module Lifecycle ====================

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

        // ==================== Config UI ====================

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

                // Advanced RTT settings (collapsible)
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
                    if (ImGui.SliderFloat("K (Variance multiplier)", ref k, 0.5f, 4.0f, "%.1f"))
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

                    ImGui.TreePop();
                }
            }

            ImGui.TextUnformatted($"Reduced a total time of {TimeSpan.FromSeconds(Config.TotalAnimationLockReduction):d\\:hh\\:mm\\:ss} from {Config.TotalActionsReduced} actions");
        }
    }
}
