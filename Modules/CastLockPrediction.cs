using Dalamud.Bindings.ImGui;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableCastLockPrediction = true;
        public float DefaultCasterTax = 0.1f;
        public bool LearnCastTax = true;
        public Database.CastTaxDatabase CastTaxDb = new();
    }
}

namespace Tsunippy.Modules
{
    public class CastLockPrediction : Module
    {
        public override bool IsEnabled
        {
            get => Config.EnableCastLockPrediction;
            set => Config.EnableCastLockPrediction = value;
        }

        public override int DrawOrder => 2;

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Cast Lock Prediction", ref Config.EnableCastLockPrediction))
                Config.Save();
            PluginUI.SetItemTooltip("Lets the authoritative timing controller pre-stage cast-complete lock prediction and resolve it through the same correction path as instant actions.");

            if (!Config.EnableCastLockPrediction)
                return;

            var tax = Config.DefaultCasterTax * 1000f;
            if (ImGui.SliderFloat("Caster Tax (ms)", ref tax, 50f, 200f, "%.0f"))
            {
                Config.DefaultCasterTax = tax / 1000f;
                Config.Save(checkModules: false);
            }
            PluginUI.SetItemTooltip("Fallback cast-tax value used when the learned cast database is not confident yet.");

            if (ImGui.Checkbox("Learn Cast Tax", ref Config.LearnCastTax))
                Config.Save(checkModules: false);
            PluginUI.SetItemTooltip("Learns cast-tax values per action and lets the timing controller use them for prediction confidence and correction.");

            if (ImGui.Button("Reset Learned Cast Tax"))
            {
                Config.CastTaxDb.Reset();
                Config.Save(checkModules: false);
            }
        }
    }
}
