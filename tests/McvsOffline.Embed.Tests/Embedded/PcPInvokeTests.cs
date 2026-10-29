using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Windows;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The Windows P/Invokes of the PC host (docs/design/pc-port.md §6.3, §6.5, §12.2), checked with Cecil on the built PC DLL of
    /// this configuration (and on the net8 test assembly, which compiles the same sources): every steam_api import is cdecl with
    /// an entry point present in the export directory of the real steam_api.dll; IsOverlayEnabled and IsSteamRunning return a
    /// one-byte bool; steam_api strings are ANSI with best-fit mapping off; every kernel32/user32 import names its exact export,
    /// the *W ones are Unicode; the imports whose error is read set SetLastError.
    /// </summary>
    public class PcPInvokeTests
    {
        private static readonly HashSet<string> AllowedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kernel32", "user32", "steam_api" };

        /// <summary>The imports whose Win32 error the code reads.</summary>
        private static readonly string[] NeedLastError = { "MoveFileExW", "OpenClipboard", "SetClipboardData", "CreateWindowExW" };

        private readonly ITestOutputHelper output;

        public PcPInvokeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private List<string> Modules()
        {
            var paths = new List<string> { typeof(SteamOverlay).Assembly.Location };
            string dll = TestEnvironment.PcServerDll;
            if (dll != null) paths.Add(dll);
            else if (TestEnvironment.RequirePc) throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but the PC server DLL of this configuration is not built");
            else output.WriteLine("the PC server DLL is not built: only the net8 copy of the sources is checked");
            return paths;
        }

        private static List<MethodDefinition> Imports(ModuleDefinition module, bool windowsOnly)
        {
            var list = new List<MethodDefinition>();
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                TypeDefinition t = stack.Pop();
                foreach (TypeDefinition n in t.NestedTypes) stack.Push(n);
                if (!t.FullName.StartsWith("McvsOffline.", StringComparison.Ordinal)) continue;
                foreach (MethodDefinition m in t.Methods.Where(x => x.IsPInvokeImpl))
                {
                    if (!windowsOnly || AllowedModules.Contains(m.PInvokeInfo.Module.Name)) list.Add(m);
                }
            }
            return list;
        }

        private static string Name(MethodDefinition m)
        {
            return m.DeclaringType.Name + "." + m.Name + " -> " + m.PInvokeInfo.Module.Name + "!" + (m.PInvokeInfo.EntryPoint ?? m.Name);
        }

        [Fact]
        public void TheSteamImportsMatchTheX86Exports()
        {
            HashSet<string> exports = SteamExports();
            foreach (string path in Modules())
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true }))
                {
                    List<MethodDefinition> steam = Imports(module, true).Where(m => m.PInvokeInfo.Module.Name == "steam_api").ToList();
                    Assert.Equal(8, steam.Count);
                    var problems = new List<string>();
                    foreach (MethodDefinition m in steam)
                    {
                        PInvokeInfo info = m.PInvokeInfo;
                        if (!info.IsCallConvCdecl) problems.Add(Name(m) + ": not cdecl");
                        if (!info.IsNoMangle) problems.Add(Name(m) + ": no ExactSpelling");
                        if (string.IsNullOrEmpty(info.EntryPoint) || info.EntryPoint != m.Name) problems.Add(Name(m) + ": EntryPoint is not the export name");
                        if (exports != null && !exports.Contains(info.EntryPoint)) problems.Add(Name(m) + ": not exported by steam_api.dll");
                        if (m.Parameters.Any(p => p.ParameterType.FullName == "System.String"))
                        {
                            if (!info.IsCharSetAnsi) problems.Add(Name(m) + ": strings not ANSI");
                            if (!info.IsBestFitDisabled) problems.Add(Name(m) + ": BestFitMapping not off");
                            if (!info.IsThrowOnUnmappableCharEnabled) problems.Add(Name(m) + ": ThrowOnUnmappableChar not on");
                            foreach (ParameterDefinition p in m.Parameters.Where(p => p.ParameterType.FullName == "System.String"))
                            {
                                if (p.MarshalInfo == null || p.MarshalInfo.NativeType != NativeType.LPStr) problems.Add(Name(m) + ": " + p.Name + " not LPStr");
                            }
                        }
                        if (m.ReturnType.FullName == "System.Boolean")
                        {
                            MarshalInfo ret = m.MethodReturnType.MarshalInfo;
                            if (ret == null || ret.NativeType != NativeType.U1) problems.Add(Name(m) + ": bool return not MarshalAs(U1)");
                        }
                        if (m.Parameters.Any(p => p.ParameterType.FullName == "System.Int64")) problems.Add(Name(m) + ": a 64-bit parameter (handles are int)");
                    }
                    Assert.True(problems.Count == 0, Path.GetFileName(path) + ":\n" + string.Join("\n", problems));
                    Assert.Contains(steam, m => m.Name == "SteamAPI_ISteamUtils_IsOverlayEnabled" && m.ReturnType.FullName == "System.Boolean");
                    Assert.Contains(steam, m => m.Name == "SteamAPI_IsSteamRunning" && m.ReturnType.FullName == "System.Boolean");
                    MethodDefinition web = steam.Single(m => m.Name == "SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage");
                    Assert.Equal(new[] { "System.IntPtr", "System.String" }, web.Parameters.Select(p => p.ParameterType.FullName).ToArray());   // 2 parameters: SDK 1.42
                    Assert.Equal(new[] { "System.IntPtr", "System.Int32", "System.Int32", "System.String" },
                                 steam.Single(m => m.Name == "SteamAPI_ISteamClient_GetISteamFriends").Parameters.Select(p => p.ParameterType.FullName).ToArray());
                }
            }
        }

        [Fact]
        public void TheWin32ImportsNameTheirExactExports()
        {
            foreach (string path in Modules())
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true }))
                {
                    List<MethodDefinition> win32 = Imports(module, true).Where(m => m.PInvokeInfo.Module.Name != "steam_api").ToList();
                    output.WriteLine(Path.GetFileName(path) + ": " + string.Join(", ", win32.Select(Name)));
                    var problems = new List<string>();
                    foreach (MethodDefinition m in win32)
                    {
                        PInvokeInfo info = m.PInvokeInfo;
                        string entry = info.EntryPoint ?? m.Name;
                        if (!info.IsNoMangle) problems.Add(Name(m) + ": no ExactSpelling");
                        if (entry.EndsWith("W", StringComparison.Ordinal) && !info.IsCharSetUnicode) problems.Add(Name(m) + ": a *W import that is not Unicode");
                        if (m.Parameters.Any(p => p.ParameterType.FullName == "System.String") && !info.IsCharSetUnicode)
                            problems.Add(Name(m) + ": passes a string without CharSet.Unicode");
                        if (Array.IndexOf(NeedLastError, entry) >= 0 && !info.SupportsLastError) problems.Add(Name(m) + ": no SetLastError");
                    }
                    Assert.True(problems.Count == 0, Path.GetFileName(path) + ":\n" + string.Join("\n", problems));
                    foreach (string expected in new[] { "OutputDebugStringW", "GetCurrentThreadId", "GlobalAlloc", "GlobalLock", "GlobalUnlock", "GlobalFree",
                                                        "OpenClipboard", "EmptyClipboard", "SetClipboardData", "CloseClipboard", "CreateWindowExW", "DestroyWindow",
                                                        "SetUnhandledExceptionFilter", "GetModuleHandleExW", "GetModuleFileNameW", "CreateFileW", "WriteFile",
                                                        "FlushFileBuffers", "CloseHandle" })
                    {
                        Assert.Contains(win32, m => (m.PInvokeInfo.EntryPoint ?? m.Name) == expected);
                    }
                }
            }
        }

        /// <summary>
        /// WR2 F3: the native crash filter's imports run inside a top-level exception filter, so none marshals a string (paths and
        /// names are pinned char[] buffers of exactly the *W exports); SetUnhandledExceptionFilter takes and returns a raw
        /// function pointer (WINAPI), and the filter's delegate type is declared stdcall with the LONG (EXCEPTION_POINTERS*) shape.
        /// </summary>
        [Fact]
        public void TheCrashFilterImportsAreAllocationFreeAndTheFilterIsStdcall()
        {
            foreach (string path in Modules())
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true }))
                {
                    Dictionary<string, MethodDefinition> win32 = Imports(module, true).Where(m => m.DeclaringType.Name == "Win32")
                                                                                       .ToDictionary(m => m.PInvokeInfo.EntryPoint ?? m.Name);
                    string Shape(string entry)
                    {
                        MethodDefinition m = win32[entry];
                        return m.ReturnType.FullName + " (" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
                    }
                    Assert.Equal("System.IntPtr (System.IntPtr)", Shape("SetUnhandledExceptionFilter"));
                    Assert.True(win32["SetUnhandledExceptionFilter"].PInvokeInfo.IsCallConvWinapi);
                    Assert.Equal("System.Boolean (System.UInt32, System.IntPtr, System.IntPtr&)", Shape("GetModuleHandleExW"));
                    Assert.Equal("System.UInt32 (System.IntPtr, System.Char[], System.UInt32)", Shape("GetModuleFileNameW"));
                    Assert.True(win32["GetModuleFileNameW"].Parameters[1].IsOut);
                    Assert.Equal("System.IntPtr (System.Char[], System.UInt32, System.UInt32, System.IntPtr, System.UInt32, System.UInt32, System.IntPtr)", Shape("CreateFileW"));
                    Assert.Equal("System.Boolean (System.IntPtr, System.Byte[], System.UInt32, System.UInt32&, System.IntPtr)", Shape("WriteFile"));
                    Assert.Equal("System.Boolean (System.IntPtr)", Shape("FlushFileBuffers"));
                    Assert.Equal("System.Boolean (System.IntPtr)", Shape("CloseHandle"));
                    foreach (string entry in new[] { "GetModuleHandleExW", "GetModuleFileNameW", "CreateFileW" })
                        Assert.True(win32[entry].PInvokeInfo.IsCharSetUnicode, entry + ": the char[] must be WCHAR (blittable, pinned)");
                    foreach (string entry in new[] { "SetUnhandledExceptionFilter", "GetModuleHandleExW", "GetModuleFileNameW", "CreateFileW", "WriteFile", "FlushFileBuffers", "CloseHandle" })
                        Assert.DoesNotContain(win32[entry].Parameters, p => p.ParameterType.FullName == "System.String");

                    TypeDefinition filter = module.GetType("McvsOffline.Embedded.Windows.NativeCrashFilter").NestedTypes.Single(t => t.Name == "TopLevelFilter");
                    CustomAttribute convention = filter.CustomAttributes.Single(a => a.AttributeType.Name == "UnmanagedFunctionPointerAttribute");
                    Assert.Equal(3, (int)convention.ConstructorArguments[0].Value);   // CallingConvention.StdCall (WINAPI on x86)
                    MethodDefinition invoke = filter.Methods.Single(m => m.Name == "Invoke");
                    Assert.Equal("System.Int32", invoke.ReturnType.FullName);
                    Assert.Equal(new[] { "System.IntPtr" }, invoke.Parameters.Select(p => p.ParameterType.FullName).ToArray());
                }
            }
        }

        [Fact]
        public void ThePcDllImportsOnlyWindowsModules()
        {
            string dll = TestEnvironment.PcServerDll;
            if (dll == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but the PC server DLL is not built");
                return;
            }
            using (ModuleDefinition module = ModuleDefinition.ReadModule(dll, new ReaderParameters { InMemory = true }))
            {
                string[] refs = module.ModuleReferences.Select(r => r.Name).ToArray();
                output.WriteLine("ModuleRefs: " + string.Join(", ", refs));
                Assert.All(refs, r => Assert.Contains(r, AllowedModules));
                Assert.Contains("steam_api", refs);
                Assert.Contains("user32", refs);
            }
        }

        /// <summary>The names in the export directory of $MCVS_PC_REFS/steam_api.dll (PE32), or null without the file.</summary>
        private HashSet<string> SteamExports()
        {
            string pc = TestEnvironment.PcRefs;
            string file = pc == null ? null : Path.Combine(pc, "steam_api.dll");
            if (file == null || !File.Exists(file))
            {
                if (TestEnvironment.RequirePc) throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but MCVS_PC_REFS has no steam_api.dll");
                output.WriteLine("steam_api.dll is not available (MCVS_PC_REFS): the export check is skipped");
                return null;
            }
            HashSet<string> names = PeExports(File.ReadAllBytes(file));
            output.WriteLine("steam_api.dll exports " + names.Count + " names");
            Assert.Equal(842, names.Count);   // [WH] §1.1
            return names;
        }

        /// <summary>The exported names of a PE32 image (export directory → AddressOfNames).</summary>
        internal static HashSet<string> PeExports(byte[] image)
        {
            int pe = BitConverter.ToInt32(image, 0x3C);
            Assert.Equal(0x00004550u, BitConverter.ToUInt32(image, pe));
            int sections = BitConverter.ToUInt16(image, pe + 6);
            int optionalSize = BitConverter.ToUInt16(image, pe + 20);
            int optional = pe + 24;
            Assert.Equal(0x10B, BitConverter.ToUInt16(image, optional));   // PE32: the 32-bit steam_api.dll
            int exportRva = BitConverter.ToInt32(image, optional + 96);
            int sectionTable = optional + optionalSize;
            Func<int, int> offset = rva =>
            {
                for (int i = 0; i < sections; i++)
                {
                    int s = sectionTable + i * 40;
                    int va = BitConverter.ToInt32(image, s + 12);
                    int size = Math.Max(BitConverter.ToInt32(image, s + 8), BitConverter.ToInt32(image, s + 16));
                    if (rva >= va && rva < va + size) return rva - va + BitConverter.ToInt32(image, s + 20);
                }
                throw new InvalidDataException("RVA 0x" + rva.ToString("x") + " is in no section");
            };
            int export = offset(exportRva);
            int count = BitConverter.ToInt32(image, export + 24);
            int namesTable = offset(BitConverter.ToInt32(image, export + 32));
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int at = offset(BitConverter.ToInt32(image, namesTable + i * 4));
                int end = Array.IndexOf(image, (byte)0, at);
                names.Add(Encoding.ASCII.GetString(image, at, end - at));
            }
            return names;
        }
    }
}
