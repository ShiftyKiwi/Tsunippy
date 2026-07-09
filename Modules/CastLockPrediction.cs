using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Tsunippy.Database;
using Tsunippy.Runtime;
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
        private const float LockEqualityEpsilon = 0.0005f;

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
        private PendingPrediction pendingPrediction;
        private float recentFrameDelta = 1f / 60f;
        private int expiredPredictionCount;
        private string lastPredictionReason = "none";
        private ushort lastOwnedSequence;
        private uint lastOwnedActionId;
        private long lastOwnedResponseUntilTick;
        private const int ResponseHandoffMilliseconds = 500;

        // Diagnostics
        public float LastPredictedCastLock { get; private set; }
        public float LastActualCastLock { get; private set; }
        public int PendingPredictionCount => pendingPrediction == null ? 0 : 1;
        public int ExpiredPredictionCount => expiredPredictionCount;
        public string LastPredictionReason => lastPredictionReason;

        private void CastBegin(uint casterEntityId, nint packetData)
        {
            if (casterEntityId != DalamudApi.ObjectTable.LocalPlayer?.EntityId)
                return;

            isCasting = true;
            lockApplied = false;
            pendingPrediction = null;
            unsafe
            {
                castSequence = Game.actionManager->currentSequence;
                castActionId = Game.actionManager->castActionId;
            }
            castContext = DalamudApi.ClientState.IsPvP ? GameContext.PvP : GameContext.PvE;
        }

        public bool ShouldOwnCastResponse(ushort sequence, uint actionId)
        {
            if (Environment.TickCount64 <= lastOwnedResponseUntilTick
                && ResponseMatches(lastOwnedSequence, lastOwnedActionId, sequence, actionId))
                return true;

            if (!IsEnabled || (!isCasting && !lockApplied && pendingPrediction == null))
                return false;

            return ResponseMatches(castSequence, castActionId, sequence, actionId);
        }

        private void CastInterrupt(nint actionManager)
        {
            lastPredictionReason = "interrupted";
            ResetRuntimeState();
        }

        /// <summary>
        /// Each frame, check if the cast is completing and pre-apply the caster tax lock.
        /// </summary>
        private unsafe void Update()
        {
            recentFrameDelta = Math.Clamp((float)DalamudApi.Framework.UpdateDelta.TotalSeconds, 0.001f, 0.050f);

            if (pendingPrediction != null && pendingPrediction.ExpiresTick < Environment.TickCount64)
            {
                expiredPredictionCount++;
                lastPredictionReason = "expired";
                ResetRuntimeState();
                return;
            }

            if (!isCasting || lockApplied) return;

            if (DalamudApi.Condition[ConditionFlag.BetweenAreas] || DalamudApi.ObjectTable.LocalPlayer == null)
            {
                lastPredictionReason = "state reset";
                ResetRuntimeState();
                return;
            }

            var am = Game.actionManager;
            if (am->castActionId == 0)
            {
                lastPredictionReason = "cast no longer active";
                ResetRuntimeState();
                return;
            }

            if (castActionId != 0 && am->castActionId != castActionId)
            {
                lastPredictionReason = "cast action changed";
                ResetRuntimeState();
                return;
            }

            // Treat "about to complete" as roughly two recent frames, with a 20ms
            // minimum for low-FPS jitter and a tight clamp so high-FPS clients do
            // not accidentally use the old 50ms pseudo-frame window.
            var remaining = am->castTime - am->elapsedCastTime;
            var completionWindow = Math.Clamp(MathF.Max(recentFrameDelta * 2f, 0.020f), 0.020f, 0.050f);
            if (remaining > completionWindow || remaining < -recentFrameDelta)
                return;

            // Pre-apply the caster tax lock
            var animLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();
            var floor = animLockModule?.CurrentFloor ?? global::Tsunippy.RTT.DynamicFloor.DefaultFloor;
            var learnedTax = Config.LearnCastTax
                ? Config.CastTaxDb.GetTax(castActionId, castContext, Config.DefaultCasterTax)
                : Config.DefaultCasterTax;
            var predictedLock = learnedTax + floor;
            var existingLock = am->animationLock;
            var epoch = animLockModule?.CurrentEpoch ?? 0;

            if (!animLockModule?.IsDryRunEnabled ?? true)
            {
                am->animationLock = MathF.Max(existingLock, predictedLock);
            }

            lockApplied = true;
            LastPredictedCastLock = predictedLock;
            pendingPrediction = new PendingPrediction
            {
                Sequence = castSequence,
                ActionId = castActionId,
                IsPvP = castContext == GameContext.PvP,
                BaseLock = learnedTax,
                PredictedLock = predictedLock,
                OriginalLockAtPrediction = existingLock,
                CreatedTick = Environment.TickCount64,
                ExpiresTick = Environment.TickCount64 + 1_250,
                ModelEpoch = epoch,
                Source = "cast",
            };
            lastPredictionReason = "pending";

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
            if (!isCasting && !lockApplied && pendingPrediction == null) return;
            if ((nint)casterPtr != DalamudApi.ObjectTable.LocalPlayer?.Address) return;
            if (NearlyEqual(oldLock, newLock))
            {
                MarkOwnedResponse(header->SourceSequence, actionId: castActionId != 0 ? castActionId : header->SpellId);
                lastPredictionReason = "skipped no lock delta";
                ResetRuntimeState();
                return;
            }

            var actionId = castActionId != 0 ? castActionId : header->SpellId;
            var animLockModule = global::Tsunippy.Modules.Modules.GetInstance<AnimationLock>();

            if (pendingPrediction == null && lockApplied)
            {
                MarkOwnedResponse(header->SourceSequence, actionId);
                lastPredictionReason = "missing pending cast";
                ResetRuntimeState();
                return;
            }

            if (pendingPrediction != null && pendingPrediction.ModelEpoch != (animLockModule?.CurrentEpoch ?? 0))
            {
                MarkOwnedResponse(header->SourceSequence, actionId);
                lastPredictionReason = "stale epoch";
                ResetRuntimeState();
                return;
            }

            if (pendingPrediction != null && pendingPrediction.ExpiresTick < Environment.TickCount64)
            {
                MarkOwnedResponse(header->SourceSequence, actionId);
                expiredPredictionCount++;
                lastPredictionReason = "expired";
                ResetRuntimeState();
                return;
            }

            if (!ResponseMatches(castSequence, actionId, header->SourceSequence, header->SpellId))
            {
                lastPredictionReason = "sequence mismatch";
                ResetRuntimeState();
                return;
            }

            if (pendingPrediction != null && pendingPrediction.ActionId != 0 && actionId != pendingPrediction.ActionId)
            {
                lastPredictionReason = "action mismatch";
                ResetRuntimeState();
                return;
            }

            // This is the server's response for our cast action
            MarkOwnedResponse(header->SourceSequence, actionId);
            LastActualCastLock = newLock;

            if (Config.LearnCastTax && !(animLockModule?.ConflictDetected ?? false))
            {
                if (Config.CastTaxDb.RecordTax(actionId, castContext, newLock))
                    animLockModule?.NotifyLearnedDataChanged();
            }

            var hadPreAppliedLock = lockApplied;
            isCasting = false;
            lockApplied = false;
            castSequence = 0;
            castActionId = 0;
            pendingPrediction = null;
            lastPredictionReason = "accepted";
            if (!hadPreAppliedLock)
            {
                if (Config.EnableLogging)
                    PrintLog($"Cast Lock Learned: action={actionId}, server={F2MS(newLock)} ms, no pre-apply");
                lastPredictionReason = "accepted without pre-apply";
                return;
            }

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
            ResetRuntimeState();
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
            ResetRuntimeState();
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

        private void ResetRuntimeState()
        {
            isCasting = false;
            lockApplied = false;
            castSequence = 0;
            castActionId = 0;
            castContext = GameContext.PvE;
            pendingPrediction = null;
            LastPredictedCastLock = 0f;
            LastActualCastLock = 0f;
        }

        private void MarkOwnedResponse(ushort sequence, uint actionId)
        {
            lastOwnedSequence = sequence;
            lastOwnedActionId = actionId;
            lastOwnedResponseUntilTick = Environment.TickCount64 + ResponseHandoffMilliseconds;
        }

        private static bool ResponseMatches(ushort expectedSequence, uint expectedActionId, ushort actualSequence, uint actualActionId)
        {
            var hasSequence = expectedSequence != 0 && actualSequence != 0;
            var hasAction = expectedActionId != 0 && actualActionId != 0;

            if (hasSequence && expectedSequence != actualSequence)
                return false;

            if (hasAction && expectedActionId != actualActionId)
                return false;

            // Some responses can be missing one identifier depending on action path.
            // The 500ms handoff accepts a match on the other concrete identifier only;
            // both-zero never matches, so unrelated non-cast responses are not hidden.
            return hasSequence || hasAction;
        }

        private static bool NearlyEqual(float left, float right)
            => Math.Abs(left - right) <= LockEqualityEpsilon;
    }
}
