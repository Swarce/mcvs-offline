using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher.Dex;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>Whole-file scans of a dex for the targetsdk tests: PendingIntent creation sites and their flags, and call sites.</summary>
    internal static class DexScan
    {
        public const int FlagImmutable = 0x04000000, FlagMutable = 0x02000000;

        private static readonly string[] Creators = { "getActivity", "getActivities", "getBroadcast", "getService", "getForegroundService" };

        /// <summary>One PendingIntent.get* call and what its flags argument can hold there.</summary>
        public sealed class PendingIntentSite
        {
            public string Dex;
            public string Method;     // "Lcls;->name(...)R"
            public int Pc;
            public string Call;       // getActivity, getBroadcast, ...
            public int FlagsRegister;

            /// <summary>The constant flag values that can reach the call; null when a non-constant value (or a parameter) can.</summary>
            public List<long> Flags;

            /// <summary>Every value that can reach the call is a constant with FLAG_IMMUTABLE or FLAG_MUTABLE set.</summary>
            public bool HasMutabilityFlag
            {
                get { return Flags != null && Flags.Count > 0 && Flags.All(f => (f & (FlagImmutable | FlagMutable)) != 0); }
            }

            public string Key { get { return Method + " " + DexInstruction.Hex(Pc); } }

            public override string ToString()
            {
                return Dex + " " + Key + " " + Call + " flags v" + FlagsRegister + "=" +
                       (Flags == null ? "(not a constant)" : string.Join("|", Flags.Select(f => "0x" + f.ToString("x"))));
            }
        }

        /// <summary>Calls every method with code; <paramref name="visit"/> gets the method and its decoded instructions.</summary>
        public static void ForEachMethod(DexFile dex, Action<DexFile.Method, DexFile.CodeItem, List<DexInstruction>> visit)
        {
            foreach (DexFile.ClassDef cls in dex.Classes())
            {
                foreach (DexFile.Method m in dex.GetMethods(cls).Where(m => m.CodeOff != 0))
                {
                    DexFile.CodeItem code = dex.GetCode(m);
                    visit(m, code, DexInstruction.DecodeAll(code.Insns));
                }
            }
        }

        /// <summary>
        /// Every static call of PendingIntent.getActivity/getActivities/getBroadcast/getService/getForegroundService (each takes
        /// its flags as the fourth argument), with the values its flags register can hold at the call (reaching writes over the
        /// method's control flow, exception edges included).
        /// </summary>
        public static List<PendingIntentSite> PendingIntentSites(DexFile dex)
        {
            var sites = new List<PendingIntentSite>();
            ForEachMethod(dex, (method, code, instructions) =>
            {
                DexFlow flow = null;
                foreach (DexInstruction i in instructions.Where(x => x.Opcode == 0x71 || x.Opcode == 0x77))
                {
                    string target = dex.GetMethodRef(i.Index);
                    if (!target.StartsWith("Landroid/app/PendingIntent;->", StringComparison.Ordinal)) continue;
                    string name = target.Substring(target.IndexOf("->", StringComparison.Ordinal) + 2);
                    name = name.Substring(0, name.IndexOf('('));
                    if (!Creators.Contains(name)) continue;
                    flow = flow ?? new DexFlow(code, instructions);
                    int register = i.Args[3];
                    List<DexInstruction> writes = flow.ReachingWrites(register, i.Pc, out bool fromEntry);
                    bool constant = !fromEntry && writes.Count > 0 && writes.All(w => w.Opcode >= 0x12 && w.Opcode <= 0x15);
                    sites.Add(new PendingIntentSite
                    {
                        Dex = dex.Name, Method = method.Reference, Pc = i.Pc, Call = name, FlagsRegister = register,
                        Flags = constant ? writes.Select(w => w.Literal).Distinct().OrderBy(v => v).ToList() : null,
                    });
                }
            });
            return sites;
        }

        /// <summary>The methods (by reference) that contain an invoke whose target satisfies <paramref name="match"/>.</summary>
        public static SortedSet<string> Callers(DexFile dex, Func<string, bool> match)
        {
            var callers = new SortedSet<string>(StringComparer.Ordinal);
            ForEachMethod(dex, (method, code, instructions) =>
            {
                foreach (DexInstruction i in instructions.Where(x => x.IsInvoke && x.IndexKind == DexIndexKind.Method))
                {
                    if (match(dex.GetMethodRef(i.Index))) callers.Add(method.Reference);
                }
            });
            return callers;
        }
    }
}
