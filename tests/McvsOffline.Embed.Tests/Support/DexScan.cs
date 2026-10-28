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

        /// <summary>What an Intent holds that decides which activity it starts and whether it matches a task's root intent.</summary>
        public sealed class IntentValue
        {
            public string Action;
            public SortedSet<string> Categories = new SortedSet<string>(StringComparer.Ordinal);
            public string Component;   // "package/class"

            public override string ToString()
            {
                return "action " + (Action ?? "(none)") + ", categories {" + string.Join(", ", Categories) + "}, component " + (Component ?? "(none)");
            }
        }

        private sealed class ComponentValue
        {
            public string Package, Class;
        }

        /// <summary>
        /// Runs the straight-line code [<paramref name="fromPc"/>, <paramref name="toPc"/>) of a method on symbolic values and
        /// returns the Intent in <paramref name="register"/> at <paramref name="toPc"/>. Knows only what an intent-building block
        /// uses (nop, constants, new-instance, move-result-object, the Intent and ComponentName constructors, addCategory,
        /// setComponent and Intent.makeMainActivity, modelled on AOSP: new Intent(ACTION_MAIN) + setComponent +
        /// addCategory(CATEGORY_LAUNCHER)) and throws on anything else, and on a branch into the block.
        /// </summary>
        public static IntentValue StraightLineIntent(DexFile dex, IReadOnlyList<DexInstruction> instructions, int fromPc, int toPc, int register)
        {
            Dictionary<int, DexInstruction> byPc = instructions.ToDictionary(i => i.Pc);
            foreach (DexInstruction i in instructions.Where(x => !x.IsPayload))
            {
                foreach (int t in i.ReferencedOffsets(byPc).Where(t => t > fromPc && t <= toPc))
                    throw new InvalidOperationException(i.Name + " at " + DexInstruction.Hex(i.Pc) + " branches into the block at " + DexInstruction.Hex(t));
            }
            var regs = new Dictionary<int, object>();
            object result = null;
            T Get<T>(int r) where T : class => regs.TryGetValue(r, out object v) && v is T t ? t : throw new InvalidOperationException("v" + r + " holds no " + typeof(T).Name);
            for (int pc = fromPc; pc < toPc;)
            {
                DexInstruction i = byPc[pc];
                string reference = i.IndexKind == DexIndexKind.Method ? dex.GetMethodRef(i.Index) : null;
                object previous = result;   // a move-result takes the result of the instruction right before it
                result = null;
                switch (i.Name)
                {
                    case "nop":
                        break;
                    case "const/4": case "const/16": case "const": case "const/high16":
                        regs[i.A] = i.Literal;
                        break;
                    case "const-string":
                        regs[i.A] = dex.GetString(i.Index);
                        break;
                    case "new-instance" when dex.GetTypeDescriptor(i.Index) == "Landroid/content/Intent;":
                        regs[i.A] = new IntentValue();
                        break;
                    case "new-instance" when dex.GetTypeDescriptor(i.Index) == "Landroid/content/ComponentName;":
                        regs[i.A] = new ComponentValue();
                        break;
                    case "move-result-object":
                        regs[i.A] = previous ?? throw new InvalidOperationException("no result to move at " + DexInstruction.Hex(pc));
                        break;
                    case "invoke-direct" when reference == "Landroid/content/Intent;-><init>(Ljava/lang/String;)V":
                        Get<IntentValue>(i.Args[0]).Action = Get<string>(i.Args[1]);
                        break;
                    case "invoke-direct" when reference == "Landroid/content/ComponentName;-><init>(Ljava/lang/String;Ljava/lang/String;)V":
                        ComponentValue c = Get<ComponentValue>(i.Args[0]);
                        c.Package = Get<string>(i.Args[1]);
                        c.Class = Get<string>(i.Args[2]);
                        break;
                    case "invoke-virtual" when reference == "Landroid/content/Intent;->addCategory(Ljava/lang/String;)Landroid/content/Intent;":
                        Get<IntentValue>(i.Args[0]).Categories.Add(Get<string>(i.Args[1]));
                        result = regs[i.Args[0]];
                        break;
                    case "invoke-virtual" when reference == "Landroid/content/Intent;->setComponent(Landroid/content/ComponentName;)Landroid/content/Intent;":
                        ComponentValue set = Get<ComponentValue>(i.Args[1]);
                        Get<IntentValue>(i.Args[0]).Component = set.Package + "/" + set.Class;
                        result = regs[i.Args[0]];
                        break;
                    case "invoke-static" when reference == "Landroid/content/Intent;->makeMainActivity(Landroid/content/ComponentName;)Landroid/content/Intent;":
                        ComponentValue main = Get<ComponentValue>(i.Args[0]);
                        var made = new IntentValue { Action = "android.intent.action.MAIN", Component = main.Package + "/" + main.Class };
                        made.Categories.Add("android.intent.category.LAUNCHER");
                        result = made;
                        break;
                    default:
                        throw new InvalidOperationException("not modelled: " + DexInstruction.Hex(pc) + " " + i.Render(dex));
                }
                pc += i.Size;
            }
            return Get<IntentValue>(register);
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
