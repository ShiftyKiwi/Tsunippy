using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;

namespace Tsunippy.Modules
{
    public static class Modules
    {
        public readonly record struct ModuleStatus(string Name, bool IsEnabled, int FailureCount, string LastFailure);

        private class ModuleInfo
        {
            public Module module = null;
            public bool isEnabled = true;
            public int failureCount;
            public string lastFailure = string.Empty;
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
                var info = new ModuleInfo { module = module, isEnabled = module.IsEnabled };

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
                        UpdateFailureState(info, e, "startup");
                        try
                        {
                            module.Disable();
                        }
                        catch (Exception disableException)
                        {
                            DalamudApi.LogError($"Failed rolling back module: {module.GetType()}\n{disableException}");
                        }
                        module.IsEnabled = false;
                        info.isEnabled = false;
                    }
                }

                modules.Add(t, info);
            }

            drawOrder = modules.Values.OrderBy(info => info.module.DrawOrder);
            isInitialized = true;
        }

        public static Module GetInstance(Type type) => modules.TryGetValue(type, out var instance) ? instance.module : null;

        public static T GetInstance<T>() where T : Module => GetInstance(typeof(T)) as T;

        public static IReadOnlyList<ModuleStatus> GetStatusSnapshot()
            => modules.Values
                .Select(info => new ModuleStatus(
                    info.module.GetType().Name,
                    info.isEnabled,
                    info.failureCount,
                    info.lastFailure))
                .ToArray();

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
                    UpdateFailureState(info, e, "state transition");
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

        public static void HandleRuntimeFailure(object target, string source, Exception exception)
        {
            if (target is not Module failedModule)
                return;

            var info = modules.Values.FirstOrDefault(moduleInfo => ReferenceEquals(moduleInfo.module, failedModule));
            if (info?.module == null)
                return;

            UpdateFailureState(info, exception, source);

            if (!info.isEnabled)
                return;

            try
            {
                info.module.Disable();
            }
            catch (Exception disableException)
            {
                DalamudApi.LogError($"Module rollback failed after runtime error: {info.module.GetType()}\n{disableException}");
            }

            info.module.IsEnabled = false;
            info.isEnabled = false;

            DalamudApi.ShowNotification(
                $"{info.module.GetType().Name} was disabled after a runtime failure in {source}.",
                Dalamud.Interface.ImGuiNotification.NotificationType.Warning);

            try
            {
                Tsunippy.Config?.Save(checkModules: false);
            }
            catch (Exception saveException)
            {
                DalamudApi.LogError($"Failed persisting disabled module state for {info.module.GetType()}\n{saveException}");
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

            modules.Clear();
            drawOrder = null;
            isInitialized = false;
            isDisposing = false;
        }

        public static void Draw()
        {
            if (!isInitialized || drawOrder == null)
                return;

            var first = true;
            foreach (var info in drawOrder)
            {
                if (!first)
                    ImGui.Separator();
                info.module.DrawConfig();
                first = false;
            }
        }

        private static void UpdateFailureState(ModuleInfo info, Exception exception, string source)
        {
            info.failureCount++;
            info.lastFailure = $"{source}: {exception.GetType().Name}: {exception.Message}";
        }
    }
}
