using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Tsunippy.Database;
using static Tsunippy.Tsunippy;

namespace Tsunippy
{
    public partial class Configuration
    {
        public bool EnableCastLockPrediction = true;
        public float DefaultCasterTax = 0.1f;
        public bool LearnCastTax = true;
        public CastTaxDatabase CastTaxDb = new();
    }
}

namespace Tsunippy.Modules
{
    /// <summary>
    /// Cast Lock Prediction Module
    ///
    /// Improvement over NoClippy: Pre-applies the expected caster tax animation lock
    /// at the moment a cast completes client-side, instead of waiting for the server
    /// response. This gives a ~RTT head start on the next action for casters.
    ///
    /// NoClippy's behavior: waits for ReceiveActionEffect, then does `newLock += oldLock`.
    /// Tsunippy's behavior: detects cast completion each frame, immediately applies
    /// `casterTax + dynamicFloor`, then corrects when the server responds.
    ///
    /// This is most impactful for caster jobs (BLM, SMN, RDM, SGE, etc.) where
    /// the ~100ms caster tax + RTT delay compounds into noticeable weaving difficulty.
    /// </summary>
    public class CastLockPrediction : Module
    {
        public override bool IsEnabled
        {
            get => Config.EnableCastLockPrediction;
            set => Config.EnableCastLockPrediction = value;
        }

        public override int DrawOrder => 2;

        // State
        private bool isCasting = false;
        private bool lockApplied = false;
        private ushort castSequence = 0;
        private uint castActionId = 0;
        private GameContext castContext = GameContext.PvE;

        // Diagnostics
        public float LastPredictedCastLock { get; private set; }
        public float LastActualCastLock { get; private set; }

        private void CastBegin(ulong objectID, nint packetData)
        {
            isCasting = true;
            lockApplied = false;
            unsafe
            {
                castSequence = Game.actionManager->currentSequence;
                castActionId = Game.actionManager->castActionId;
            }
            castContext = DalamudApi.ClientState.IsPvP ? GameContext.PvP : GameContext.PvE;
        }

        private void CastInterrupt(nint actionManager)
        {
            isCasting = false;
            lockApplied = false;
            castSequence = 0;
            castActionId = 0;
        }

        /// <summary>
        /// Each frame, check if the cast is completing and pre-apply the caster tax lock.
        /// </summary>
        private unsafe void Update()
        {
            if (!isCasting || lockApplied) return;

            var am = Game.actionManager;
            if (am->castActionId == 0) return;

            // Check if cast is about to complete (within one frame of finishing)
            var remaining = am->castTime - am->elapsedCastTime;
            if (remaining > 0.05f) return; // Not close enough yet

            // Pre-apply the caster tax lock
            var animLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
            var floor = animLockModule?.CurrentFloor ?? global::Tsunippy.RTT.DynamicFloor.DefaultFloor;
            var learnedTax = Config.LearnCastTax
                ? Config.CastTaxDb.GetTax(castActionId, castContext, Config.DefaultCasterTax)
                : Config.DefaultCasterTax;
            var predictedLock = learnedTax + floor;

            if (!animLockModule?.IsDryRunEnabled ?? true)
            {
                am->animationLock = predictedLock;
            }

            lockApplied = true;
            LastPredictedCastLock = predictedLock;

            DalamudApi.LogDebug($"Cast lock pre-applied: {F2MS(predictedLock)} ms (tax={F2MS(Config.DefaultCasterTax)}, floor={F2MS(floor)})");
        }

        /// <summary>
        /// When the server responds for a cast action, correct the pre-applied lock.
        /// </summary>
        private unsafe void ReceiveActionEffect(uint casterEntityId, Character* casterPtr,
            Vector3* targetPos, ActionEffectHandler.Header* header,
            ActionEffectHandler.TargetEffects* effects,
            GameObjectId* targetEntityIds, float oldLock, float newLock)
        {
            if (!lockApplied) return;
            if (oldLock == newLock || (nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address) return;
            if (castSequence != 0 && header->SourceSequence != castSequence) return;

            // This is the server's response for our cast action
            lockApplied = false;
            isCasting = false;
            castSequence = 0;
            LastActualCastLock = newLock;

            var animLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
            if (Config.LearnCastTax && !(animLockModule?.ConflictDetected ?? false))
            {
                var actionId = castActionId != 0 ? castActionId : header->SpellId;
                if (Config.CastTaxDb.RecordTax(actionId, castContext, newLock))
                    animLockModule?.NotifyLearnedDataChanged();
            }

            castActionId = 0;
            if (animLockModule?.IsDryRunEnabled ?? true) return;

            // The server's lock replaces ours. oldLock is what remains of our prediction.
            // If our prediction was accurate, oldLock should be close to our predicted lock
            // minus the RTT that elapsed. We just use the server's value plus any remaining
            // lock from our prediction window.
            var remainingPredictedLock = Math.Min(Math.Max(oldLock, 0), LastPredictedCastLock);
            var adjustedLock = newLock + remainingPredictedLock;
            if (float.IsFinite(adjustedLock) && adjustedLock < 10)
            {
                Game.actionManager->animationLock = adjustedLock;
            }

            if (Config.EnableLogging)
                PrintLog($"Cast Lock Corrected: predicted={F2MS(LastPredictedCastLock)} ms, server={F2MS(newLock)} ms, final={F2MS(adjustedLock)} ms");
        }

        public override unsafe void Enable()
        {
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
            Game.OnUpdate += Update;
            Game.OnReceiveActionEffect += ReceiveActionEffect;
        }

        public override unsafe void Disable()
        {
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
            Game.OnUpdate -= Update;
            Game.OnReceiveActionEffect -= ReceiveActionEffect;
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Cast Lock Prediction", ref Config.EnableCastLockPrediction))
                Config.Save();
            PluginUI.SetItemTooltip("Pre-applies the expected caster tax at cast completion" +
                "\ninstead of waiting for the server response." +
                "\nGives casters a ~RTT head start on the next action." +
                "\n\nMost impactful for BLM, SMN, RDM, SGE, and other casting jobs.");

            if (Config.EnableCastLockPrediction)
            {
                var tax = Config.DefaultCasterTax * 1000f;
                if (ImGui.SliderFloat("Caster Tax (ms)", ref tax, 50f, 200f, "%.0f"))
                {
                    Config.DefaultCasterTax = tax / 1000f;
                    Config.Save(checkModules: false);
                }
                PluginUI.SetItemTooltip("The expected caster tax duration in milliseconds.\nDefault: 100ms (standard FFXIV caster tax).");

                if (ImGui.Checkbox("Learn Cast Tax", ref Config.LearnCastTax))
                    Config.Save(checkModules: false);
                PluginUI.SetItemTooltip("Learns cast-tax values per action instead of relying only on the global default.");

                if (ImGui.Button("Reset Learned Cast Tax"))
                {
                    Config.CastTaxDb.Reset();
                    Config.Save(checkModules: false);
                }
            }
        }
    }
}
