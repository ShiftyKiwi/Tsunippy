using Dalamud.Configuration;
using System;
using Tsunippy.Database;
using Tsunippy.Runtime;

namespace Tsunippy
{
    public partial class Configuration : IPluginConfiguration
    {
        private const int CurrentConfigVersion = 2;

        public int Version { get; set; }
        public TsunippyProfile Profile { get; set; } = TsunippyProfile.Auto;
        public int ReplayLogCapacity { get; set; } = 512;

        public void Initialize()
        {
            var changed = false;

            try
            {
                // v1 -> v2 adds local-only profiles, replay capacity, and richer learned
                // database state. Preserve valid user values and only repair missing or
                // invalid fields produced by older serialized configs.
                if (!Enum.IsDefined(typeof(TsunippyProfile), Profile))
                {
                    Profile = TsunippyProfile.Auto;
                    changed = true;
                }

                var clampedReplayCapacity = Math.Clamp(ReplayLogCapacity, 64, 4096);
                if (ReplayLogCapacity != clampedReplayCapacity)
                {
                    ReplayLogCapacity = clampedReplayCapacity;
                    changed = true;
                }

                if (LockDb == null)
                {
                    LockDb = new LockDatabase();
                    changed = true;
                }

                if (LockDb.Entries == null)
                {
                    LockDb.Entries = new();
                    changed = true;
                }

                if (CastTaxDb == null)
                {
                    CastTaxDb = new CastTaxDatabase();
                    changed = true;
                }

                if (CastTaxDb.Entries == null)
                {
                    CastTaxDb.Entries = new();
                    changed = true;
                }

                if (Version < CurrentConfigVersion)
                {
                    Version = CurrentConfigVersion;
                    changed = true;
                }

                if (changed)
                    Save(checkModules: false);
            }
            catch (Exception exception)
            {
                DalamudApi.LogError("Failed migrating Tsunippy configuration.", exception);
                throw;
            }
        }

        public void Save(bool checkModules = true)
        {
            if (checkModules)
                Modules.Modules.CheckModules();
            DalamudApi.PluginInterface.SavePluginConfig(this);
        }
    }
}
