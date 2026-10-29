using System;
using System.IO;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// Everything the kit's commands take from the machine: the console, the special folders, the clock, the running game,
    /// the installer and the probe actions. <see cref="Real"/> is the player's PC; tests build one over temp folders, so every
    /// command runs on any OS without touching the real profile.
    /// </summary>
    public sealed class KitEnvironment
    {
        public TextWriter Out { get; set; }

        public TextWriter Error { get; set; }

        public ConsoleLines Input { get; set; }

        /// <summary>%TEMP%: the kit's temporary folders (mcvs-pckit-&lt;guid&gt;) and the fallback log folder (McvsOffline\kit, \logs).</summary>
        public string TempRoot { get; set; }

        /// <summary>Where 'collect' puts its zip (falls back to Documents, then %TEMP%).</summary>
        public string Desktop { get; set; }

        public string Documents { get; set; }

        /// <summary>%APPDATA% (the engine's own data, listed by 'collect').</summary>
        public string AppData { get; set; }

        /// <summary>%LOCALAPPDATA% (the game's crash logs in Gameloft\, the spike's McvsOffline\spike).</summary>
        public string LocalAppData { get; set; }

        public Func<DateTime> Now { get; set; }

        public Func<bool> GameRunning { get; set; }

        /// <summary>The PC server DLL the kit installs (the embedded resource), or null when the kit was built without it.</summary>
        public Func<byte[]> ServerDll { get; set; }

        public Func<PcInstallOptions, PcInstallReport> Install { get; set; }

        public Func<PcUninstallOptions, PcInstallReport> Uninstall { get; set; }

        public IPcFileOps Files { get; set; }

        public IProbeActions Probe { get; set; }

        /// <summary>The game folder when neither --game nor MCVS_PC_GAME gives one: the Steam lookup (<see cref="SteamLocator"/>).</summary>
        public Func<string, GameLocation> LocateGame { get; set; }

        /// <summary>The .NET Framework 4 folder the game runs on (Windows: %WINDIR%\Microsoft.NET\Framework\v4.0.30319).</summary>
        public Func<string> DefaultBcl { get; set; }

        /// <summary>(pid, file): writes a full minidump of that process, returns its size; throws on failure (<see cref="MiniDump"/>).</summary>
        public Func<int, string, long> WriteDump { get; set; }

        /// <summary>Free bytes on the drive of a folder, -1 when unknown.</summary>
        public Func<string, long> FreeBytes { get; set; }

        /// <summary>The kit's own file, hashed for the log headers and summary.txt (<see cref="KitServer.Identity"/>); null: unknown.</summary>
        public Func<string> KitPath { get; set; }

        public static KitEnvironment Real()
        {
            return new KitEnvironment
            {
                Out = Console.Out,
                Error = Console.Error,
                Input = ConsoleLines.Stdin,
                TempRoot = Path.GetTempPath(),
                Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Now = () => DateTime.Now,
                GameRunning = GameFolder.IsGameRunning,
                ServerDll = KitServer.Embedded,
                Install = PcInstaller.Install,
                Uninstall = PcInstaller.Uninstall,
                Files = new PcFileOps(),
                Probe = new RealProbeActions(),
                LocateGame = folder =>
                {
                    string problem;
                    GameLocation found = SteamLocator.Locate(folder, out problem);
                    if (found == null) throw new DirectoryNotFoundException(problem);
                    return found;
                },
                DefaultBcl = PcCli.DefaultBcl,
                WriteDump = MiniDump.Write,
                FreeBytes = MiniDump.FreeBytes,
                KitPath = KitServer.OwnPath,
            };
        }
    }
}
