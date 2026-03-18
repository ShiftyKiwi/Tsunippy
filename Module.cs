using Tsunippy.Runtime;

namespace Tsunippy
{
    public abstract class Module
    {
        public virtual bool DisableOnRuntimeFailure => true;

        public virtual bool IsEnabled
        {
            get => true;
            set => _ = value;
        }
        public virtual int DrawOrder => 0;

        public virtual void ResetRuntime(RuntimeResetReason reason) { }
        public virtual void DrawConfig() { }
        public virtual void Enable() { }
        public virtual void Disable() { }
    }
}
