using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Tsunippy.Database;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableDatabaseBrowser = false;
    }
}

namespace Tsunippy.Modules
{
    public class LearnedDatabaseBrowser : Module
    {
        private string filter = string.Empty;
        private ResetTarget pendingResetTarget = ResetTarget.None;
        private int pendingResetCount;
        private string pendingResetDescription = string.Empty;
        private const string ResetPopupName = "Confirm Learned DB Reset";

        private enum ResetTarget
        {
            None,
            Filtered,
            Locks,
            CastTax,
        }

        public override bool IsEnabled
        {
            get => true;
            set => _ = value;
        }

        public override int DrawOrder => 9;

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Show Learned Database Browser", ref Config.EnableDatabaseBrowser))
                Config.Save(checkModules: false);

            if (!Config.EnableDatabaseBrowser)
                return;

            ImGui.InputText("Filter action ID", ref filter, 32);

            if (ImGui.Button("Export Learned JSON"))
                ExportLearnedJson();

            ImGui.SameLine();
            var hasFilter = !string.IsNullOrWhiteSpace(filter);
            var filteredCount = hasFilter
                ? CountFiltered(Config.LockDb.Entries) + CountFiltered(Config.CastTaxDb.Entries)
                : 0;

            if (!hasFilter)
            {
                ImGui.TextUnformatted("Reset Filtered Entries: enter a filter first");
            }
            else if (ImGui.Button($"Reset {filteredCount} Filtered Entries..."))
            {
                OpenResetPopup(ResetTarget.Filtered, filteredCount, $"filtered entries matching \"{filter}\"");
            }

            if (ImGui.BeginTabBar("TsunippyLearnedDbTabs"))
            {
                if (ImGui.BeginTabItem("Animation Locks"))
                {
                    DrawDatabaseTable(Config.LockDb.Entries, false);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Cast Tax"))
                {
                    DrawDatabaseTable(Config.CastTaxDb.Entries, true);
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            DrawResetPopup();
        }

        private void DrawDatabaseTable(Dictionary<string, LockEntry> entries, bool castTax)
        {
            var resetTarget = castTax ? ResetTarget.CastTax : ResetTarget.Locks;
            var resetLabel = castTax
                ? $"Reset All Cast Tax ({entries.Count})..."
                : $"Reset All Locks ({entries.Count})...";
            if (ImGui.Button(resetLabel))
                OpenResetPopup(resetTarget, entries.Count, castTax ? "all cast-tax entries" : "all animation-lock entries");

            if (!ImGui.BeginTable(castTax ? "CastTaxTable" : "LockTable", 10,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                    new System.Numerics.Vector2(0, 260)))
                return;

            ImGui.TableSetupColumn("Action");
            ImGui.TableSetupColumn("Context");
            ImGui.TableSetupColumn(castTax ? "Tax" : "Lock");
            ImGui.TableSetupColumn("Dev");
            ImGui.TableSetupColumn("Samples");
            ImGui.TableSetupColumn("Conf");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Outliers");
            ImGui.TableSetupColumn("Last Seen");
            ImGui.TableSetupColumn("Actions");
            ImGui.TableHeadersRow();

            var removeKeys = new List<string>();
            var toggleFreezeKeys = new List<string>();
            foreach (var (key, entry) in entries)
            {
                if (!LockDatabase.TryParseKey(key, out var actionId, out var context))
                    continue;

                if (!Matches(actionId))
                    continue;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(actionId.ToString());
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(context.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted($"{F2MS(entry.MeanLock)} ms");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted($"{F2MS(entry.MeanDeviation)} ms");
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(entry.SampleCount.ToString());
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted($"{entry.Confidence:P0}");
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(entry.State.ToString());
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(entry.OutlierStreak.ToString());
                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(FormatLastSeen(entry.LastObservedUnix));
                ImGui.TableSetColumnIndex(9);
                ImGui.PushID(key);
                if (ImGui.SmallButton(entry.Frozen ? "Unfreeze" : "Freeze"))
                    toggleFreezeKeys.Add(key);

                ImGui.SameLine();
                if (ImGui.SmallButton("Reset"))
                    removeKeys.Add(key);
                ImGui.PopID();
            }

            ImGui.EndTable();

            var changed = false;
            foreach (var key in toggleFreezeKeys)
            {
                if (!entries.TryGetValue(key, out var entry))
                    continue;

                if (!LockDatabase.TryParseKey(key, out var actionId, out var context))
                    continue;

                var frozen = !entry.Frozen;
                if (!SetFrozenEntry(actionId, context, frozen, castTax))
                    continue;

                changed = true;
                PrintEcho($"{(frozen ? "Froze" : "Unfroze")} learned entry {key}.");
            }

            foreach (var key in removeKeys)
            {
                if (!LockDatabase.TryParseKey(key, out var actionId, out var context))
                    continue;

                if (!ResetEntry(actionId, context, castTax))
                    continue;

                changed = true;
                PrintEcho($"Reset learned entry {key}.");
            }

            if (changed)
                Config.Save(checkModules: false);
        }

        private static bool SetFrozenEntry(uint actionId, GameContext context, bool frozen, bool castTax)
            => castTax
                ? Config.CastTaxDb.SetFrozen(actionId, context, frozen)
                : Config.LockDb.SetFrozen(actionId, context, frozen);

        private static bool ResetEntry(uint actionId, GameContext context, bool castTax)
            => castTax
                ? Config.CastTaxDb.ResetEntry(actionId, context)
                : Config.LockDb.ResetEntry(actionId, context);

        private bool Matches(uint actionId)
            => string.IsNullOrWhiteSpace(filter)
               || actionId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);

        private void ResetFiltered(Dictionary<string, LockEntry> entries)
        {
            var removeKeys = new List<string>();
            foreach (var (key, _) in entries)
            {
                if (!LockDatabase.TryParseKey(key, out var actionId, out _))
                    continue;

                if (Matches(actionId))
                    removeKeys.Add(key);
            }

            foreach (var key in removeKeys)
                entries.Remove(key);
        }

        private int CountFiltered(Dictionary<string, LockEntry> entries)
        {
            var count = 0;
            foreach (var (key, _) in entries)
            {
                if (!LockDatabase.TryParseKey(key, out var actionId, out _))
                    continue;

                if (Matches(actionId))
                    count++;
            }

            return count;
        }

        private void OpenResetPopup(ResetTarget target, int count, string description)
        {
            pendingResetTarget = target;
            pendingResetCount = count;
            pendingResetDescription = description;
            ImGui.OpenPopup(ResetPopupName);
        }

        private void DrawResetPopup()
        {
            var open = true;
            if (!ImGui.BeginPopupModal(ResetPopupName, ref open, ImGuiWindowFlags.AlwaysAutoResize))
                return;

            ImGui.TextWrapped($"Reset {pendingResetCount} {pendingResetDescription}?");
            ImGui.TextWrapped("This only changes local learned data and cannot be undone from inside the plugin.");

            if (ImGui.Button("Confirm Reset"))
            {
                var removed = ExecutePendingReset();
                Config.Save(checkModules: false);
                PrintEcho($"Reset {removed} learned database entr{(removed == 1 ? "y" : "ies")}.");
                pendingResetTarget = ResetTarget.None;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingResetTarget = ResetTarget.None;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        private int ExecutePendingReset()
        {
            switch (pendingResetTarget)
            {
                case ResetTarget.Filtered:
                    var filtered = CountFiltered(Config.LockDb.Entries) + CountFiltered(Config.CastTaxDb.Entries);
                    ResetFiltered(Config.LockDb.Entries);
                    ResetFiltered(Config.CastTaxDb.Entries);
                    return filtered;

                case ResetTarget.Locks:
                    var locks = Config.LockDb.Entries.Count;
                    Config.LockDb.Reset();
                    return locks;

                case ResetTarget.CastTax:
                    var castTax = Config.CastTaxDb.Entries.Count;
                    Config.CastTaxDb.Reset();
                    return castTax;

                default:
                    return 0;
            }
        }

        private static string FormatLastSeen(long unix)
        {
            if (unix <= 0)
                return "never";

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
            return age.TotalDays >= 1
                ? $"{age.TotalDays:0}d ago"
                : $"{age.TotalHours:0.0}h ago";
        }

        private static void ExportLearnedJson()
        {
            try
            {
                var directory = Path.Combine(DalamudApi.PluginInterface.ConfigDirectory.FullName, "exports");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"tsunippy-learned-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                var payload = new
                {
                    ExportedAt = DateTimeOffset.UtcNow,
                    Config.LockDb,
                    Config.CastTaxDb,
                };

                File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                PrintEcho($"Exported learned database to {path}");
            }
            catch (Exception exception)
            {
                DalamudApi.LogError("Failed exporting learned database.", exception);
                PrintError($"Learned database export failed: {exception.Message}");
            }
        }
    }
}
