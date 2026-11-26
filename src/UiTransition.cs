using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualRemote
{
    internal sealed class UiTransition
    {
        private const uint GetClientAreaAnimation = 0x1042;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);

        private readonly Control owner;
        private readonly Action invalidate;
        private Timer timer;
        private float start;
        private float target;
        private long startedAt;
        private int duration;

        internal UiTransition(Control owner, float initial = 0f, Action invalidate = null)
        {
            this.owner = owner;
            this.invalidate = invalidate ?? new Action(owner.Invalidate);
            Value = target = initial;
            owner.VisibleChanged += delegate { if (!owner.Visible) Finish(); };
            owner.HandleDestroyed += delegate { Finish(); };
            owner.Disposed += delegate
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                }
            };
        }

        internal float Value { get; private set; }

        internal void MoveTo(float next, int milliseconds)
        {
            if (owner.IsDisposed || owner.Disposing) return;
            if (milliseconds > 0 && next == target) return;
            target = next;

            if (Value == target || milliseconds <= 0 || !owner.IsHandleCreated || !owner.Visible || !AnimationsEnabled())
            {
                bool changed = Value != target;
                Finish();
                if (changed) invalidate();
                return;
            }

            start = Value;
            duration = milliseconds;
            startedAt = Stopwatch.GetTimestamp();
            if (timer == null)
            {
                timer = new Timer { Interval = 15 };
                timer.Tick += Advance;
            }
            timer.Start();
        }

        private static bool AnimationsEnabled()
        {
            bool enabled;
            return SystemParametersInfo(GetClientAreaAnimation, 0, out enabled, 0)
                ? enabled : SystemInformation.UIEffectsEnabled;
        }

        private void Advance(object sender, EventArgs e)
        {
            if (owner.IsDisposed || owner.Disposing)
            {
                Finish();
                return;
            }
            double elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            float remaining = 1f - Math.Min(1f, (float)(elapsed / duration));
            Value = target + (start - target) * remaining * remaining * remaining;
            if (remaining <= 0f) Finish();
            invalidate();
        }

        private void Finish()
        {
            if (timer != null) timer.Stop();
            Value = target;
        }
    }
}
