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

        /// <summary>
        /// A conservative reachability (rapid type analysis) over the Java code of several dex files, from entry classes the
        /// system instantiates on its own (manifest components, the Application class). A class is instantiated by an entry,
        /// a reached new-instance, or a reached const-class (an explicit intent, a JobInfo, a reflective start). A method is
        /// reached when it is the target of a reached invoke-static/direct/super (resolved through the in-dex superclass
        /// chain), the implementation in an instantiated subtype of a reached invoke-virtual/interface, a static initializer of
        /// a class reached code uses, or a method the framework can call on an instantiated object: every instance method of a
        /// class with a supertype outside the dex files other than java.lang.Object (a component, a Runnable, a Handler; which
        /// of them overrides a framework method is not known here), and the Object overrides (toString, equals, hashCode,
        /// finalize) of the others. Inside a method only the instructions reachable from its entry count (a method that now
        /// starts with return-void reaches nothing). Not seen: classes instantiated by name strings (Class.forName, layouts)
        /// and calls from native or managed code.
        /// </summary>
        public sealed class Reachability
        {
            private const string JavaObject = "Ljava/lang/Object;";
            private const uint AccStatic = 0x0008;
            private static readonly string[] ObjectMethods = { "toString()Ljava/lang/String;", "equals(Ljava/lang/Object;)Z", "hashCode()I", "finalize()V" };

            private readonly Dictionary<string, (DexFile Dex, DexFile.ClassDef Class, List<DexFile.Method> Methods)> classes =
                new Dictionary<string, (DexFile, DexFile.ClassDef, List<DexFile.Method>)>(StringComparer.Ordinal);
            private readonly Dictionary<string, HashSet<string>> supertypes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            private readonly HashSet<string> instantiated = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> initialized = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<(string Owner, string Signature)> virtualCalls = new HashSet<(string, string)>();
            private readonly Queue<(DexFile Dex, DexFile.Method Method)> work = new Queue<(DexFile, DexFile.Method)>();

            /// <summary>Reached method reference -> the reference it was reached from ("entry: ..." for an entry class).</summary>
            public Dictionary<string, string> Reached { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public Reachability(IEnumerable<DexFile> dexFiles)
            {
                foreach (DexFile dex in dexFiles)
                {
                    foreach (DexFile.ClassDef cls in dex.Classes())
                    {
                        if (!classes.ContainsKey(cls.Descriptor)) classes[cls.Descriptor] = (dex, cls, dex.GetMethods(cls));
                    }
                }
            }

            public bool Defines(string descriptor)
            {
                return classes.ContainsKey(descriptor);
            }

            /// <summary>An entry class: the system instantiates it and calls its methods.</summary>
            public void AddEntry(string descriptor, string why)
            {
                Instantiate(descriptor, "entry: " + why);
            }

            /// <summary>The type itself and all its supertypes (superclasses and interfaces, transitively; types outside the dex files included).</summary>
            private HashSet<string> Supertypes(string descriptor)
            {
                if (supertypes.TryGetValue(descriptor, out HashSet<string> set)) return set;
                set = new HashSet<string>(StringComparer.Ordinal) { descriptor };
                supertypes[descriptor] = set;
                if (classes.TryGetValue(descriptor, out var c))
                {
                    foreach (string parent in (c.Class.Superclass == null ? new string[0] : new[] { c.Class.Superclass }).Concat(c.Class.Interfaces))
                        set.UnionWith(Supertypes(parent));
                }
                return set;
            }

            private void Instantiate(string descriptor, string from)
            {
                if (!classes.ContainsKey(descriptor) || !instantiated.Add(descriptor)) return;
                Initialize(descriptor, from);
                HashSet<string> types = Supertypes(descriptor);
                bool framework = types.Any(t => t != JavaObject && !classes.ContainsKey(t));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (string d = descriptor; d != null && classes.TryGetValue(d, out var c); d = c.Class.Superclass)
                {
                    foreach (DexFile.Method m in c.Methods.Where(m => (m.AccessFlags & AccStatic) == 0 && m.Name != "<init>"))
                    {
                        string signature = m.Name + m.Descriptor;
                        if (seen.Add(signature) && (framework || ObjectMethods.Contains(signature))) Reach(c.Dex, m, from);
                    }
                }
                foreach (var call in virtualCalls.Where(v => types.Contains(v.Owner)).ToList()) Dispatch(descriptor, call.Signature, from);
            }

            private void Initialize(string descriptor, string from)
            {
                for (string d = descriptor; d != null && classes.TryGetValue(d, out var c); d = c.Class.Superclass)
                {
                    if (!initialized.Add(d)) break;
                    foreach (DexFile.Method m in c.Methods.Where(m => m.Name == "<clinit>")) Reach(c.Dex, m, from);
                }
            }

            private void Reach(DexFile dex, DexFile.Method method, string from)
            {
                if (Reached.ContainsKey(method.Reference)) return;
                Reached[method.Reference] = from;
                work.Enqueue((dex, method));
            }

            /// <summary>Reaches the method <paramref name="signature"/> resolves to from <paramref name="descriptor"/> up its in-dex superclass chain.</summary>
            private void Dispatch(string descriptor, string signature, string from)
            {
                for (string d = descriptor; d != null && classes.TryGetValue(d, out var c); d = c.Class.Superclass)
                {
                    DexFile.Method m = c.Methods.FirstOrDefault(x => x.Name + x.Descriptor == signature);
                    if (m == null) continue;
                    Reach(c.Dex, m, from);
                    return;
                }
            }

            public void Run()
            {
                while (work.Count > 0)
                {
                    (DexFile dex, DexFile.Method method) = work.Dequeue();
                    if (method.CodeOff == 0) continue;
                    DexFile.CodeItem code = dex.GetCode(method);
                    var flow = new DexFlow(code, DexInstruction.DecodeAll(code.Insns));
                    HashSet<int> live = flow.Reachable();
                    string from = method.Reference;
                    foreach (DexInstruction i in flow.Instructions.Where(x => live.Contains(x.Pc) && !x.IsPayload))
                    {
                        if (i.IsInvoke && i.IndexKind == DexIndexKind.Method)
                        {
                            string target = dex.GetMethodRef(i.Index);
                            int arrow = target.IndexOf("->", StringComparison.Ordinal);
                            string owner = target.Substring(0, arrow), signature = target.Substring(arrow + 2);
                            switch (i.Opcode)
                            {
                                case 0x6e: case 0x72: case 0x74: case 0x78:   // invoke-virtual, invoke-interface (and /range)
                                    if (virtualCalls.Add((owner, signature)))
                                    {
                                        foreach (string x in instantiated.Where(x => Supertypes(x).Contains(owner)).ToList()) Dispatch(x, signature, from);
                                    }
                                    break;
                                case 0x71: case 0x77:                         // invoke-static
                                    Initialize(owner, from);
                                    Dispatch(owner, signature, from);
                                    break;
                                default:                                      // invoke-direct, invoke-super
                                    Dispatch(owner, signature, from);
                                    break;
                            }
                        }
                        else if (i.Opcode == 0x22 || i.Opcode == 0x1c)   // new-instance, const-class
                        {
                            Instantiate(dex.GetTypeDescriptor(i.Index), from);
                        }
                        else if (i.Opcode >= 0x60 && i.Opcode <= 0x6d)   // sget*, sput*
                        {
                            string field = dex.GetFieldRef(i.Index);
                            Initialize(field.Substring(0, field.IndexOf("->", StringComparison.Ordinal)), from);
                        }
                    }
                }
            }

            /// <summary>The chain of methods from an entry to <paramref name="reference"/>, the entry first.</summary>
            public List<string> PathTo(string reference)
            {
                var path = new List<string>();
                string r = reference;
                while (r != null && path.Count < 200 && Reached.TryGetValue(r, out string from))
                {
                    path.Insert(0, r);
                    if (from.StartsWith("entry: ", StringComparison.Ordinal))
                    {
                        path.Insert(0, from);
                        break;
                    }
                    r = from;
                }
                return path;
            }
        }
    }
}
