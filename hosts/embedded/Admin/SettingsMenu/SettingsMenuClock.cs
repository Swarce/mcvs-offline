// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>A monotonic clock in milliseconds for the timings in the Settings menu's log lines (<see cref="SettingsMenuClock"/>; the tests give their own).</summary>
    internal interface ISettingsMenuClock
    {
        /// <summary>Milliseconds since an arbitrary start; may throw (the caller catches).</summary>
        double Milliseconds();
    }

    /// <summary>
    /// Build 23 (owner, build 22 on the S22: "a small stutter when tapping the gear"; docs/design/settings-menu.md §3.2, "Build 23"):
    /// the one place that touches <see cref="Stopwatch"/>, with the instance API the game itself uses (<c>new Stopwatch()</c>,
    /// <c>Start</c>, PlayerComponent.cs:1172-1173; the game reads <c>ElapsedMilliseconds</c>, :546, which is <c>Elapsed</c>'s path) and
    /// <c>Elapsed</c>, never the static <c>GetTimestamp</c> or <c>Frequency</c> (raw ElapsedTicks would need Frequency to be ms). The
    /// Stopwatch is kept as an object and reached only through <see cref="SettingsMenuTimer.Now"/>, whose try/catch also keeps the
    /// JIT from inlining it (the trimmed mscorlib has no MethodImplAttribute(MethodImplOptions) to say NoInlining), so a type that
    /// failed to load could only fail there, inside the callers' try/catch, never while the game compiles a delegate body of ours
    /// (OnInvokeDelegate would rethrow that into Flash and abort setup(); MenuComponent.cs ~:905). EmbeddedDllResolutionTests walks
    /// Stopwatch's members in the trimmed assemblies. No layout decision ever reads it.
    /// </summary>
    internal sealed class SettingsMenuClock : ISettingsMenuClock
    {
        private readonly object watch;

        private SettingsMenuClock(object watch)
        {
            this.watch = watch;
        }

        /// <summary>A running clock (UiNatives.Start, inside its try/catch).</summary>
        public static ISettingsMenuClock Create()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            return new SettingsMenuClock(stopwatch);
        }

        public double Milliseconds()
        {
            return ((Stopwatch)watch).Elapsed.TotalMilliseconds;
        }

        /// <summary>A clock that reads <paramref name="milliseconds"/> (the tests' clock, advanced by known amounts).</summary>
        internal static ISettingsMenuClock Of(Func<double> milliseconds)
        {
            return new FuncClock(milliseconds);
        }

        private sealed class FuncClock : ISettingsMenuClock
        {
            private readonly Func<double> read;

            public FuncClock(Func<double> read)
            {
                this.read = read;
            }

            public double Milliseconds()
            {
                return read();
            }
        }
    }

    /// <summary>
    /// One focus-in's timings (build 23, measurement only): EnterSection's steps, the same-frame step's calls, the breadcrumb, the
    /// engine's length of the focus-in frame and the run's own cost per frame. Written on the game thread by UiNatives,
    /// <see cref="SettingsMenuLayout"/> and <see cref="SameFrameBreadcrumb"/>; read only for the log lines.
    /// </summary>
    internal sealed class SettingsMenuTimings
    {
        private readonly List<KeyValuePair<string, double>> steps = new List<KeyValuePair<string, double>>();
        private readonly List<double> frameStarts = new List<double>();

        /// <summary>The run (<see cref="SettingsMenuState.LayoutRun"/>) of this focus-in; 0 until its grid step ran.</summary>
        public int Run;

        /// <summary>The first timed focus-in of this process (its JIT cost stands apart).</summary>
        public bool First;

        /// <summary>EnterSection's own time, all its steps.</summary>
        public double EnterSection;

        /// <summary>The clock when EnterSection returned.</summary>
        public double EnteredAt;

        /// <summary>The same-frame step's calls of its native (gate and looks) while armed: time and count.</summary>
        public double SameFrame;

        public int SameFrameCalls;

        /// <summary>The breadcrumb's write, when this focus-in wrote it.</summary>
        public double? Breadcrumb;

        /// <summary><c>ApplicationDeltaTime</c> read at the run's first frame: the engine's length of the focus-in frame.</summary>
        public double? FocusInFrameDt;

        /// <summary>
        /// <c>ApplicationDeltaTime</c> read in EnterSection: the frame before the focus-in (UpdateWorlds sets it at the frame's start,
        /// GameApplication.cs:1418), the baseline of <see cref="FocusInFrameDt"/>: both include the frame-rate limit's pacing.
        /// </summary>
        public double? PreviousFrameDt;

        /// <summary>The run's cost per frame: sum, frames, the largest and its frame.</summary>
        public double Ours;

        public int OursFrames;
        public double OursMax;
        public int OursMaxFrame;

        public void Step(string name, double ms)
        {
            steps.Add(new KeyValuePair<string, double>(name, ms));
        }

        /// <summary>A run frame started (its number, 1 first) at <paramref name="now"/>.</summary>
        public void FrameStarted(double now)
        {
            frameStarts.Add(now);
        }

        public void FrameCost(int frame, double ms)
        {
            Ours += ms;
            OursFrames++;
            if (OursFrames == 1 || ms > OursMax)
            {
                OursMax = ms;
                OursMaxFrame = frame;
            }
        }

        /// <summary>From EnterSection's end to the run's first frame (frame pacing included), or null.</summary>
        public double? ToFirstFrame
        {
            get { return frameStarts.Count == 0 ? (double?)null : frameStarts[0] - EnteredAt; }
        }

        /// <summary>
        /// The focus-in line's part: "; ms: EnterSection 4.2 (page 0.0, remap 0.1, ...), same frame 0.0, breadcrumb 1.9; focus-in
        /// frame dt 41.0 (the frame before 16.7); to the run's first frame 38.0; first in this process".
        /// </summary>
        public string FocusInText()
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, double> s in steps) parts.Add(s.Key + " " + Ms(s.Value));
            string text = "; ms: EnterSection " + Ms(EnterSection) + " (" + string.Join(", ", parts.ToArray()) + "), same frame " + Ms(SameFrame);
            if (Breadcrumb.HasValue) text += ", breadcrumb " + Ms(Breadcrumb.Value);
            if (FocusInFrameDt.HasValue) text += "; focus-in frame dt " + Ms(FocusInFrameDt.Value) + Baseline();
            double? first = ToFirstFrame;
            if (first.HasValue) text += "; to the run's first frame " + Ms(first.Value);
            if (First) text += "; first in this process";
            return text;
        }

        /// <summary>
        /// The laid-out line's part: "; ours 31.5 ms over 47 frames (max 1.1 at frame 1); frame gaps median 16.7, max 38.0 at frame
        /// 2". <paramref name="current"/> is the cost of the frame running now, not counted yet.
        /// </summary>
        public string RunText(double current, int frame)
        {
            double ours = Ours + current;
            int frames = OursFrames + 1;
            double max = OursMax;
            int maxFrame = OursMaxFrame;
            if (OursFrames == 0 || current > max)
            {
                max = current;
                maxFrame = frame;
            }
            string text = "; ours " + Ms(ours) + " ms over " + frames.ToString(CultureInfo.InvariantCulture) + (frames == 1 ? " frame" : " frames") +
                          " (max " + Ms(max) + " at frame " + maxFrame.ToString(CultureInfo.InvariantCulture) + ")";
            if (frameStarts.Count < 2) return text;
            var gaps = new List<double>();
            double gapMax = 0;
            int gapFrame = 0;
            for (int i = 1; i < frameStarts.Count; i++)
            {
                double gap = frameStarts[i] - frameStarts[i - 1];
                gaps.Add(gap);
                if (i == 1 || gap > gapMax)
                {
                    gapMax = gap;
                    gapFrame = i + 1;
                }
            }
            gaps.Sort();
            double median = gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
            return text + "; frame gaps median " + Ms(median) + ", max " + Ms(gapMax) + " at frame " + gapFrame.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Both switches off (the run ends at its first frame): "settings menu: focus-in N timings (stock): ...".</summary>
        public string StockText(int run)
        {
            double? first = ToFirstFrame;
            return "settings menu: focus-in " + run.ToString(CultureInfo.InvariantCulture) + " timings (stock): focus-in frame dt " +
                   (FocusInFrameDt.HasValue ? Ms(FocusInFrameDt.Value) + Baseline() : "unknown") + ", to the run's first frame " +
                   (first.HasValue ? Ms(first.Value) : "unknown") + " ms";
        }

        /// <summary>" (the frame before 16.7)": the paced length a focus-in frame dt compares with; empty when it was not read.</summary>
        private string Baseline()
        {
            return PreviousFrameDt.HasValue ? " (the frame before " + Ms(PreviousFrameDt.Value) + ")" : "";
        }

        /// <summary>One decimal, invariant ("4.2").</summary>
        public static string Ms(double ms)
        {
            return ms.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The clock as the Settings menu code reads it (build 23): <see cref="SettingsMenuState.Clock"/>, null without one or after a
    /// failure; the first failure logs <c>settings menu: timings unavailable (Type: message)</c> once, and every line keeps its
    /// build-22 text from then on. Never throws.
    /// </summary>
    internal static class SettingsMenuTimer
    {
        public static double? Now(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (state == null) return null;
            ISettingsMenuClock clock = state.Clock;
            if (clock == null || state.TimingsFailed) return null;
            try
            {
                return clock.Milliseconds();
            }
            catch (Exception ex)
            {
                try
                {
                    state.Clock = null;
                    if (state.BeginTimingsFailure() && host != null && host.Log != null)
                    {
                        Exception cause = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        host.Log.Info("settings menu: timings unavailable (" + cause.GetType().Name + ": " + cause.Message + ")");
                    }
                }
                catch (Exception)
                {
                }
                return null;
            }
        }

        /// <summary>The timings of run <paramref name="run"/>, or null (none, another run's, or the clock failed since).</summary>
        public static SettingsMenuTimings Of(SettingsMenuState state, int run)
        {
            SettingsMenuTimings t = state == null || state.TimingsFailed ? null : state.Timings;
            return t != null && t.Run == run ? t : null;
        }
    }
}
