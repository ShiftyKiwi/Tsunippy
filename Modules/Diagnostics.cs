using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableDiagnostics = false;
        public bool DiagnosticsOverlay = false;
    }
}

namespace Tsunippy.Modules
{
    /// <summary>
    /// Real-time diagnostics overlay module.
    ///
    /// New module not present in NoClippy. Displays:
    /// - Current SRTT (smoothed RTT) in ms
    /// - Current RTTVAR (jitter) in ms
    /// - Dynamic floor value in ms
    /// - Last correction applied in ms
    /// - Packets in last 50ms window
    /// - Lock database confidence for last action
    /// - Current effective simulated RTT
    ///
    /// Invaluable for tuning Jacobson/Karels parameters and understanding
    /// the plugin's real-time behavior.
    /// </summary>
    public class Diagnostics : Module
    {
        public override bool IsEnabled
        {
            get => Config.EnableDiagnostics;
            set => Config.EnableDiagnostics = value;
        }

        public override int DrawOrder => 10;

        private void DrawOverlay()
        {
            if (!Config.EnableDiagnostics || !Config.DiagnosticsOverlay) return;

            var animLock = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
            if (animLock == null) return;

            ImGui.SetNextWindowSize(new Vector2(300, 0) * ImGuiHelpers.GlobalScale);
            ImGui.Begin("Tsunippy Diagnostics", ref Config.DiagnosticsOverlay,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize);

            var green = new Vector4(0.4f, 1f, 0.4f, 1f);
            var yellow = new Vector4(1f, 1f, 0.4f, 1f);
            var white = new Vector4(1f, 1f, 1f, 1f);
            var gray = new Vector4(0.6f, 0.6f, 0.6f, 1f);

            // RTT Estimator State
            ImGui.TextColored(yellow, "RTT Estimator (Jacobson/Karels)");
            ImGui.Separator();

            DrawStatRow("Smoothed RTT", FormatMs(animLock.CurrentSRTT), animLock.CurrentSRTT > 0 ? green : gray);
            DrawStatRow("RTT Variance", FormatMs(animLock.CurrentRTTVAR), green);
            DrawStatRow("Maturity", $"{animLock.EstimatorMaturity} ({animLock.VarianceTrustFactor:P0} variance)", animLock.IsRttWarm ? green : yellow);
            DrawStatRow("Connection", $"{animLock.ConnectionClassification}", green);
            DrawStatRow("Last RTT", FormatMs(animLock.LastRTT), white);
            DrawStatRow("RTT Samples", $"{animLock.RTTSampleCount}", gray);

            ImGui.Spacing();
            ImGui.TextColored(yellow, "Dynamic Floor");
            ImGui.Separator();

            DrawStatRow("Current Floor", FormatMs(animLock.CurrentFloor), green);
            DrawStatRow("Raw Min", FormatMs(animLock.RawMinRTT), gray);
            DrawStatRow("Mode", $"{animLock.CurrentFloorMode}", white);
            DrawStatRow("Last Adjust", animLock.LastFloorAdjustmentReason, gray);
            DrawStatRow("Floor Samples", $"{animLock.FloorSampleCount}", gray);
            DrawStatRow("NoClippy Floor", "40 ms", gray);

            ImGui.Spacing();
            ImGui.TextColored(yellow, "Epoch / Safety");
            ImGui.Separator();
            DrawStatRow("Profile", $"{Config.Profile} -> {animLock.EffectiveProfile}", white);
            DrawStatRow("Epoch", $"{animLock.CurrentEpoch}", white);
            DrawStatRow("Last Reset", $"{animLock.LastEpochResetReason} ({animLock.TimeSinceEpochReset.TotalSeconds:0.0}s)", gray);
            DrawStatRow("Safe Mode", animLock.SafeModeActive ? animLock.LastSafeModeReason : "none", animLock.SafeModeActive ? yellow : green);
            DrawStatRow("Pending", $"{animLock.PendingPredictionCount}", gray);
            DrawStatRow("Expired", $"{animLock.ExpiredPredictionCount}", gray);
            DrawStatRow("Stale Invalid", $"{animLock.StalePredictionsInvalidated}", gray);
            DrawStatRow("Prediction", animLock.LastPredictionReason, white);

            ImGui.Spacing();
            ImGui.TextColored(yellow, "Last Action");
            ImGui.Separator();

            DrawStatRow("Action ID", $"{animLock.LastActionID}", white);
            DrawStatRow("Correction", FormatMs(animLock.LastCorrection), white);
            DrawStatRow("Variance Buffer", FormatMs(animLock.LastVarianceBuffer), white);
            DrawStatRow("Adjusted Lock", FormatMs(animLock.LastAdjustedLock), green);
            DrawStatRow("Packets (50ms)", $"{animLock.PacketsSent}", gray);
            DrawStatRow("Action Packets", $"{animLock.ActionPacketsSent}", gray);
            DrawStatRow("Pending Saves", $"{animLock.PendingLearnedEntries}", gray);
            DrawStatRow("Conflict State", animLock.ConflictDetected ? "Dry Run" : "Normal", animLock.ConflictDetected ? yellow : green);

            var decision = animLock.LastDecision;
            if (decision != null)
            {
                ImGui.Spacing();
                ImGui.TextColored(yellow, "Last Decision");
                ImGui.Separator();
                DrawStatRow("Formula", decision.HasFormula
                    ? $"{FormatMs(decision.ExistingLockBeforeWrite)} + {FormatMs(decision.Correction)} + {FormatMs(decision.VarianceBuffer)} = {FormatMs(decision.FinalAppliedLock)}"
                    : "n/a (decision was not applied)", decision.HasFormula ? white : gray);
                DrawStatRow("Reason", decision.DecisionReason, white);
                if (!string.IsNullOrEmpty(decision.RejectionReason))
                    DrawStatRow("Rejected", decision.RejectionReason, yellow);
                DrawStatRow("Replay Rows", $"{animLock.ReplayRecordCount}", gray);
            }

            var cast = global::Tsunippy.Modules.Modules.GetInstance<CastLockPrediction>();
            if (cast != null)
            {
                ImGui.Spacing();
                ImGui.TextColored(yellow, "Cast Prediction");
                ImGui.Separator();
                DrawStatRow("Predicted", FormatMs(cast.LastPredictedCastLock), white);
                DrawStatRow("Actual", FormatMs(cast.LastActualCastLock), white);
                DrawStatRow("Replay", "cast decision not replay-recorded", gray);
                DrawStatRow("Pending", $"{cast.PendingPredictionCount}", gray);
                DrawStatRow("Expired", $"{cast.ExpiredPredictionCount}", gray);
                DrawStatRow("State", cast.LastPredictionReason, white);
            }

            // Database info
            if (animLock.LastActionID != 0)
            {
                var context = DalamudApi.ClientState.IsPvP
                    ? Database.GameContext.PvP
                    : Database.GameContext.PvE;
                var entry = Config.LockDb.GetEntry(animLock.LastActionID, context);
                if (entry != null)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(yellow, "Lock Database");
                    ImGui.Separator();
                    DrawStatRow("Mean Lock", FormatMs(entry.MeanLock), white);
                    DrawStatRow("Confidence", $"{entry.Confidence:P0} ({entry.SampleCount} samples)", white);
                }
            }

            ImGui.Spacing();
            ImGui.TextColored(yellow, "Parameters");
            ImGui.Separator();
            DrawStatRow("Alpha", $"{Config.JKAlpha:F3}", gray);
            DrawStatRow("Beta", $"{Config.JKBeta:F3}", gray);
            DrawStatRow("K", $"{Config.JKK:F1}", gray);
            DrawStatRow("Floor Scale", $"{Config.DynamicFloorScaling:F2}", gray);

            ImGui.Spacing();
            ImGui.TextColored(yellow, "Hook Health");
            ImGui.Separator();
            DrawStatRow("Hooks Ready", $"{Game.EnabledHookCount}/{Game.ExpectedHookCount}", Game.IsInitialized ? green : yellow);
            DrawStatRow("Runtime Failures", $"{Game.RuntimeFailureCount}", Game.RuntimeFailureCount == 0 ? green : yellow);
            if (!string.IsNullOrEmpty(Game.LastInitializationError))
                ImGui.TextWrapped($"Init error: {Game.LastInitializationError}");
            if (!string.IsNullOrEmpty(Game.LastRuntimeFailure))
                ImGui.TextWrapped($"Last runtime failure: {Game.LastRuntimeFailure}");

            var statuses = global::Tsunippy.Modules.Modules.GetStatusSnapshot();
            if (statuses.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(yellow, "Module Health");
                ImGui.Separator();
                foreach (var status in statuses)
                {
                    DrawStatRow(status.Name, status.IsEnabled ? "Enabled" : "Disabled", status.IsEnabled ? green : yellow);
                    if (!string.IsNullOrEmpty(status.LastFailure))
                        ImGui.TextWrapped($"  {status.LastFailure}");
                }
            }

            ImGui.End();
        }

        public void DrawOverlayWindow() => DrawOverlay();

        private static void DrawStatRow(string label, string value, Vector4 valueColor)
        {
            ImGui.TextUnformatted($"  {label}:");
            ImGui.SameLine(160 * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(valueColor, value);
        }

        private static string FormatMs(float seconds)
            => float.IsFinite(seconds) && seconds >= 0
                ? $"{F2MS(seconds)} ms"
                : "n/a";

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Diagnostics", ref Config.EnableDiagnostics))
                Config.Save();
            PluginUI.SetItemTooltip("Enables the real-time diagnostics system.\nShows RTT estimator state, dynamic floor, and correction details.");

            if (Config.EnableDiagnostics)
            {
                ImGui.SameLine();
                if (ImGui.Checkbox("Show Overlay", ref Config.DiagnosticsOverlay))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Opens a separate floating window with live diagnostics.");
            }
        }
    }
}
