using System;
using Dalamud.Game.Text;
using Dalamud.Bindings.ImGui;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool LogToChat = false;
        public XivChatType LogChatType = XivChatType.None;
    }
}

namespace Tsunippy.Modules
{
    /// <summary>
    /// Logging configuration module.
    /// Controls where log output is sent (chat window vs Dalamud log).
    /// </summary>
    public class Logging : Module
    {
        public override int DrawOrder => 8;

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Output to Chat Log", ref Tsunippy.Config.LogToChat))
                Tsunippy.Config.Save(checkModules: false);
            PluginUI.SetItemTooltip("Sends logging to the chat log instead of the Dalamud log.");

            if (!Tsunippy.Config.LogToChat) return;

            if (ImGui.BeginCombo("Log Chat Type", Tsunippy.Config.LogChatType.ToString()))
            {
                foreach (var chatType in Enum.GetValues<XivChatType>())
                {
                    if (!ImGui.Selectable(chatType.ToString())) continue;

                    Tsunippy.Config.LogChatType = chatType;
                    Tsunippy.Config.Save(checkModules: false);
                }

                ImGui.EndCombo();
            }

            PluginUI.SetItemTooltip("Overrides the default Dalamud chat channel.");
        }
    }
}
