using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using Tsunippy.Runtime;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableEncounterStats = false;
        public bool EnableEncounterStatsLogging = false;
    }
}

namespace Tsunippy.Modules
{
    /// <summary>
    /// Enhanced encounter statistics module.
    ///
    /// Improvements over NoClippy:
    /// - Per-action clip tracking (which actions clipped and by how much)
    /// - Running averages in the UI
    /// - Same combat begin/end lifecycle
    /// </summary>
    public class Stats : Module
    {
        private const int PendingPredictionSkipWindowMs = 1000;
        private const int PluginOwnedSuppressionWindowMs = 1800;
        private const int RecentPluginOwnedDecisionWindowMs = 750;
        private const int RecentIssuedActionSequenceWindowMs = 8000;
        private const int RecentIssuedActionNearNowWindowMs = 1500;
        private const int ActiveEngagementWindowMs = 3500;
        private const int LiveUiRefreshIntervalMs = 250;
        private const float PredictionLockUpperTolerance = 0.030f;
        private const float PredictionLockLowerTolerance = 0.180f;
        private const float PluginOwnedResidualUpperTolerance = 0.050f;
        private const float MaxCommittedWasteSeconds = 3.0f;

        public override bool IsEnabled
        {
            get => Config.EnableEncounterStats;
            set => Config.EnableEncounterStats = value;
        }

        public override int DrawOrder => 5;

        // Encounter tracking state
        private DateTime begunEncounter = DateTime.MinValue;
        private ushort lastDetectedClip = 0;
        private float currentWastedGCD = 0;
        private float encounterTotalClip = 0;
        private float encounterTotalWaste = 0;
        private float encounterCastTax = 0;
        private float encounterLockInduced = 0;
        private float encounterUnknown = 0;
        private float encounterIgnoredDowntime = 0;
        private float encounterPluginOwnedSuppressed = 0;
        private int encounterClipCount = 0;
        private int encounterAttributedClipCount = 0;
        private int encounterUnknownClipCount = 0;
        private int encounterGCDCount = 0;
        private int unknownAttributionCount = 0;
        private int encounterSkippedPendingPredictions = 0;
        private int encounterPluginOwnedSuppressedCount = 0;
        private int encounterIgnoredDowntimeWindows = 0;
        private long lastCombatActivityTick = 0;
        private bool hasRecentCombatActivity = false;
        private AnimationLock animationLockModule;

        // Per-action clip tracking
        private readonly Dictionary<uint, ActionClipStats> perActionClips = new();
        private readonly Dictionary<ushort, PluginOwnedSuppression> pluginOwnedSuppressions = new();
        private readonly RecentIssuedActionTracker recentIssuedActions = new();

        // Last encounter results (for display)
        private string lastEncounterSummary = "";
        private string liveEncounterText = "";
        private string liveUnknownAttributionText = "";
        private string livePluginOwnedText = "";
        private string liveIgnoredDowntimeText = "";
        private long nextLiveUiRefreshTick;
        private readonly Queue<string> previousEncounters = new();
        private const int EncounterHistoryLimit = 5;

        private enum ClipKind
        {
            Normal,
            CastTax,
            LockInduced,
            Unknown,
        }

        private sealed class PluginOwnedSuppression
        {
            public ushort Sequence;
            public uint ActionId;
            public float ReferenceLock;
            public long CreatedTick;
            public long ExpiresTick;
            public string State = "unknown";
            public string Reason = "unknown";
            public bool Counted;
        }

        private sealed class ActionClipStats
        {
            public float TotalClip;
            public float CastTax;
            public float LockInduced;
            public float Unknown;
            public float MaxClip;
            public int Count;

            public void Add(float amount, ClipKind kind)
            {
                TotalClip += amount;
                MaxClip = Math.Max(MaxClip, amount);
                Count++;

                switch (kind)
                {
                    case ClipKind.CastTax:
                        CastTax += amount;
                        break;
                    case ClipKind.LockInduced:
                        LockInduced += amount;
                        break;
                    case ClipKind.Unknown:
                        Unknown += amount;
                        break;
                }
            }
        }

        private void BeginEncounter()
        {
            begunEncounter = DateTime.Now;
            encounterTotalClip = 0;
            encounterTotalWaste = 0;
            encounterCastTax = 0;
            encounterLockInduced = 0;
            encounterUnknown = 0;
            encounterIgnoredDowntime = 0;
            encounterPluginOwnedSuppressed = 0;
            encounterClipCount = 0;
            encounterAttributedClipCount = 0;
            encounterUnknownClipCount = 0;
            encounterGCDCount = 0;
            unknownAttributionCount = 0;
            encounterSkippedPendingPredictions = 0;
            encounterPluginOwnedSuppressedCount = 0;
            encounterIgnoredDowntimeWindows = 0;
            currentWastedGCD = 0;
            lastCombatActivityTick = 0;
            hasRecentCombatActivity = false;
            nextLiveUiRefreshTick = 0;
            liveEncounterText = "";
            liveUnknownAttributionText = "";
            livePluginOwnedText = "";
            liveIgnoredDowntimeText = "";
            perActionClips.Clear();
            pluginOwnedSuppressions.Clear();
        }

        private void EndEncounter()
        {
            var span = DateTime.Now - begunEncounter;
            var formattedTime = $"{Math.Floor(span.TotalMinutes):00}:{span.Seconds:00}";
            var avgClip = encounterClipCount > 0 ? encounterTotalClip / encounterClipCount : 0;

            lastEncounterSummary = $"[{formattedTime}] Clip: {encounterTotalClip:0.00}s ({encounterClipCount} clips, avg {F2MS(avgClip)} ms, attributed {encounterAttributedClipCount}, unknown {encounterUnknownClipCount}), Plugin-owned: {encounterPluginOwnedSuppressed:0.00}s ({encounterPluginOwnedSuppressedCount}, fresh pending {encounterSkippedPendingPredictions}), Lock: {encounterLockInduced:0.00}s, Cast tax: {encounterCastTax:0.00}s, Unknown: {encounterUnknown:0.00}s, Waste: {encounterTotalWaste:0.00}s";
            if (encounterIgnoredDowntimeWindows > 0)
                lastEncounterSummary += $", Ignored downtime: {encounterIgnoredDowntime:0.00}s ({encounterIgnoredDowntimeWindows} windows)";

            previousEncounters.Enqueue(lastEncounterSummary);
            while (previousEncounters.Count > EncounterHistoryLimit)
                previousEncounters.Dequeue();

            PrintLog($"Encounter stats: {lastEncounterSummary}");

            // Log per-action breakdown if we have data
            if (Config.EnableEncounterStatsLogging && perActionClips.Count > 0)
            {
                PrintLog("Per-action clip breakdown:");
                foreach (var (actionId, stats) in perActionClips)
                {
                    var avg = stats.TotalClip / stats.Count;
                    PrintLog($"  Action {actionId}: {F2MS(stats.TotalClip)} ms total, {stats.Count} clips, avg {F2MS(avg)} ms, max {F2MS(stats.MaxClip)} ms, cast-tax {F2MS(stats.CastTax)} ms, lock {F2MS(stats.LockInduced)} ms, unknown {F2MS(stats.Unknown)} ms");
                }
            }

            begunEncounter = DateTime.MinValue;
        }

        private unsafe void DetectClipping()
        {
            var animationLock = Game.actionManager->animationLock;
            var sequence = Game.actionManager->currentSequence;
            if (lastDetectedClip == sequence
                || Game.actionManager->isGCDRecastActive
                || animationLock <= 0) return;

            var animLockModule = GetAnimationLockModule();
            if (TrySuppressPluginOwnedLock(animLockModule, sequence, animationLock))
            {
                lastDetectedClip = sequence;
                return;
            }

            // Detect new GCD start (for counting)
            encounterGCDCount++;

            var actionId = ResolveActionIdForClip(animLockModule, sequence);
            var kind = ClassifyClip(animationLock, actionId);

            if (kind != ClipKind.Normal)
            {
                encounterTotalClip += animationLock;
                encounterClipCount++;
                switch (kind)
                {
                    case ClipKind.CastTax:
                        encounterCastTax += animationLock;
                        break;
                    case ClipKind.LockInduced:
                        encounterLockInduced += animationLock;
                        break;
                    case ClipKind.Unknown:
                        encounterUnknown += animationLock;
                        break;
                }

                if (kind == ClipKind.Unknown)
                    encounterUnknownClipCount++;
                else
                    encounterAttributedClipCount++;

                // Track per-action
                if (actionId != 0)
                {
                    if (!perActionClips.TryGetValue(actionId, out var stats))
                    {
                        stats = new ActionClipStats();
                        perActionClips[actionId] = stats;
                    }

                    stats.Add(animationLock, kind);
                }

                if (Config.EnableEncounterStatsLogging)
                    PrintLog($"GCD Clip: {F2MS(animationLock)} ms ({kind}, action: {actionId})");
            }

            lastDetectedClip = sequence;
        }

        private unsafe void DetectWastedGCD()
        {
            var now = Environment.TickCount64;
            if (ShouldResetWastedGcdWindow())
                return;

            if (Game.actionManager->isGCDRecastActive || Game.actionManager->isQueued || Game.actionManager->animationLock > 0)
            {
                MarkCombatActivity(now);
                CommitOrDiscardWastedGcd();
                return;
            }

            if (!hasRecentCombatActivity || now - lastCombatActivityTick > ActiveEngagementWindowMs)
            {
                DiscardWastedGcdWindow();
                return;
            }

            currentWastedGCD += (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            if (currentWastedGCD > MaxCommittedWasteSeconds)
                DiscardWastedGcdWindow();
        }

        private ClipKind ClassifyClip(float animationLock, uint actionId)
        {
            if (animationLock <= 0)
                return ClipKind.Normal;

            if (actionId == 0)
                return ClipKind.Unknown;

            var context = DalamudApi.ClientState.IsPvP ? Database.GameContext.PvP : Database.GameContext.PvE;
            var learnedTax = Config.CastTaxDb.GetTax(actionId, context, Config.DefaultCasterTax);
            var entry = Config.CastTaxDb.GetEntry(actionId, context);
            var tolerance = Math.Max(0.015f, (entry?.MeanDeviation ?? 0.005f) * 4f);

            if (Math.Abs(animationLock - learnedTax) <= tolerance || animationLock <= learnedTax + tolerance)
                return ClipKind.CastTax;

            return ClipKind.LockInduced;
        }

        private uint ResolveActionIdForClip(AnimationLock animLockModule, ushort sequence)
        {
            if (animLockModule != null
                && animLockModule.TryGetRecentAcceptedActionForSequence(sequence, TimeSpan.FromSeconds(2), out var actionId))
                return actionId;

            if (animLockModule != null
                && animLockModule.TryGetRecentIssuedActionForSequence(sequence, TimeSpan.FromMilliseconds(RecentIssuedActionSequenceWindowMs), out var moduleIssuedAction))
                return ResolveFromIssuedAction(sequence, moduleIssuedAction, "animation-lock sequence");

            if (recentIssuedActions.TryFindBySequence(sequence, TimeSpan.FromMilliseconds(RecentIssuedActionSequenceWindowMs), out var statsIssuedAction))
                return ResolveFromIssuedAction(sequence, statsIssuedAction, "stats sequence");

            if (recentIssuedActions.TryFindNearNow(TimeSpan.FromMilliseconds(RecentIssuedActionNearNowWindowMs), out statsIssuedAction))
                return ResolveFromIssuedAction(sequence, statsIssuedAction, "stats nearby");

            if (animLockModule != null
                && animLockModule.TryGetRecentIssuedActionNearNow(TimeSpan.FromMilliseconds(RecentIssuedActionNearNowWindowMs), out moduleIssuedAction))
                return ResolveFromIssuedAction(sequence, moduleIssuedAction, "animation-lock nearby");

            unknownAttributionCount++;
            return 0;
        }

        private static uint ResolveFromIssuedAction(ushort clipSequence, RecentIssuedAction action, string reason)
        {
            if (Config.EnableEncounterStatsLogging)
            {
                var age = action.AgeMilliseconds(Environment.TickCount64);
                DalamudApi.LogDebug($"Stats attributed raw clip via recent issued action: clipSeq={clipSequence}, issuedSeq={action.Sequence}, action={action.ActionId}, original={action.OriginalActionId}, age={age} ms, source={action.Source}/{action.ActionType}, reason={reason}");
            }

            return action.ActionId;
        }

        private bool TrySuppressPluginOwnedLock(AnimationLock animLockModule, ushort sequence, float animationLock)
        {
            if (animLockModule == null)
                return false;

            var now = Environment.TickCount64;
            if (pluginOwnedSuppressions.TryGetValue(sequence, out var suppression))
            {
                if (suppression.ExpiresTick >= now && IsPlausiblePluginOwnedResidual(animationLock, suppression.ReferenceLock))
                {
                    CountPluginOwnedSuppression(suppression, animationLock, now, "suppression-window");
                    return true;
                }

                if (suppression.ExpiresTick < now)
                    pluginOwnedSuppressions.Remove(sequence);
            }

            if (animLockModule.TryGetRecentPredictionState(sequence, TimeSpan.FromMilliseconds(PendingPredictionSkipWindowMs), out var prediction)
                && prediction.IsPendingForSequence
                && IsPlausiblePluginOwnedResidual(animationLock, prediction.PredictedLock))
            {
                encounterSkippedPendingPredictions++;
                suppression = CreateSuppression(sequence, prediction, now, "fresh pending prediction");
                pluginOwnedSuppressions[sequence] = suppression;
                CountPluginOwnedSuppression(suppression, animationLock, now, "fresh-pending");
                return true;
            }

            if (animLockModule.TryGetRecentPluginOwnedState(sequence, TimeSpan.FromMilliseconds(RecentPluginOwnedDecisionWindowMs), out var owned)
                && IsPlausiblePluginOwnedResidual(animationLock, owned.PredictedLock))
            {
                suppression = CreateSuppression(sequence, owned, now, "recent plugin-owned decision");
                pluginOwnedSuppressions[sequence] = suppression;
                CountPluginOwnedSuppression(suppression, animationLock, now, "recent-owned");
                return true;
            }

            return false;
        }

        private static bool IsNearPredictedLock(float animationLock, float predictedLock)
        {
            if (!float.IsFinite(animationLock) || !float.IsFinite(predictedLock) || predictedLock <= 0)
                return false;

            var lowerTolerance = Math.Max(PredictionLockLowerTolerance, predictedLock * 0.25f);
            return animationLock <= predictedLock + PredictionLockUpperTolerance
                   && animationLock >= predictedLock - lowerTolerance;
        }

        private static bool IsPlausiblePluginOwnedResidual(float animationLock, float referenceLock)
        {
            if (!float.IsFinite(animationLock) || !float.IsFinite(referenceLock) || animationLock <= 0 || referenceLock <= 0)
                return false;

            return IsNearPredictedLock(animationLock, referenceLock)
                   || animationLock <= referenceLock + PluginOwnedResidualUpperTolerance;
        }

        private static PluginOwnedSuppression CreateSuppression(ushort sequence, Runtime.RecentPredictionState state, long now, string reason)
            => new()
            {
                Sequence = sequence,
                ActionId = state.ActionId,
                ReferenceLock = state.PredictedLock,
                CreatedTick = state.CreatedTick > 0 ? state.CreatedTick : now,
                ExpiresTick = now + PluginOwnedSuppressionWindowMs,
                State = state.State,
                Reason = reason,
            };

        private void CountPluginOwnedSuppression(PluginOwnedSuppression suppression, float animationLock, long now, string reason)
        {
            if (suppression.Counted)
                return;

            suppression.Counted = true;
            encounterPluginOwnedSuppressed += animationLock;
            encounterPluginOwnedSuppressedCount++;
            if (Config.EnableEncounterStatsLogging)
                DalamudApi.LogDebug($"Stats suppressed plugin-owned lock: seq={suppression.Sequence}, action={suppression.ActionId}, lock={F2MS(animationLock)} ms, reference={F2MS(suppression.ReferenceLock)} ms, age={now - suppression.CreatedTick} ms, state={suppression.State}, reason={reason}/{suppression.Reason}");
        }

        private bool ShouldResetWastedGcdWindow()
        {
            if (DalamudApi.Condition[ConditionFlag.BetweenAreas] || DalamudApi.ObjectTable.LocalPlayer == null)
            {
                DiscardWastedGcdWindow();
                hasRecentCombatActivity = false;
                lastCombatActivityTick = 0;
                return true;
            }

            return false;
        }

        private void MarkCombatActivity(long now)
        {
            hasRecentCombatActivity = true;
            lastCombatActivityTick = now;
        }

        private void CommitOrDiscardWastedGcd()
        {
            if (currentWastedGCD <= 0)
                return;

            if (currentWastedGCD <= MaxCommittedWasteSeconds)
            {
                encounterTotalWaste += currentWastedGCD;
                if (Config.EnableEncounterStatsLogging)
                    PrintLog($"Wasted GCD: {F2MS(currentWastedGCD)} ms");
            }
            else
            {
                encounterIgnoredDowntime += currentWastedGCD;
                encounterIgnoredDowntimeWindows++;
                if (Config.EnableEncounterStatsLogging)
                    DalamudApi.LogDebug($"Stats ignored downtime-like GCD gap: {F2MS(currentWastedGCD)} ms");
            }

            currentWastedGCD = 0;
        }

        private void DiscardWastedGcdWindow()
        {
            if (currentWastedGCD <= 0)
                return;

            encounterIgnoredDowntime += currentWastedGCD;
            encounterIgnoredDowntimeWindows++;
            if (Config.EnableEncounterStatsLogging)
                DalamudApi.LogDebug($"Stats discarded GCD downtime window: {F2MS(currentWastedGCD)} ms");
            currentWastedGCD = 0;
        }

        private void Update()
        {
            if (DalamudApi.Condition[ConditionFlag.InCombat])
            {
                if (begunEncounter == DateTime.MinValue)
                    BeginEncounter();

                DetectClipping();
                DetectWastedGCD();
            }
            else if (begunEncounter != DateTime.MinValue)
            {
                EndEncounter();
            }
        }

        private AnimationLock GetAnimationLockModule()
            => animationLockModule ??= global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();

        private void RefreshLiveUiCacheIfNeeded()
        {
            if (begunEncounter == DateTime.MinValue)
                return;

            var now = Environment.TickCount64;
            if (now < nextLiveUiRefreshTick && liveEncounterText.Length > 0)
                return;

            nextLiveUiRefreshTick = now + LiveUiRefreshIntervalMs;

            // Keep DrawConfig cheap in combat; this is display-only cache data.
            var span = DateTime.Now - begunEncounter;
            var avgClip = encounterClipCount > 0 ? encounterTotalClip / encounterClipCount : 0;
            liveEncounterText = $"In Combat [{Math.Floor(span.TotalMinutes):00}:{span.Seconds:00}]: {encounterTotalClip:0.00}s clip ({encounterClipCount}x, avg {F2MS(avgClip)} ms), attributed {encounterAttributedClipCount}, unknown {encounterUnknownClipCount}, plugin-owned {encounterPluginOwnedSuppressedCount}, waste {encounterTotalWaste:0.00}s";
            liveUnknownAttributionText = unknownAttributionCount > 0
                ? $"Unknown attributions: {unknownAttributionCount}"
                : string.Empty;
            livePluginOwnedText = encounterPluginOwnedSuppressedCount > 0
                ? $"Plugin-owned suppressions: {encounterPluginOwnedSuppressedCount} ({encounterPluginOwnedSuppressed:0.00}s), fresh pending {encounterSkippedPendingPredictions}"
                : string.Empty;
            liveIgnoredDowntimeText = encounterIgnoredDowntimeWindows > 0
                ? $"Ignored downtime-like gaps: {encounterIgnoredDowntime:0.00}s ({encounterIgnoredDowntimeWindows})"
                : string.Empty;
        }

        public override void DrawConfig()
        {
            ImGui.Columns(2, "EncounterColumns", false);

            if (ImGui.Checkbox("Enable Encounter Stats", ref Config.EnableEncounterStats))
                Config.Save();
            PluginUI.SetItemTooltip("Tracks encounter clip, plugin-owned lock, and wasted GCD totals.\nShows summaries in this UI and logs one final encounter summary.");

            ImGui.NextColumn();

            if (Config.EnableEncounterStats)
            {
                if (ImGui.Checkbox("Enable Stats Logging", ref Config.EnableEncounterStatsLogging))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Verbose per-event combat diagnostics for debugging and smoke tests.\nNot recommended for normal gameplay.");
            }

            ImGui.Columns(1);

            // Show last encounter summary
            if (!string.IsNullOrEmpty(lastEncounterSummary))
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("Last:");
                ImGui.SameLine();
                ImGui.TextWrapped(lastEncounterSummary);
            }

            if (previousEncounters.Count > 1 && ImGui.TreeNode("Recent Encounters"))
            {
                foreach (var encounter in previousEncounters)
                    ImGui.TextWrapped(encounter);
                ImGui.TreePop();
            }

            // Show current encounter if in combat
            if (begunEncounter != DateTime.MinValue)
            {
                RefreshLiveUiCacheIfNeeded();
                ImGui.Spacing();
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f),
                    liveEncounterText);
                if (liveUnknownAttributionText.Length > 0)
                    ImGui.TextUnformatted(liveUnknownAttributionText);
                if (livePluginOwnedText.Length > 0)
                    ImGui.TextUnformatted(livePluginOwnedText);
                if (liveIgnoredDowntimeText.Length > 0)
                    ImGui.TextUnformatted(liveIgnoredDowntimeText);
            }
        }

        private unsafe void UseAction(ActionManager* actionManager, ActionType actionType, uint actionID,
            ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId,
            bool* outOptAreaTargeted, bool ret)
        {
            if (!ret)
                return;

            RecordIssuedLocalAction(actionType, actionID, "UseAction");
        }

        private unsafe void UseActionLocation(nint actionManager, uint actionType, uint actionID,
            ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            if (ret == 0)
                return;

            RecordIssuedLocalAction((ActionType)actionType, actionID, "UseActionLocation");
        }

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
                IsGcdRelevant = actionType == ActionType.Action,
            });
        }

        public override unsafe void Enable()
        {
            animationLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
            Game.OnUseAction += UseAction;
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnUpdate += Update;
        }

        public override unsafe void Disable()
        {
            Game.OnUseAction -= UseAction;
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnUpdate -= Update;
            animationLockModule = null;
            recentIssuedActions.Clear();
        }
    }
}
