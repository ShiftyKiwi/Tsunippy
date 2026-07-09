using System;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Tsunippy
{
    public static unsafe class Game
    {
        public static Structures.ActionManager* actionManager;
        public const int ExpectedHookCount = 6;

        public const float DefaultClientAnimationLock = 0.5f;
        public static bool IsInitialized { get; private set; }
        public static int EnabledHookCount { get; private set; }
        public static int RuntimeFailureCount { get; private set; }
        public static string LastInitializationError { get; private set; } = string.Empty;
        public static string LastRuntimeFailure { get; private set; } = string.Empty;

        // ==================== Hook 1: UseAction ====================
        public delegate void UseActionEventDelegate(ActionManager* actionManager, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted, bool ret);
        public static event UseActionEventDelegate OnUseAction;

        private static Hook<ActionManager.Delegates.UseAction> UseActionHook;

        private static bool UseActionDetour(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
        {
            var ret = UseActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
            DispatchUseAction(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted, ret);
            return ret;
        }

        // ==================== Hook 2: UseActionLocation ====================
        public delegate void UseActionLocationEventDelegate(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret);
        public static event UseActionLocationEventDelegate OnUseActionLocation;

        private delegate byte UseActionLocationDelegate(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte c);
        private static Hook<UseActionLocationDelegate> UseActionLocationHook;

        private static byte UseActionLocationDetour(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte c)
        {
            var ret = UseActionLocationHook.Original(actionManager, actionType, actionID, targetedActorID, vectorLocation, param, c);
            DispatchUseActionLocation(actionManager, actionType, actionID, targetedActorID, vectorLocation, param, ret);
            return ret;
        }

        // ==================== Hook 3: CastBegin ====================
        private static bool invokeCastInterrupt = false;
        private static long castInterruptExpiryTick;

        public delegate void CastBeginEventDelegate(uint casterEntityId, nint packetData);
        public static event CastBeginEventDelegate OnCastBegin;

        private delegate void CastBeginDelegate(uint casterEntityId, ActorCastPacket* packetData);
        private static Hook<CastBeginDelegate> CastBeginHook;

        private static void CastBeginDetour(uint casterEntityId, ActorCastPacket* packetData)
        {
            CastBeginHook.Original(casterEntityId, packetData);
            if (casterEntityId != DalamudApi.ObjectTable.LocalPlayer?.EntityId) return;
            DispatchCastBegin(casterEntityId, (nint)packetData);
            invokeCastInterrupt = true;
            castInterruptExpiryTick = Environment.TickCount64 + 2000;
        }

        // ==================== Hook 4: CastInterrupt ====================
        // Seems to always be called twice, hence the invokeCastInterrupt guard
        public delegate void CastInterruptDelegate(nint actionManager);
        public static event CastInterruptDelegate OnCastInterrupt;

        private static Hook<CastInterruptDelegate> CastInterruptHook;

        private static void CastInterruptDetour(nint actionManager)
        {
            CastInterruptHook.Original(actionManager);
            if (!invokeCastInterrupt || Environment.TickCount64 > castInterruptExpiryTick)
            {
                invokeCastInterrupt = false;
                return;
            }

            DispatchCastInterrupt(actionManager);
            invokeCastInterrupt = false;
        }

        // ==================== Hook 5: ReceiveActionEffect ====================
        public delegate void ReceiveActionEffectEventDelegate(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds, float oldLock, float newLock);
        public static event ReceiveActionEffectEventDelegate OnReceiveActionEffect;

        private static Hook<ActionEffectHandler.Delegates.Receive> ReceiveActionEffectHook;

        private static void ReceiveActionEffectDetour(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
        {
            var oldLock = actionManager->animationLock;
            ReceiveActionEffectHook.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
            DispatchReceiveActionEffect(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds, oldLock, actionManager->animationLock);
        }

        // ==================== Hook 6: SendPacket ====================
        // Enhanced: passes packet pointer so PacketTracker can classify
        public delegate void NetworkMessageEventDelegate(nint packet);
        public static event NetworkMessageEventDelegate OnNetworkMessageDelegate;

        private static Hook<ZoneClient.Delegates.SendPacket> SendPacketHook;

        private static bool SendPacketDetour(ZoneClient* zoneClient, nint packet, uint a3, uint a4, bool a5)
        {
            DispatchNetworkMessage(packet);
            return SendPacketHook.Original(zoneClient, packet, a3, a4, a5);
        }

        // ==================== Initialization ====================
        public static void Initialize()
        {
            if (IsInitialized)
                return;

            LastInitializationError = string.Empty;
            EnabledHookCount = 0;
            RuntimeFailureCount = 0;
            LastRuntimeFailure = string.Empty;

            try
            {
                actionManager = (Structures.ActionManager*)ActionManager.Instance();
                if (actionManager == null)
                    throw new InvalidOperationException("ActionManager.Instance() returned null.");

                ValidateActionManagerState();

                UseActionHook = DalamudApi.GameInteropProvider.HookFromAddress<ActionManager.Delegates.UseAction>(
                    (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);

                UseActionLocationHook = DalamudApi.GameInteropProvider.HookFromAddress<UseActionLocationDelegate>(
                    (nint)ActionManager.MemberFunctionPointers.UseActionLocation, UseActionLocationDetour);

                CastBeginHook = DalamudApi.GameInteropProvider.HookFromAddress<CastBeginDelegate>(
                    DalamudApi.SigScanner.ScanText("40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B D1"), CastBeginDetour);

                CastInterruptHook = DalamudApi.GameInteropProvider.HookFromAddress<CastInterruptDelegate>(
                    DalamudApi.SigScanner.ScanText("48 8B C4 48 83 EC 48 48 89 58 08"), CastInterruptDetour);

                ReceiveActionEffectHook = DalamudApi.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                    ActionEffectHandler.Addresses.Receive.Value, ReceiveActionEffectDetour);

                SendPacketHook = DalamudApi.GameInteropProvider.HookFromAddress<ZoneClient.Delegates.SendPacket>(
                    (nint)ZoneClient.MemberFunctionPointers.SendPacket, SendPacketDetour);

                EnableHook(UseActionHook);
                EnableHook(UseActionLocationHook);
                EnableHook(CastBeginHook);
                EnableHook(CastInterruptHook);
                EnableHook(ReceiveActionEffectHook);
                EnableHook(SendPacketHook);

                IsInitialized = true;
            }
            catch (Exception exception)
            {
                LastInitializationError = $"{exception.GetType().Name}: {exception.Message}";
                DalamudApi.LogError("Failed initializing Tsunippy game hooks.", exception);
                Dispose();
                throw;
            }
        }

        // ==================== Framework Update ====================
        public static event Action OnUpdate;
        public static void Update() => OnUpdate?.Invoke();

        // ==================== Disposal ====================
        public static void Dispose()
        {
            UseActionHook?.Dispose();
            UseActionHook = null;
            OnUseAction = null;

            UseActionLocationHook?.Dispose();
            UseActionLocationHook = null;
            OnUseActionLocation = null;

            CastBeginHook?.Dispose();
            CastBeginHook = null;
            OnCastBegin = null;

            CastInterruptHook?.Dispose();
            CastInterruptHook = null;
            OnCastInterrupt = null;

            ReceiveActionEffectHook?.Dispose();
            ReceiveActionEffectHook = null;
            OnReceiveActionEffect = null;

            SendPacketHook?.Dispose();
            SendPacketHook = null;
            OnNetworkMessageDelegate = null;

            OnUpdate = null;
            invokeCastInterrupt = false;
            castInterruptExpiryTick = 0;
            actionManager = null;
            IsInitialized = false;
            EnabledHookCount = 0;
        }

        private static void DispatchUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted, bool ret)
        {
            var handlers = OnUseAction;
            if (handlers == null)
                return;

            foreach (UseActionEventDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted, ret);
                }
                catch (Exception exception)
                {
                    OnUseAction -= handler;
                    RecordRuntimeFailure(nameof(OnUseAction), handler, exception);
                }
            }
        }

        private static void DispatchUseActionLocation(nint actionManager, uint actionType, uint actionID, ulong targetedActorID, nint vectorLocation, uint param, byte ret)
        {
            var handlers = OnUseActionLocation;
            if (handlers == null)
                return;

            foreach (UseActionLocationEventDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(actionManager, actionType, actionID, targetedActorID, vectorLocation, param, ret);
                }
                catch (Exception exception)
                {
                    OnUseActionLocation -= handler;
                    RecordRuntimeFailure(nameof(OnUseActionLocation), handler, exception);
                }
            }
        }

        private static void DispatchCastBegin(uint casterEntityId, nint packetData)
        {
            var handlers = OnCastBegin;
            if (handlers == null)
                return;

            foreach (CastBeginEventDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(casterEntityId, packetData);
                }
                catch (Exception exception)
                {
                    OnCastBegin -= handler;
                    RecordRuntimeFailure(nameof(OnCastBegin), handler, exception);
                }
            }
        }

        private static void DispatchCastInterrupt(nint actionManager)
        {
            var handlers = OnCastInterrupt;
            if (handlers == null)
                return;

            foreach (CastInterruptDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(actionManager);
                }
                catch (Exception exception)
                {
                    OnCastInterrupt -= handler;
                    RecordRuntimeFailure(nameof(OnCastInterrupt), handler, exception);
                }
            }
        }

        private static void DispatchReceiveActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds, float oldLock, float newLock)
        {
            var handlers = OnReceiveActionEffect;
            if (handlers == null)
                return;

            foreach (ReceiveActionEffectEventDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds, oldLock, newLock);
                }
                catch (Exception exception)
                {
                    OnReceiveActionEffect -= handler;
                    RecordRuntimeFailure(nameof(OnReceiveActionEffect), handler, exception);
                }
            }
        }

        private static void DispatchNetworkMessage(nint packet)
        {
            var handlers = OnNetworkMessageDelegate;
            if (handlers == null)
                return;

            foreach (NetworkMessageEventDelegate handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(packet);
                }
                catch (Exception exception)
                {
                    OnNetworkMessageDelegate -= handler;
                    RecordRuntimeFailure(nameof(OnNetworkMessageDelegate), handler, exception);
                }
            }
        }

        private static void EnableHook<T>(Hook<T> hook) where T : Delegate
        {
            hook.Enable();
            EnabledHookCount++;
        }

        private static void RecordRuntimeFailure(string source, Delegate handler, Exception exception)
        {
            RuntimeFailureCount++;
            LastRuntimeFailure = $"{source}: {handler.Method.DeclaringType?.Name}.{handler.Method.Name}: {exception.GetType().Name}: {exception.Message}";
            DalamudApi.LogError($"Runtime failure in {source} subscriber {handler.Method.DeclaringType?.FullName}.{handler.Method.Name}", exception);
            Modules.Modules.HandleRuntimeFailure(handler.Target, source, exception);
        }

        private static void ValidateActionManagerState()
        {
            if (!float.IsFinite(actionManager->animationLock) || actionManager->animationLock < 0 || actionManager->animationLock > 10)
                throw new InvalidOperationException("ActionManager animationLock was outside the expected range during initialization.");
        }
    }
}
