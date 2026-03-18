using System;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Tsunippy
{
    public class Tsunippy : IDalamudPlugin
    {
        public static Tsunippy Plugin { get; private set; }
        public static Configuration Config { get; private set; }

        private bool frameworkSubscribed;
        private bool uiDrawSubscribed;
        private bool configUiSubscribed;
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
        [HelpMessage("/tsunippy [on|off|toggle|dry|diag|help] - Toggles the config window if no option is specified.")]
        private void OnTsunippy(string command, string argument)
        {
            switch (argument)
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

                case "":
                    ConfigUI.ToggleVisible();
                    break;

                default:
                    PrintEcho("Usage: /tsunippy <option>" +
                        "\n  on / off / toggle - Enable or disable animation lock compensation." +
                        "\n  dry - Toggle dry run (calculations only, no lock overrides)." +
                        "\n  diag - Toggle the real-time diagnostics overlay." +
                        "\n  (no args) - Open the configuration window.");
                    break;
            }
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
