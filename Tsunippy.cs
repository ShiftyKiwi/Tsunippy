using System;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Tsunippy.Runtime;

namespace Tsunippy
{
    public class Tsunippy : IDalamudPlugin
    {
        public static Tsunippy Plugin { get; private set; }
        public static Configuration Config { get; private set; }

        private bool frameworkSubscribed;
        private bool uiDrawSubscribed;
        private bool configUiSubscribed;
        private bool mainUiSubscribed;
        private bool modulesInitialized;
        private bool gameInitialized;
        private bool dalamudInitialized;

        public Tsunippy(IDalamudPluginInterface pluginInterface)
        {
            Plugin = this;
            DalamudApi.Initialize(this, pluginInterface);
            dalamudInitialized = true;

            Config = (Configuration)DalamudApi.PluginInterface.GetPluginConfig() ?? new();
            Config.Initialize();

            try
            {
                Game.Initialize();
                gameInitialized = true;

                DalamudApi.Framework.Update += Update;
                frameworkSubscribed = true;
                DalamudApi.PluginInterface.UiBuilder.Draw += PluginUI.Draw;
                uiDrawSubscribed = true;
                DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += ConfigUI.ToggleVisible;
                configUiSubscribed = true;
                DalamudApi.PluginInterface.UiBuilder.OpenMainUi += ConfigUI.ToggleVisible;
                mainUiSubscribed = true;

                Modules.Modules.Initialize();
                modulesInitialized = true;
            }
            catch (Exception e)
            {
                PrintError("Failed to load!");
                DalamudApi.LogError(e.ToString());
                DalamudApi.ShowNotification("Tsunippy failed to initialize and rolled back its startup state.", Dalamud.Interface.ImGuiNotification.NotificationType.Error);
                RollbackStartup();
            }
        }

        [Command("/tsunippy")]
        [HelpMessage("/tsunippy [on|off|toggle|dry|diag|stats|profile|export|reset|relearn|db|help] - Toggles the config window if no option is specified.")]
        private void OnTsunippy(string command, string argument)
        {
            var args = argument.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var primary = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

            switch (primary)
            {
                case "on":
                case "toggle" when !Config.EnableAnimLockComp:
                case "t" when !Config.EnableAnimLockComp:
                    Config.EnableAnimLockComp = true;
                    Config.Save();
                    PrintEcho("Enabled animation lock compensation!");
                    break;

                case "off":
                case "toggle" when Config.EnableAnimLockComp:
                case "t" when Config.EnableAnimLockComp:
                    Config.EnableAnimLockComp = false;
                    Config.Save();
                    PrintEcho("Disabled animation lock compensation!");
                    break;

                case "dry":
                case "d":
                    PrintEcho($"Dry run is now {((Config.EnableDryRun = !Config.EnableDryRun) ? "enabled" : "disabled")}.");
                    Config.Save(checkModules: false);
                    break;

                case "diag":
                    Config.EnableDiagnostics = !Config.EnableDiagnostics;
                    Config.DiagnosticsOverlay = Config.EnableDiagnostics;
                    Config.Save();
                    PrintEcho($"Diagnostics overlay is now {(Config.EnableDiagnostics ? "enabled" : "disabled")}.");
                    break;

                case "stats":
                    Config.EnableEncounterStats = !Config.EnableEncounterStats;
                    Config.Save();
                    PrintEcho($"Encounter stats are now {(Config.EnableEncounterStats ? "enabled" : "disabled")}.");
                    break;

                case "profile":
                    HandleProfileCommand(args);
                    break;

                case "export":
                    HandleExportCommand(args);
                    break;

                case "reset":
                    HandleResetCommand(args);
                    break;

                case "relearn":
                    global::Tsunippy.Modules.Modules.GetInstance<global::Tsunippy.Modules.AnimationLock>()?.Relearn();
                    Config.Save(checkModules: false);
                    PrintEcho("Reset learned lock and cast-tax databases; timing model epoch advanced.");
                    break;

                case "db":
                    Config.EnableDatabaseBrowser = true;
                    ConfigUI.isVisible = true;
                    Config.Save(checkModules: false);
                    PrintEcho("Opened the learned database browser.");
                    break;

                case "help":
                case "?":
                    PrintHelp();
                    break;

                case "":
                    ConfigUI.ToggleVisible();
                    break;

                default:
                    PrintHelp();
                    break;
            }
        }

        private static void HandleProfileCommand(string[] args)
        {
            if (args.Length < 2)
            {
                PrintEcho($"Current profile: {Config.Profile}. Use /tsunippy profile safe|balanced|aggressive|auto.");
                return;
            }

            if (!Enum.TryParse<TsunippyProfile>(args[1], true, out var profile))
            {
                PrintEcho("Unknown profile. Use safe, balanced, aggressive, or auto.");
                return;
            }

            Config.Profile = profile;
            Config.Save(checkModules: false);
            PrintEcho($"Profile set to {profile}.");
        }

        private static void HandleExportCommand(string[] args)
        {
            var format = args.Length >= 2 ? args[1].ToLowerInvariant() : "json";
            if (format is not ("json" or "csv"))
            {
                PrintEcho("Usage: /tsunippy export [json|csv]");
                return;
            }

            var animLock = global::Tsunippy.Modules.Modules.GetInstance<global::Tsunippy.Modules.AnimationLock>();
            if (animLock == null)
            {
                PrintEcho("Animation lock module is not available; nothing was exported.");
                return;
            }

            try
            {
                var path = animLock.ExportReplay(format);
                PrintEcho($"Exported recent decisions to {path}");
            }
            catch (Exception exception)
            {
                DalamudApi.LogError("Failed exporting Tsunippy replay log.", exception);
                PrintError($"Export failed: {exception.Message}");
            }
        }

        private static void HandleResetCommand(string[] args)
        {
            if (args.Length < 2)
            {
                PrintEcho("Usage: /tsunippy reset floor|rtt");
                return;
            }

            var animLock = global::Tsunippy.Modules.Modules.GetInstance<global::Tsunippy.Modules.AnimationLock>();
            if (animLock == null)
            {
                PrintEcho("Animation lock module is not available.");
                return;
            }

            switch (args[1].ToLowerInvariant())
            {
                case "floor":
                    animLock.ResetFloor();
                    PrintEcho("Reset dynamic floor and advanced the timing epoch.");
                    break;
                case "rtt":
                    animLock.ResetRttModel();
                    PrintEcho("Reset RTT estimators and advanced the timing epoch.");
                    break;
                default:
                    PrintEcho("Usage: /tsunippy reset floor|rtt");
                    break;
            }
        }

        private static void PrintHelp()
        {
            PrintEcho("Commands:" +
                "\n  /tsunippy - Open the configuration window." +
                "\n  /tsunippy on|off|toggle - Enable or disable compensation." +
                "\n  /tsunippy dry - Toggle dry run." +
                "\n  /tsunippy diag - Toggle diagnostics overlay." +
                "\n  /tsunippy stats - Toggle encounter stats." +
                "\n  /tsunippy profile safe|balanced|aggressive|auto - Set local tuning profile." +
                "\n  /tsunippy export [json|csv] - Export recent local decision log." +
                "\n  /tsunippy reset floor|rtt - Reset local timing state." +
                "\n  /tsunippy relearn - Reset learned lock and cast-tax data." +
                "\n  /tsunippy db - Open learned database browser.");
        }

        public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[Tsunippy] {message}");
        public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[Tsunippy] {message}");

        public static void PrintLog(string message)
        {
            if (Config.LogToChat)
            {
                if (Config.LogChatType != XivChatType.None)
                {
                    DalamudApi.ChatGui.Print(new XivChatEntry
                    {
                        Message = $"[Tsunippy] {message}",
                        Type = Config.LogChatType
                    });
                }
                else
                {
                    PrintEcho(message);
                }
            }
            else
            {
                DalamudApi.LogInfo(message);
            }
        }

        /// <summary>Convert float seconds to integer milliseconds for display.</summary>
        public static int F2MS(float f) => (int)Math.Round(f * 1000);

        private static void Update(IFramework framework) => Game.Update();

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            Config.Save(checkModules: false);
            RollbackStartup();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void RollbackStartup()
        {
            if (frameworkSubscribed)
            {
                DalamudApi.Framework.Update -= Update;
                frameworkSubscribed = false;
            }

            if (uiDrawSubscribed)
            {
                DalamudApi.PluginInterface.UiBuilder.Draw -= PluginUI.Draw;
                uiDrawSubscribed = false;
            }

            if (configUiSubscribed)
            {
                DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= ConfigUI.ToggleVisible;
                configUiSubscribed = false;
            }

            if (mainUiSubscribed)
            {
                DalamudApi.PluginInterface.UiBuilder.OpenMainUi -= ConfigUI.ToggleVisible;
                mainUiSubscribed = false;
            }

            if (modulesInitialized)
            {
                Modules.Modules.Dispose();
                modulesInitialized = false;
            }

            if (gameInitialized)
            {
                Game.Dispose();
                gameInitialized = false;
            }

            if (dalamudInitialized)
            {
                DalamudApi.Dispose();
                dalamudInitialized = false;
            }
        }
    }
}
