using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Diagnostics;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Our P/Invokes run on every ABI the APK ships (arm64-v8a, armeabi-v7a) with one managed DLL, so their C# types must
    /// match the C prototype on 32-bit as well as 64-bit: a C# long is 64-bit everywhere, while off_t and ssize_t are 32-bit
    /// on armeabi-v7a, where a long argument goes into a register pair the callee does not read (review of b023bbb:
    /// lseek(int, long, int) moved the dump file's offset to an arbitrary value).
    /// </summary>
    public class PInvokeAbiTests
    {
        /// <summary>The only 64-bit C# values allowed in a P/Invoke: off64_t of the *64 entry points (-1 = the return).</summary>
        private static readonly HashSet<string> Off64 = new HashSet<string>(StringComparer.Ordinal)
        {
            "lseek64:-1", "lseek64:1", "pread64:3", "pwrite64:3", "ftruncate64:1",
        };

        /// <summary>Functions with a plain off_t (32-bit on armeabi-v7a): never bound, the *64 variant is.</summary>
        private static readonly HashSet<string> OffT = new HashSet<string>(StringComparer.Ordinal)
        {
            "lseek", "pread", "pwrite", "ftruncate", "truncate", "mmap", "sendfile", "fallocate", "posix_fadvise", "lockf",
        };

        /// <summary>Functions returning ssize_t (pointer-sized): IntPtr.</summary>
        private static readonly HashSet<string> SsizeT = new HashSet<string>(StringComparer.Ordinal)
        {
            "read", "write", "pread64", "pwrite64", "readlink", "recv", "send", "recvfrom", "sendto",
        };

        /// <summary>
        /// The Windows imports the net8 test assembly compiles in (hosts/embedded/Windows/, docs/design/pc-port.md §12.2): not Android
        /// libraries, so the Android ABI rules do not apply to them (PcPInvokeTests checks them against the x86 exports).
        /// </summary>
        private static readonly HashSet<string> WindowsModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kernel32", "user32", "steam_api",
        };

        private readonly ITestOutputHelper output;

        public PInvokeAbiTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void EveryPInvokeIsTypedForEveryShippedAbi()
        {
            var assemblies = new List<string> { typeof(ThreadDump).Assembly.Location, typeof(McvsOffline.Server.Core.Http.HttpServer).Assembly.Location };
            string embedded = TestEnvironment.EmbeddedServerDll;
            if (embedded != null) assemblies.Add(embedded);
            else Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") != "1", "MCVS_REQUIRE_EMBEDDED=1 but the embedded DLL is missing");

            var problems = new List<string>();
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in assemblies)
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true }))
                {
                    foreach (TypeDefinition type in AllTypes(module))
                    {
                        if (!type.FullName.StartsWith("McvsOffline.", StringComparison.Ordinal)) continue;
                        foreach (MethodDefinition m in type.Methods.Where(x => x.IsPInvokeImpl))
                        {
                            string entry = m.PInvokeInfo.EntryPoint ?? m.Name;
                            string where = Path.GetFileName(path) + " " + type.FullName + "." + m.Name + " -> " + m.PInvokeInfo.Module.Name + "!" + entry;
                            seen.Add(Path.GetFileName(path) + ": " + m.PInvokeInfo.Module.Name + "!" + entry);
                            if (WindowsModules.Contains(m.PInvokeInfo.Module.Name)) continue;
                            if (OffT.Contains(entry)) problems.Add(where + ": takes a 32-bit off_t on armeabi-v7a, bind " + entry + "64");
                            if (Is64(m.ReturnType) && !Off64.Contains(entry + ":-1")) problems.Add(where + ": 64-bit return");
                            for (int i = 0; i < m.Parameters.Count; i++)
                            {
                                if (Is64(m.Parameters[i].ParameterType) && !Off64.Contains(entry + ":" + i))
                                    problems.Add(where + ": parameter " + i + " (" + m.Parameters[i].Name + ") is 64-bit");
                            }
                            if (SsizeT.Contains(entry) && m.ReturnType.FullName != "System.IntPtr")
                                problems.Add(where + ": returns ssize_t, declared " + m.ReturnType.FullName);
                        }
                    }
                }
            }
            output.WriteLine(string.Join("\n", seen));
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.Contains(seen, s => s.EndsWith("libc!lseek64", StringComparison.Ordinal));   // the scan saw ThreadDump's bindings
            Assert.Contains(seen, s => s.EndsWith("libc!pread64", StringComparison.Ordinal));
            Assert.Contains(seen, s => s.EndsWith("kernel32!GetCurrentThreadId", StringComparison.Ordinal));   // the filter had something to skip
            if (embedded != null) Assert.Contains(seen, s => s.StartsWith("McvsOffline.Server.dll: liblog.so!", StringComparison.Ordinal));
        }

        [Fact]
        public void ThreadDumpLibcBindingsUseSixtyFourBitOffsets()
        {
            string dir = TestEnvironment.NewTempDir("libc");
            string file = Path.Combine(dir, "data.bin");
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("0123456789"));
            int fd = ThreadDump.Libc.open(file, ThreadDump.O_RDONLY | ThreadDump.O_CLOEXEC, 0);
            Assert.True(fd >= 0, "open failed");
            try
            {
                Assert.Equal(10L, ThreadDump.Libc.lseek64(fd, 0, ThreadDump.SEEK_END));
                Assert.Equal(3L, ThreadDump.Libc.lseek64(fd, 3, ThreadDump.SEEK_SET));
                var buffer = new byte[3];
                Assert.Equal(new IntPtr(3), ThreadDump.Libc.pread64(fd, buffer, new IntPtr(3), 4));
                Assert.Equal("456", Encoding.ASCII.GetString(buffer));
                Assert.Equal(3L, ThreadDump.Libc.lseek64(fd, 0, ThreadDump.SEEK_CUR));   // pread does not move the offset
                const long Beyond4G = 5_000_000_000L;   // only survives a binding that really passes 64 bits
                Assert.Equal(Beyond4G, ThreadDump.Libc.lseek64(fd, Beyond4G, ThreadDump.SEEK_SET));
                Assert.Equal(Beyond4G, ThreadDump.Libc.lseek64(fd, 0, ThreadDump.SEEK_CUR));
                Assert.Equal(IntPtr.Zero, ThreadDump.Libc.pread64(fd, buffer, new IntPtr(3), Beyond4G));   // past the end: 0 bytes
            }
            finally
            {
                ThreadDump.Libc.close(fd);
            }

            string link = Path.Combine(dir, "link");
            File.CreateSymbolicLink(link, file);
            var target = new byte[512];
            IntPtr n = ThreadDump.Libc.readlink(link, target, new IntPtr(target.Length));
            Assert.Equal(file, Encoding.UTF8.GetString(target, 0, n.ToInt32()));
            Assert.Equal(new IntPtr(-1), ThreadDump.Libc.readlink(Path.Combine(dir, "missing"), target, new IntPtr(target.Length)));
        }

        [Fact]
        public void TheNativeStackScanOnlyRunsInSixtyFourBitProcesses()
        {
            Assert.True(ThreadDump.StackScanSupported("arm64", 8));
            Assert.True(ThreadDump.StackScanSupported("x86_64", 8));
            Assert.False(ThreadDump.StackScanSupported("arm", 4));      // armeabi-v7a: 4-byte words, Thumb/ARM calls
            Assert.False(ThreadDump.StackScanSupported("arm64", 4));
            Assert.False(ThreadDump.StackScanSupported("?", 8));
        }

        private static bool Is64(TypeReference t)
        {
            return t.FullName == "System.Int64" || t.FullName == "System.UInt64";
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                TypeDefinition t = stack.Pop();
                yield return t;
                foreach (TypeDefinition n in t.NestedTypes) stack.Push(n);
            }
        }
    }
}
