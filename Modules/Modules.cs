using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;

namespace Tsunippy.Modules
{
    public static class Modules
    {
        private class ModuleInfo
        {
            public Module module = null;
            public bool isEnabled = true;
        }

        private static readonly Dictionary<Type, ModuleInfo> modules = new();
        private static IOrderedEnumerable<ModuleInfo> drawOrder;
        private static bool isInitialized;
        private static bool isDisposing;

        public static void Initialize()
        {
            if (isInitialized) return;

            foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsSubclassOf(typeof(Module)) && !t.IsAbstract))
            {
                var module = (Module)Activator.CreateInstance(t);
                if (module == null) continue;

                if (module.IsEnabled)
                {
                    try
                    {
                        module.Enable();
                        DalamudApi.LogInfo($"Loaded module: {module.GetType()}");
                    }
                    catch (Exception e)
                    {
                        DalamudApi.LogError($"Failed loading module: {module.GetType()}\n{e}");
                        try
                        {
                            module.Disable();
                        }
                        catch (Exception disableException)
                        {
                            DalamudApi.LogError($"Failed rolling back module: {module.GetType()}\n{disableException}");
                        }
                        module.IsEnabled = false;
                    }
                }

                modules.Add(t, new ModuleInfo { module = module, isEnabled = module.IsEnabled });
            }

            drawOrder = modules.Values.OrderBy(info => info.module.DrawOrder);
            isInitialized = true;
        }

        public static Module GetInstance(Type type) => modules.TryGetValue(type, out var instance) ? instance.module : null;

        public static T GetInstance<T>() where T : Module => GetInstance(typeof(T)) as T;

        public static void CheckModules()
        {
            if (!isInitialized || isDisposing) return;

            foreach (var (_, info) in modules)
            {
                var module = info.module;
                if (module.IsEnabled == info.isEnabled) continue;

                try
                {
                    if (module.IsEnabled)
                    {
                        module.Enable();
                        DalamudApi.LogInfo($"Enabled module: {module.GetType()}");
                    }
                    else
                    {
                        module.Disable();
                        DalamudApi.LogInfo($"Disabled module: {module.GetType()}");
                    }

                    info.isEnabled = module.IsEnabled;
                }
                catch (Exception e)
                {
                    DalamudApi.LogError($"Module state transition failed: {module.GetType()}\n{e}");
                    try
                    {
                        module.Disable();
                    }
                    catch (Exception disableException)
                    {
                        DalamudApi.LogError($"Module rollback failed: {module.GetType()}\n{disableException}");
                    }

                    module.IsEnabled = false;
                    info.isEnabled = false;
                }
            }
        }

        public static void Dispose()
        {
            if (!isInitialized) return;

            isDisposing = true;
            foreach (var (_, info) in modules.Where(kv => kv.Value.isEnabled))
            {
                try
                {
                    info.module.Disable();
                    info.isEnabled = false;
                }
                catch (Exception e)
                {
                    DalamudApi.LogError($"Failed disposing module: {info.module.GetType()}\n{e}");
                }
            }
        }

        public static void Draw()
        {
            var first = true;
            foreach (var info in drawOrder)
            {
                if (!first)
                    ImGui.Separator();
                info.module.DrawConfig();
                first = false;
            }
        }
    }
}
