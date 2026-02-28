using Dalamud.Configuration;

namespace Tsunippy
{
    public partial class Configuration : IPluginConfiguration
    {
        public int Version { get; set; }

        public void Initialize() { }

        public void Save(bool checkModules = true)
        {
            if (checkModules)
                Modules.Modules.CheckModules();
            DalamudApi.PluginInterface.SavePluginConfig(this);
        }
    }
}
