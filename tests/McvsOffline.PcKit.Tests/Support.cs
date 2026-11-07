using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>The two helpers the linked spike SteamLocatorTests use (tests/McvsOffline.PcSpike.Tests/Support.cs has the same).</summary>
    public static class Support
    {
        public static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mcvs-pckit-tests-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void DeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

namespace McvsOffline.PcKit.Tests
{
    /// <summary>A kit environment over temp folders: a fake game folder, fake special folders, a console in strings.</summary>
    public sealed class KitSandbox : IDisposable
    {
        public KitSandbox(string input = "")
        {
            Root = McvsOffline.PcSpike.Tests.Support.NewTempDir("kit");
            Game = Path.Combine(Root, "steamapps", "common", "Modern Combat Versus");
            Directory.CreateDirectory(Path.Combine(Game, "Data"));
            File.WriteAllText(Path.Combine(Game, GameFolder.ExeName), "not really an exe");
            foreach (string f in GameFolder.PatchedFiles) File.WriteAllText(Path.Combine(Game, f), "dll " + f);
            File.WriteAllText(Path.Combine(Game, "Data", "MCVS.game"), "game");
            Temp = Mk("temp");
            Out = new StringWriter();
            Err = new StringWriter();
            Probe = new FakeProbe();
            Env = new KitEnvironment
            {
                Out = Out,
                Error = Err,
                Input = new ConsoleLines(new StringReader(input)),
                TempRoot = Temp,
                Desktop = Mk("Desktop"),
                Documents = Mk("Documents"),
                AppData = Mk("AppData/Roaming"),
                LocalAppData = Mk("AppData/Local"),
                Now = () => new DateTime(2026, 11, 2, 18, 0, 0),
                GameRunning = () => false,
                ServerDll = () => new byte[] { 1, 2, 3 },
                Install = o => { throw new InvalidOperationException("no installer in this test"); },
                Uninstall = PcInstaller.Uninstall,
                Files = new PcFileOps(),
                Probe = Probe,
                LocateGame = folder =>
                {
                    // No Steam search in tests: only an explicit folder (as --game) is found.
                    string problem = "no --game in this test";
                    GameLocation found = folder == null ? null : SteamLocator.Locate(folder, out problem);
                    if (found == null) throw new DirectoryNotFoundException(problem);
                    return found;
                },
                DefaultBcl = () => Path.Combine(Root, "bcl"),
                KitPath = () => KitFile,
            };
            File.WriteAllText(KitFile, KitFileText);
        }

        /// <summary>What the sandbox's kit file holds (its SHA-256 is in every log header and summary.txt).</summary>
        public const string KitFileText = "the kit exe";

        /// <summary>The kit's own file in this sandbox.</summary>
        public string KitFile { get { return Path.Combine(Root, "McvsOffline.PcKit.exe"); } }

        public string Root { get; }

        public string Game { get; }

        public string Temp { get; }

        public StringWriter Out { get; }

        public StringWriter Err { get; }

        public FakeProbe Probe { get; }

        public KitEnvironment Env { get; }

        public string Mk(string relative)
        {
            string dir = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public KitOptions Options(params string[] args)
        {
            return KitOptions.Parse(args.Concat(new[] { "--game", Game }).ToArray(), null);
        }

        public void Dispose()
        {
            McvsOffline.PcSpike.Tests.Support.DeleteDir(Root);
        }
    }

    /// <summary>Probe actions with fixed answers.</summary>
    public sealed class FakeProbe : IProbeActions
    {
        public ProbeOutcome HttpAnswer = new ProbeOutcome(true, "ok");
        public bool? Elevated = false;
        public Dictionary<string, string> Tools = new Dictionary<string, string>();
        public ProbeOutcome BindAnswer = new ProbeOutcome(true, "ok");
        public ProbeOutcome WritableAnswer = new ProbeOutcome(true, "ok");
        public int? Release = 528040;
        public List<string> Processes = new List<string> { "steam", "explorer" };
        public List<string> WritableAsked = new List<string>();

        public ProbeOutcome HttpSys(string prefix) { return HttpAnswer; }

        public bool? IsElevated() { return Elevated; }

        public string RunTool(string file, string arguments)
        {
            string output;
            return Tools.TryGetValue(file + " " + arguments, out output) ? output : null;
        }

        public ProbeOutcome TcpBind(IPAddress address, int port) { return BindAnswer; }

        public ProbeOutcome FolderWritable(string folder)
        {
            WritableAsked.Add(folder);
            return WritableAnswer;
        }

        public int? NetFrameworkRelease() { return Release; }

        public IList<string> ProcessNames() { return Processes; }

        public List<GpuAdapter> Gpus;
        public List<DisplayMode> Screens;
        public long? Memory;

        public IList<GpuAdapter> GpuAdapters() { return Gpus; }

        public IList<DisplayMode> Displays() { return Screens; }

        public long? PhysicalMemoryBytes() { return Memory; }
    }
}
