using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Tsunippy.Database;
using Tsunippy.Runtime;
using Tsunippy.Runtime.Controller;
using Tsunippy.Runtime.Replay;
using Tsunippy.Runtime.Trace;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableAnimLockComp = true;
        public bool EnableLogging = false;
        public bool EnableDryRun = false;
        public bool LearnAnimationLocks = true;
        public TimingControllerStrategy ControllerStrategy = TimingControllerStrategy.ConfidenceAdaptive;
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
        private const float LearnedSaveIdleDelay = 15f;
        private const float LearnedBatchSaveIdleDelay = 5f;
        private const float RuntimeStatsSaveIdleDelay = 120f;
        private const int SaveFlushBatchSize = 8;

        private readonly Stopwatch runtimeClock = Stopwatch.StartNew();
        private readonly TimingControllerProfile liveProfile = TimingControllerProfile.CreateFrontierDefault();

        private TimingControllerEngine controller;
        private TimingTraceCaptureSession captureSession;
        private double captureStartTimelineSeconds;
        private bool captureTruncationNotified;
        private bool saveLearnedData;
        private bool saveRuntimeStats;
        private float outOfCombatIdleTimer;
        private int pendingLearnedEntries;
        private int hotPathFailureCount;
        private string lastHotPathFailure = string.Empty;

        public override bool DisableOnRuntimeFailure => false;
        public override bool IsEnabled { get => Config.EnableAnimLockComp; set => Config.EnableAnimLockComp = value; }
        public override int DrawOrder => 1;

        public float LastRTT => controller.LastRTT;
        public float LastCorrection => controller.LastCorrection;
        public float LastVarianceBuffer => controller.LastVarianceBuffer;
        public float LastAdjustedLock => controller.LastAdjustedLock;
        public uint LastActionID => controller.LastActionId;
        public float CurrentFloor => controller.CurrentFloor;
        public float CurrentSRTT => controller.CurrentSRTT;
        public float CurrentRTTVAR => controller.CurrentRTTVAR;
        public int FloorSampleCount => controller.FloorSampleCount;
        public int RTTSampleCount => controller.RTTSampleCount;
        public int PacketsSent => controller.PacketsSent;
        public int ActionPacketsSent => controller.ActionPacketsSent;
        public int PendingLearnedEntries => pendingLearnedEntries;
        public int PendingPredictionCount => controller.PendingPredictionCount;
        public int HotPathFailureCount => hotPathFailureCount;
        public CastLifecycleStage CastStage => controller.CastStage;
        public TimingRuntimeMode CurrentMode => controller.CurrentMode;
        public TimingQuality CurrentQuality => controller.CurrentQuality;
        public TimingDecisionSource LastDecisionSource => controller.LastDecisionSource;
        public TimingDecisionReason LastDecisionReason => controller.LastDecisionReason;
        public string LastDecisionNote => controller.LastDecisionNote;
        public float LastPredictionConfidence => controller.LastPredictionConfidence;
        public bool ConflictDetected => controller.ConflictDetected;
        public bool FailureQuarantined => controller.FailureQuarantined;
        public string LastHotPathFailure => lastHotPathFailure;
        public string LastSuppressionReason => controller.LastSuppressionReason;
        public string LastRuntimeResetReason => controller.LastRuntimeResetReason;
        public bool IsDryRunEnabled => CurrentMode != TimingRuntimeMode.Active;
        public bool IsCaptureActive => captureSession != null;
        public bool CaptureTruncated => captureSession?.IsTruncated ?? false;
        public int CaptureEventCount => captureSession?.EventCount ?? 0;
        public string CaptureLabel => captureSession?.Trace.Metadata.Label ?? string.Empty;
        public string LastCapturePath { get; private set; } = string.Empty;

        public AnimationLock()
        {
            RefreshLiveProfile();
            controller = new TimingControllerEngine(liveProfile, Config.LockDb ??= new LockDatabase(), Config.CastTaxDb ??= new Database.CastTaxDatabase(), 16);
        }

        public TimingDecisionTrace[] GetRecentDecisions() => controller.GetRecentDecisions();

        public override void ResetRuntime(RuntimeResetReason reason)
        {
            RefreshLiveProfile();
            controller.ApplyProfile(liveProfile);

            var clearConflict = reason is RuntimeResetReason.Enable or RuntimeResetReason.Manual or RuntimeResetReason.ConflictRecovery or RuntimeResetReason.ModuleStateChange;
            var clearFailure = reason is RuntimeResetReason.Enable or RuntimeResetReason.Manual or RuntimeResetReason.ModuleStateChange;
            var result = controller.ResetRuntime(TimelineNow, reason, reason.ToString(), clearConflict, clearFailure);
            ApplyResult(result);

            if (captureSession != null)
                RecordCapture(new TimingRuntimeResetTraceEvent(CaptureTimelineNow, reason, new TimingResetSemantics(clearConflict, clearFailure), $"Runtime reset: {reason}"));
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
            => GuardHotPath(nameof(UseAction), () =>
            {
                var manager = Game.actionManager;
                if (!ret || manager == null)
                    return;

                RefreshLiveProfile();
                controller.ApplyProfile(liveProfile);

                var resolvedActionId = ActionManager.GetSpellIdForAction(actionType, actionID);
                var context = GetCurrentContext();
                RecordCapture(new TimingActionRequestTraceEvent(CaptureTimelineNow, resolvedActionId, manager->currentSequence, context, TimingActionKind.Instant, manager->animationLock, true));
                ApplyResult(controller.ProcessActionRequest(TimelineNow, resolvedActionId, manager->currentSequence, context, TimingActionKind.Instant, manager->animationLock, true));
            });

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret)
            => GuardHotPath(nameof(UseActionLocation), () =>
            {
                var manager = Game.actionManager;
                if (ret == 0 || manager == null)
                    return;

                RefreshLiveProfile();
                controller.ApplyProfile(liveProfile);

                var resolvedActionId = ActionManager.GetSpellIdForAction((ActionType)actionType, actionID);
                var context = GetCurrentContext();
                RecordCapture(new TimingActionRequestTraceEvent(CaptureTimelineNow, resolvedActionId, manager->currentSequence, context, TimingActionKind.Instant, manager->animationLock, true));
                ApplyResult(controller.ProcessActionRequest(TimelineNow, resolvedActionId, manager->currentSequence, context, TimingActionKind.Instant, manager->animationLock, true));
            });

        private unsafe void CastBegin(ulong objectID, nint packetData)
            => GuardHotPath(nameof(CastBegin), () =>
            {
                var actionManager = Game.actionManager;
                if (actionManager == null || actionManager->castActionId == 0)
                    return;

                RefreshLiveProfile();
                controller.ApplyProfile(liveProfile);

                var resolvedActionId = ActionManager.GetSpellIdForAction(actionManager->castActionType, actionManager->castActionId);
                var context = GetCurrentContext();
                controller.ProcessCastBegin(resolvedActionId, actionManager->currentSequence, context);
                RecordCapture(new TimingCastBeginTraceEvent(CaptureTimelineNow, resolvedActionId, actionManager->currentSequence, context));
            });

        private void CastInterrupt(nint actionManager)
            => GuardHotPath(nameof(CastInterrupt), () =>
            {
                RefreshLiveProfile();
                controller.ApplyProfile(liveProfile);

                var interruptedActionId = controller.TrackedCastActionId;
                var interruptedSequence = controller.TrackedCastSequence;
                var interruptedContext = controller.TrackedCastContext;
                var result = controller.ProcessCastInterrupt(TimelineNow, interruptedActionId, interruptedSequence);
                ApplyResult(result);
                RecordCapture(new TimingCastInterruptTraceEvent(CaptureTimelineNow, interruptedActionId, interruptedSequence, interruptedContext));
            });

        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId* targetEntityIds, float oldLock, float newLock)
            => GuardHotPath(nameof(ReceiveActionEffect), () =>
            {
                if ((nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address)
                    return;

                RefreshLiveProfile();
                controller.ApplyProfile(liveProfile);

                var context = GetCurrentContext();
                RecordCapture(new TimingActionEffectTraceEvent(CaptureTimelineNow, header->SpellId, header->SourceSequence, context, oldLock, newLock, header->AnimationLock));
                ApplyResult(controller.ProcessActionEffect(TimelineNow, header->SpellId, header->SourceSequence, context, oldLock, newLock, header->AnimationLock));
            });

        private void NetworkMessage(nint packet)
            => GuardHotPath(nameof(NetworkMessage), () =>
            {
                controller.ProcessNetworkPacket(TimingPacketClass.Unknown);
                RecordCapture(new TimingNetworkPacketTraceEvent(CaptureTimelineNow, TimingPacketClass.Unknown));
            });

        private void Update()
            => GuardHotPath(nameof(Update), HandleUpdate);

        private unsafe void HandleUpdate()
        {
            RefreshLiveProfile();
            controller.ApplyProfile(liveProfile);

            var actionManager = Game.actionManager;
            var deltaSeconds = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            var inCombat = DalamudApi.Condition[ConditionFlag.InCombat];
            var betweenAreas = DalamudApi.Condition[ConditionFlag.BetweenAreas];
            var localPlayerId = DalamudApi.ObjectTable.LocalPlayer?.GameObjectId is { } gameObjectId ? (long?)gameObjectId : null;
            var hasActiveCast = actionManager != null && actionManager->castActionId != 0;
            var castRemaining = hasActiveCast ? Math.Max(actionManager->castTime - actionManager->elapsedCastTime, 0f) : 0f;
            var currentAnimationLock = actionManager != null ? actionManager->animationLock : 0f;

            RecordCapture(new TimingAdvanceTraceEvent(
                CaptureTimelineNow,
                deltaSeconds,
                inCombat,
                betweenAreas,
                localPlayerId,
                GetCurrentContext(),
                hasActiveCast,
                castRemaining,
                currentAnimationLock));

            ApplyResult(controller.ProcessUpdate(TimelineNow, deltaSeconds, betweenAreas, localPlayerId, hasActiveCast, castRemaining, currentAnimationLock));

            outOfCombatIdleTimer = inCombat ? 0f : outOfCombatIdleTimer + deltaSeconds;
            if ((saveLearnedData || saveRuntimeStats) && ShouldFlushConfigSave(inCombat))
            {
                Config.Save(checkModules: false);
                ResetDirtySaveState();
            }
        }

        public string StartTraceCapture(string label)
        {
            if (captureSession != null)
                return $"Trace capture already running: {captureSession.Trace.Metadata.Label}";

            RefreshLiveProfile();
            controller.ApplyProfile(liveProfile);

            captureStartTimelineSeconds = TimelineNow;
            captureTruncationNotified = false;
            captureSession = new TimingTraceCaptureSession(
                liveProfile,
                controller.ExportKnowledge(),
                string.IsNullOrWhiteSpace(label) ? "live-capture" : label.Trim(),
                typeof(Tsunippy).Assembly.GetName().Version?.ToString() ?? "unknown");

            ApplyResult(controller.ResetRuntime(TimelineNow, RuntimeResetReason.Manual, "Trace capture start.", true, true));
            RecordCapture(new TimingRuntimeResetTraceEvent(0d, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Trace capture start."));

            return $"Started timing trace capture '{captureSession.Trace.Metadata.Label}'.";
        }

        public string StopTraceCapture()
        {
            if (captureSession == null)
                return "No timing trace capture is currently active.";

            var completedSession = captureSession;
            captureSession = null;

            var path = completedSession.SaveToDirectory(GetTraceDirectory());
            LastCapturePath = path;
            return $"Saved timing trace to {path}";
        }

        public string AddTraceCaptureNote(string note)
        {
            if (captureSession == null)
                return "No timing trace capture is currently active.";

            captureSession.AddNote(CaptureTimelineNow, note);
            return "Added a note to the active timing trace.";
        }

        public string RunLabSelfTest()
        {
            var trace = SyntheticTimingTraceFactory.CreateSample();
            var analysis = TimingReplayRunner.Analyze(trace);
            var baseline = TimingReplayRunner.Analyze(trace, TimingControllerProfile.CreateBaseline());
            var comparison = Runtime.Evaluation.TimingReplayEvaluator.Compare(analysis.Replay, baseline.Replay);
            var equivalence = analysis.Equivalence?.IsEquivalent == true ? "equivalent" : "diverged";
            return $"Lab self-test {equivalence}, fingerprint {analysis.Replay.DecisionFingerprint[..12]}, baseline disagreement delta {comparison.DisagreementDeltaMs:F2} ms.";
        }

        private unsafe void ApplyResult(TimingControllerEventResult result)
        {
            if (result.ShouldWriteAnimationLock && Game.actionManager != null)
                Game.actionManager->animationLock = result.AnimationLockToWrite;

            if (result.LearnedDataChanged)
                MarkLearnedDataDirty();

            if (result.RuntimeStatsChanged)
            {
                Config.TotalAnimationLockReduction += result.AnimationLockReductionAdded;
                Config.TotalActionsReduced += result.ActionsReducedAdded;
                MarkRuntimeStatsDirty();
            }

            if (result.EnteredConflictQuarantine && !string.IsNullOrEmpty(result.UserMessage))
                PrintError($"{result.UserMessage} The timing controller switched into quarantine mode.");

            if (captureSession != null && result.Decision.HasValue)
                captureSession.RecordObservedDecision(result.Decision.Value);

            if (Config.EnableLogging && result.Decision.HasValue)
                PrintLog(FormatDecisionLog(result.Decision.Value));
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

            ApplyResult(controller.EnterFailureQuarantine(TimelineNow, lastHotPathFailure));
            if (captureSession != null)
                RecordCapture(new TimingRuntimeResetTraceEvent(CaptureTimelineNow, RuntimeResetReason.RuntimeFailure, new TimingResetSemantics(false, false), lastHotPathFailure));

            DalamudApi.ShowNotification("Tsunippy timing runtime entered failure quarantine. Diagnostics now report the last failure.", Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
        }

        private void RefreshLiveProfile()
        {
            liveProfile.Name = "live";
            liveProfile.Strategy = Config.ControllerStrategy;
            liveProfile.EnableCastLockPrediction = Config.EnableCastLockPrediction;
            liveProfile.EnableDryRun = Config.EnableDryRun;
            liveProfile.LearnAnimationLocks = Config.LearnAnimationLocks;
            liveProfile.LearnCastTax = Config.LearnCastTax;
            liveProfile.JKAlpha = Config.JKAlpha;
            liveProfile.JKBeta = Config.JKBeta;
            liveProfile.JKK = Config.JKK;
            liveProfile.DynamicFloorScaling = Config.DynamicFloorScaling;
            liveProfile.DynamicFloorWindow = Config.DynamicFloorWindow;
            liveProfile.DefaultActionLock = TimingMath.DefaultActionAnimationLock;
            liveProfile.DefaultCasterTax = Config.DefaultCasterTax;
            liveProfile.ExistingActionLockThreshold = TimingMath.DefaultActionAnimationLock + TimingMath.LockEqualityEpsilon;
            liveProfile.ExistingCastLockThreshold = TimingMath.LockEqualityEpsilon;
            liveProfile.MinimumPredictionConfidence = 0.15f;
            liveProfile.CastCompletionWindow = TimingMath.CastCompletionWindow;
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

        private double TimelineNow => runtimeClock.Elapsed.TotalSeconds;
        private double CaptureTimelineNow => captureSession == null ? 0d : Math.Max(TimelineNow - captureStartTimelineSeconds, 0d);

        private void RecordCapture(TimingTraceEvent traceEvent)
        {
            if (captureSession == null || traceEvent == null)
                return;

            captureSession.Record(traceEvent);
            if (captureSession.IsTruncated && !captureTruncationNotified)
            {
                captureTruncationNotified = true;
                DalamudApi.ShowNotification("Tsunippy trace capture hit the event cap and will save as a truncated trace.", Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
            }
        }

        private static string GetTraceDirectory()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TsunippyLab", "Traces");

        private static string FormatDecisionLog(TimingDecisionTrace trace)
        {
            var log = new StringBuilder()
                .Append("t+").Append(trace.TimelineSeconds.ToString("F3")).Append("s")
                .Append(" | ").Append(trace.Mode)
                .Append(" | ").Append(trace.Source).Append('/').Append(trace.Reason)
                .Append(" | ").Append(trace.ActionKind).Append(' ').Append(trace.ActionId)
                .Append(" seq ").Append(trace.Sequence);

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
            return log.ToString();
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Animation Lock Reduction", ref Config.EnableAnimLockComp))
                Config.Save();
            PluginUI.SetItemTooltip("Authoritative timing controller for live prediction, replay capture, and deterministic correction.");

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
                PluginUI.SetItemTooltip("Keeps the timing controller active for measurement and replay capture without writing animation locks.");
                ImGui.Columns(1);

                if (ImGui.Checkbox("Learn Animation Locks", ref Config.LearnAnimationLocks))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Learns action locks into the live knowledge base used for both runtime control and offline replay.");

                ImGui.TextUnformatted($"Runtime Mode: {CurrentMode}");
                ImGui.TextUnformatted($"Decision Quality: {CurrentQuality}");
                if (!string.IsNullOrEmpty(LastSuppressionReason))
                    ImGui.TextWrapped($"Last Suppression: {LastSuppressionReason}");

                if (ImGui.Button("Reset Runtime State"))
                {
                    ApplyResult(controller.ResetRuntime(TimelineNow, RuntimeResetReason.Manual, "Manual runtime reset.", true, true));
                    RecordCapture(new TimingRuntimeResetTraceEvent(CaptureTimelineNow, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Manual runtime reset."));
                }
                PluginUI.SetItemTooltip("Clears transient timing state, estimators, and pending decisions without wiping learned knowledge.");

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

                    var strategyIndex = (int)Config.ControllerStrategy;
                    if (ImGui.Combo("Controller Strategy", ref strategyIndex, "Confidence Adaptive\0Variance Only\0"))
                    {
                        Config.ControllerStrategy = (TimingControllerStrategy)strategyIndex;
                        Config.Save(checkModules: false);
                    }
                    PluginUI.SetItemTooltip("Confidence Adaptive is the frontier controller. Variance Only is a simpler baseline suited for side-by-side lab comparison.");

                    if (ImGui.Button("Reset to Defaults"))
                    {
                        Config.JKAlpha = 0.125f;
                        Config.JKBeta = 0.25f;
                        Config.JKK = 2.0f;
                        Config.DynamicFloorScaling = 0.85f;
                        Config.ControllerStrategy = TimingControllerStrategy.ConfidenceAdaptive;
                        ApplyResult(controller.ResetRuntime(TimelineNow, RuntimeResetReason.Manual, "Advanced parameters reset to defaults.", true, true));
                        RecordCapture(new TimingRuntimeResetTraceEvent(CaptureTimelineNow, RuntimeResetReason.Manual, new TimingResetSemantics(true, true), "Advanced parameters reset to defaults."));
                        Config.Save(checkModules: false);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Reset Learned Locks"))
                    {
                        Config.LockDb.Reset();
                        Config.CastTaxDb.Reset();
                        controller = new TimingControllerEngine(liveProfile, Config.LockDb, Config.CastTaxDb, 16);
                        Config.Save(checkModules: false);
                    }

                    ImGui.TreePop();
                }

                if (ImGui.TreeNode("Controller Lab"))
                {
                    if (captureSession == null)
                    {
                        if (ImGui.Button("Start Trace Capture"))
                            PrintEcho(StartTraceCapture("manual-ui"));
                    }
                    else
                    {
                        if (ImGui.Button("Stop Trace Capture"))
                            PrintEcho(StopTraceCapture());
                    }

                    ImGui.TextUnformatted($"Capture Active: {IsCaptureActive}");
                    ImGui.TextUnformatted($"Captured Events: {CaptureEventCount}");
                    if (!string.IsNullOrEmpty(CaptureLabel))
                        ImGui.TextWrapped($"Trace Label: {CaptureLabel}");
                    if (!string.IsNullOrEmpty(LastCapturePath))
                        ImGui.TextWrapped($"Last Trace: {LastCapturePath}");
                    if (CaptureTruncated)
                        ImGui.TextWrapped("Current trace is truncated due to the capture event cap.");

                    if (ImGui.Button("Run Lab Self-Test"))
                        PrintEcho(RunLabSelfTest());

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
