using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Bindings.ImGui;
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
        private int encounterClipCount = 0;
        private int encounterGCDCount = 0;

        // Per-action clip tracking
        private readonly Dictionary<uint, (float totalClip, int count)> perActionClips = new();

        // Last encounter results (for display)
        private string lastEncounterSummary = "";

        private void BeginEncounter()
        {
            begunEncounter = DateTime.Now;
            encounterTotalClip = 0;
            encounterTotalWaste = 0;
            encounterClipCount = 0;
            encounterGCDCount = 0;
            currentWastedGCD = 0;
            perActionClips.Clear();
        }

        private void EndEncounter()
        {
            var span = DateTime.Now - begunEncounter;
            var formattedTime = $"{Math.Floor(span.TotalMinutes):00}:{span.Seconds:00}";
            var avgClip = encounterClipCount > 0 ? encounterTotalClip / encounterClipCount : 0;

            lastEncounterSummary = $"[{formattedTime}] Clip: {encounterTotalClip:0.00}s ({encounterClipCount} clips, avg {F2MS(avgClip)} ms), Waste: {encounterTotalWaste:0.00}s";
            PrintLog($"Encounter stats: {lastEncounterSummary}");

            // Log per-action breakdown if we have data
            if (Config.EnableEncounterStatsLogging && perActionClips.Count > 0)
            {
                PrintLog("Per-action clip breakdown:");
                foreach (var (actionId, (totalClip, count)) in perActionClips)
                {
                    var avg = totalClip / count;
                    PrintLog($"  Action {actionId}: {F2MS(totalClip)} ms total, {count} clips, avg {F2MS(avg)} ms");
                }
            }

            begunEncounter = DateTime.MinValue;
        }

        private unsafe void DetectClipping()
        {
            var animationLock = Game.actionManager->animationLock;
            if (lastDetectedClip == Game.actionManager->currentSequence
                || Game.actionManager->isGCDRecastActive
                || animationLock <= 0) return;

            // Detect new GCD start (for counting)
            encounterGCDCount++;

            // Filter out cast tax (0.1s is standard caster tax)
            if (animationLock != 0.1f)
            {
                encounterTotalClip += animationLock;
                encounterClipCount++;

                // Track per-action
                var animLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
                var actionId = animLockModule?.LastActionID ?? 0;
                if (actionId != 0)
                {
                    if (perActionClips.TryGetValue(actionId, out var existing))
                        perActionClips[actionId] = (existing.totalClip + animationLock, existing.count + 1);
                    else
                        perActionClips[actionId] = (animationLock, 1);
                }

                if (Config.EnableEncounterStatsLogging)
                    PrintLog($"GCD Clip: {F2MS(animationLock)} ms (action: {actionId})");
            }

            lastDetectedClip = Game.actionManager->currentSequence;
        }

        private unsafe void DetectWastedGCD()
        {
            if (!Game.actionManager->isGCDRecastActive && !Game.actionManager->isQueued)
            {
                if (Game.actionManager->animationLock > 0) return;
                currentWastedGCD += (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            }
            else if (currentWastedGCD > 0)
            {
                encounterTotalWaste += currentWastedGCD;
                if (Config.EnableEncounterStatsLogging)
                    PrintLog($"Wasted GCD: {F2MS(currentWastedGCD)} ms");
                currentWastedGCD = 0;
            }
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

        public override void DrawConfig()
        {
            ImGui.Columns(2, "EncounterColumns", false);

            if (ImGui.Checkbox("Enable Encounter Stats", ref Config.EnableEncounterStats))
                Config.Save();
            PluginUI.SetItemTooltip("Tracks clips and wasted GCD time while in combat,\nand logs the total afterwards.\nAlso tracks per-action clip breakdown.");

            ImGui.NextColumn();

            if (Config.EnableEncounterStats)
            {
                if (ImGui.Checkbox("Enable Stats Logging", ref Config.EnableEncounterStatsLogging))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Logs individual encounter clips and wasted GCD time.");
            }

            ImGui.Columns(1);

            // Show last encounter summary
            if (!string.IsNullOrEmpty(lastEncounterSummary))
            {
                ImGui.Spacing();
                ImGui.TextWrapped($"Last: {lastEncounterSummary}");
            }

            // Show current encounter if in combat
            if (begunEncounter != DateTime.MinValue)
            {
                var span = DateTime.Now - begunEncounter;
                var avgClip = encounterClipCount > 0 ? encounterTotalClip / encounterClipCount : 0;
                ImGui.Spacing();
                ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f),
                    $"In Combat [{Math.Floor(span.TotalMinutes):00}:{span.Seconds:00}]: {encounterTotalClip:0.00}s clip ({encounterClipCount}x, avg {F2MS(avgClip)} ms), {encounterTotalWaste:0.00}s waste");
            }
        }

        public override void Enable() => Game.OnUpdate += Update;
        public override void Disable() => Game.OnUpdate -= Update;
    }
}
