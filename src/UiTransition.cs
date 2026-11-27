using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualRemote
{
    internal enum UiEase
    {
        EaseOutCubic,
        EaseOutQuart,
        EaseOutQuint,
        EaseOutExpo,
        EaseOutBackSoft,
        SpringSettle
    }

    internal sealed class UiTransition
    {
        private const uint GetClientAreaAnimation = 0x1042;
        private static readonly object HighResLock = new object();
        private static int highResRefCount;
        private static readonly float SpringSettleNorm = ComputeRawSpring(1f);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool enabled, uint flags);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        private readonly Control owner;
        private readonly Action invalidate;
        private Timer timer;
        private float start;
        private float target;
        private long startedAt;
        private int duration;
        private UiEase activeEase = UiEase.EaseOutQuart;
        private bool highResAcquired;

        internal UiTransition(Control owner, float initial = 0f, Action invalidate = null)
        {
            this.owner = owner;
            this.invalidate = invalidate ?? new Action(owner.Invalidate);
            Value = target = initial;
            owner.VisibleChanged += delegate { if (!owner.Visible) Finish(); };
            owner.HandleDestroyed += delegate { Finish(); };
            owner.Disposed += delegate
            {
                ReleaseHighRes();
                if (timer != null)
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                }
            };
        }

        internal static int ActiveHighResCount
        {
            get
            {
                lock (HighResLock) return highResRefCount;
            }
        }

        internal float Value { get; private set; }

        internal bool IsRunning
        {
            get { return timer != null && timer.Enabled; }
        }

        internal static bool AreAnimationsEnabled()
        {
            return AnimationsEnabled();
        }

        internal static float EvaluateCurve(float t, UiEase ease)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            switch (ease)
            {
                case UiEase.EaseOutCubic:
                {
                    float r = 1f - t;
                    return 1f - r * r * r;
                }
                case UiEase.EaseOutQuart:
                {
                    float r = 1f - t;
                    float r2 = r * r;
                    return 1f - r2 * r2;
                }
                case UiEase.EaseOutQuint:
                {
                    float r = 1f - t;
                    float r2 = r * r;
                    return 1f - r2 * r2 * r;
                }
                case UiEase.EaseOutExpo:
                    return 1f - (float)Math.Pow(2.0, -10.0 * t);
                case UiEase.EaseOutBackSoft:
                {
                    const float s = 1.05f;
                    float r = t - 1f;
                    return 1f + (s + 1f) * r * r * r + s * r * r;
                }
                case UiEase.SpringSettle:
                {
                    float norm = SpringSettleNorm > 0.0001f ? SpringSettleNorm : 1f;
                    return ComputeRawSpring(t) / norm;
                }
                default:
                {
                    float r = 1f - t;
                    float r2 = r * r;
                    return 1f - r2 * r2;
                }
            }
        }

        private static float ComputeRawSpring(float t)
        {
            return 1f - (float)(Math.Exp(-6.8 * t) * (Math.Cos(7.8 * t) + (6.8 / 7.8) * Math.Sin(7.8 * t)));
        }

        internal void MoveTo(float next, int milliseconds)
        {
            AnimateTo(next, milliseconds, UiEase.EaseOutQuart);
        }

        internal void AnimateTo(float next, int milliseconds, UiEase ease)
        {
            if (owner.IsDisposed || owner.Disposing) return;
            if (milliseconds > 0 && next == target && ease == activeEase && (IsRunning || Value == next)) return;
            target = next;
            activeEase = ease;

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
            EnsureTimer();
            AcquireHighRes();
            timer.Start();
        }

        internal void RestartFrom(float fromValue, float toValue, int milliseconds, UiEase ease)
        {
            if (owner.IsDisposed || owner.Disposing) return;
            target = toValue;
            activeEase = ease;

            if (milliseconds <= 0 || !owner.IsHandleCreated || !owner.Visible || !AnimationsEnabled())
            {
                bool changed = Value != target;
                Finish();
                if (changed) invalidate();
                return;
            }

            Value = fromValue;
            start = fromValue;
            duration = milliseconds;
            startedAt = Stopwatch.GetTimestamp();
            EnsureTimer();
            AcquireHighRes();
            timer.Start();
            invalidate();
        }

        internal void SnapTo(float next)
        {
            if (owner.IsDisposed || owner.Disposing)
            {
                ReleaseHighRes();
                return;
            }
            bool changed = Value != next;
            target = next;
            Finish();
            if (changed) invalidate();
        }

        private void EnsureTimer()
        {
            if (timer == null)
            {
                timer = new Timer { Interval = 8 };
                timer.Tick += Advance;
            }
        }

        private void AcquireHighRes()
        {
            if (highResAcquired) return;
            lock (HighResLock)
            {
                if (highResRefCount == 0)
                {
                    try { timeBeginPeriod(1); } catch { }
                }
                highResRefCount++;
                highResAcquired = true;
            }
        }

        private void ReleaseHighRes()
        {
            if (!highResAcquired) return;
            lock (HighResLock)
            {
                if (!highResAcquired) return;
                highResAcquired = false;
                if (highResRefCount > 0)
                {
                    highResRefCount--;
                    if (highResRefCount == 0)
                    {
                        try { timeEndPeriod(1); } catch { }
                    }
                }
            }
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
            float t = Math.Min(1f, Math.Max(0f, (float)(elapsed / Math.Max(1, duration))));
            Value = start + (target - start) * EvaluateCurve(t, activeEase);
            if (t >= 1f) Finish();
            invalidate();
        }

        private void Finish()
        {
            if (timer != null) timer.Stop();
            ReleaseHighRes();
            Value = target;
        }
    }
}
