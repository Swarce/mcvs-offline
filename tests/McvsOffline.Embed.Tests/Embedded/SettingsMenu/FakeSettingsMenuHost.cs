using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// An <see cref="ISettingsMenuHost"/> for the Settings menu tests: the two switches, the platform, a <see cref="FakeFlashTree"/> as
    /// the client UI, a game-thread queue drained by <see cref="Drain"/> (the start of the next frame), a log and the page's values.
    /// </summary>
    internal sealed class FakeSettingsMenuHost : ISettingsMenuHost
    {
        private readonly Queue<Action> posted = new Queue<Action>();

        public FakeSettingsMenuHost(FakeFlashTree ui = null, bool windows = false)
        {
            Ui = ui ?? new FakeFlashTree(windows);
            IsWindows = windows;
            Logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(Logs);
            Log = logger.For("admin");
            Settings = new FakePerformanceSettings();
        }

        public bool SettingsMenuTrim { get; set; } = true;

        /// <summary>The setting; <see cref="SettingsMenuPerformance"/> is false on PC whatever it says (as AdminHost).</summary>
        public bool PerformanceSetting { get; set; } = true;

        public bool SettingsMenuPerformance { get { return !IsWindows && PerformanceSetting; } }

        public bool IsWindows { get; set; }

        public Log Log { get; private set; }

        public MemoryLogSink Logs { get; private set; }

        public FakePerformanceSettings Settings { get; private set; }

        public IPerformanceSettings PerformanceSettings { get { return Settings; } }

        /// <summary>What <see cref="ClientUi"/> returns (null: no UI yet).</summary>
        public FakeFlashTree Ui { get; set; }

        /// <summary>False: <see cref="Post"/> fails (no game thread).</summary>
        public bool CanPost { get; set; } = true;

        public int Pending { get { return posted.Count; } }

        public List<string> Exits { get; } = new List<string>();

        public object ClientUi()
        {
            return Ui;
        }

        public bool Post(Action action)
        {
            if (!CanPost || action == null) return false;
            posted.Enqueue(action);
            return true;
        }

        /// <summary>Runs every posted step, also the ones the steps post (a frame at a time until none is left, at most 100).</summary>
        public int Drain()
        {
            int ran = 0;
            for (int frame = 0; frame < 100 && posted.Count > 0; frame++)
            {
                int n = posted.Count;
                for (int i = 0; i < n; i++)
                {
                    posted.Dequeue()();
                    ran++;
                }
            }
            return ran;
        }

        public bool Exit(string reason, bool relaunch)
        {
            Exits.Add(reason + (relaunch ? " relaunch" : ""));
            return true;
        }

        /// <summary>Log lines starting with <paramref name="prefix"/>.</summary>
        public List<string> Lines(string prefix)
        {
            var lines = new List<string>();
            foreach (LogEntry e in Logs.Snapshot())
            {
                if (e.Message.StartsWith(prefix, StringComparison.Ordinal)) lines.Add(e.Message);
            }
            return lines;
        }
    }

    /// <summary>
    /// <see cref="IPerformanceSettings"/> in memory, validated by the real schema like <see cref="PerformanceSettingsStore"/>, saving at
    /// once; every accepted save is in <see cref="Saves"/> ("key=value"), refused ones in <see cref="Refused"/>.
    /// </summary>
    internal sealed class FakePerformanceSettings : IPerformanceSettings
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "graphics.profile", "auto" },
            { SettingsSchema.GraphicsMsaaKey, "auto" },
            { SettingsSchema.GraphicsRenderScaleKey, "auto" },
            { SettingsSchema.GraphicsLowResMapsKey, "auto" },
            { SettingsSchema.GraphicsFpsListVersionKey, 0L },
        };

        public List<string> Saves { get; } = new List<string>();

        public List<string> Refused { get; } = new List<string>();

        public string GraphicsProfile { get { return (string)values["graphics.profile"]; } }

        public string Msaa { get { return (string)values[SettingsSchema.GraphicsMsaaKey]; } }

        public string RenderScale { get { return (string)values[SettingsSchema.GraphicsRenderScaleKey]; } }

        public string LowResMaps { get { return (string)values[SettingsSchema.GraphicsLowResMapsKey]; } }

        public long FpsListVersion { get { return (long)values[SettingsSchema.GraphicsFpsListVersionKey]; } }

        /// <summary>Sets a stored value without a save (the test's starting state).</summary>
        public void Store(string key, object value)
        {
            values[key] = value;
        }

        public bool Save(string key, object value)
        {
            string text = key + "=" + Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!values.ContainsKey(key))
            {
                Refused.Add(text);
                return false;
            }
            object normalized;
            if (key == SettingsSchema.GraphicsFpsListVersionKey)
            {
                if (!(value is long || value is int) || Convert.ToInt64(value, CultureInfo.InvariantCulture) < 0)
                {
                    Refused.Add(text);
                    return false;
                }
                normalized = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            else
            {
                string problem;
                if (!SettingsSchema.Validate(SettingsSchema.Find(key), value, out normalized, out problem))
                {
                    Refused.Add(text);
                    return false;
                }
            }
            if (Equals(values[key], normalized)) return true;
            values[key] = normalized;
            Saves.Add(key + "=" + Convert.ToString(normalized, CultureInfo.InvariantCulture));
            return true;
        }
    }
}
