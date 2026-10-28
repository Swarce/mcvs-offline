using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.Patcher.Dex
{
    /// <summary>
    /// The control flow of one decoded method, for the patcher's safety checks: which instructions can follow which (branches,
    /// switches and the exception edges of the tries), whether a register's value at some point can still be read, and which
    /// instructions can have written the value a register holds at some point.
    /// <para>
    /// Exception edges are taken conservatively: every instruction inside a try may jump to each of its handlers, and the
    /// register state at a handler is the one before that instruction (a throwing instruction writes nothing). So both
    /// questions answer "maybe" whenever a path could exist, which only makes the checks refuse more often.
    /// </para>
    /// </summary>
    public sealed class DexFlow
    {
        private readonly DexFile.CodeItem code;
        private readonly Dictionary<int, DexInstruction> byPc;
        private readonly Dictionary<int, List<int>> handlersOf = new Dictionary<int, List<int>>();       // pc -> handler addresses
        private readonly Dictionary<int, HashSet<int>> normalPreds = new Dictionary<int, HashSet<int>>();      // pc -> instructions that continue at pc
        private readonly Dictionary<int, HashSet<int>> exceptionPreds = new Dictionary<int, HashSet<int>>();   // handler pc -> instructions in its tries

        public DexFlow(DexFile.CodeItem code, IReadOnlyList<DexInstruction> instructions)
        {
            this.code = code;
            Instructions = instructions;
            byPc = instructions.ToDictionary(i => i.Pc);
            CheckOffsets();
            foreach (DexFile.TryItem t in code.Tries)
            {
                List<int> handlers = code.Handlers[t.HandlerOff].Addresses.Distinct().ToList();
                foreach (DexInstruction i in instructions.Where(x => x.Pc >= t.StartAddr && x.Pc < t.StartAddr + t.InsnCount && !x.IsPayload))
                {
                    if (!handlersOf.TryGetValue(i.Pc, out List<int> list)) handlersOf[i.Pc] = list = new List<int>();
                    foreach (int h in handlers.Where(h => !list.Contains(h)))
                    {
                        list.Add(h);
                        Add(exceptionPreds, h, i.Pc);
                    }
                }
            }
            foreach (DexInstruction i in instructions.Where(x => !x.IsPayload))
            {
                foreach (int s in i.Successors(byPc).Distinct()) Add(normalPreds, s, i.Pc);
            }
        }

        public IReadOnlyList<DexInstruction> Instructions { get; }

        public IReadOnlyDictionary<int, DexInstruction> ByPc { get { return byPc; } }

        /// <summary>The handler addresses an exception at <paramref name="pc"/> can reach.</summary>
        public IReadOnlyList<int> HandlersAt(int pc)
        {
            return handlersOf.TryGetValue(pc, out List<int> list) ? list : (IReadOnlyList<int>)new int[0];
        }

        /// <summary>The pcs of the instructions reachable from the method entry (branches, switches and exception edges).</summary>
        public HashSet<int> Reachable()
        {
            var seen = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            while (work.Count > 0)
            {
                int at = work.Pop();
                if (!byPc.TryGetValue(at, out DexInstruction i) || i.IsPayload || !seen.Add(at)) continue;
                foreach (int h in HandlersAt(at)) work.Push(h);
                foreach (int s in i.Successors(byPc)) work.Push(s);
            }
            return seen;
        }

        /// <summary>
        /// True when the value <paramref name="register"/> holds just before the instruction at <paramref name="pc"/> can be
        /// read on some path from there (it is read before it is overwritten); <paramref name="reader"/> is one such read.
        /// </summary>
        public bool IsLiveBefore(int register, int pc, out DexInstruction reader)
        {
            reader = null;
            var seen = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(pc);
            while (work.Count > 0)
            {
                int at = work.Pop();
                if (!seen.Add(at)) continue;
                if (!byPc.TryGetValue(at, out DexInstruction i) || i.IsPayload) continue;   // past the end or into data: no execution
                if (i.Reads.Contains(register))
                {
                    reader = i;
                    return true;
                }
                foreach (int h in HandlersAt(at)) work.Push(h);
                if (i.Writes.Contains(register)) continue;
                foreach (int s in i.Successors(byPc)) work.Push(s);
            }
            return false;
        }

        /// <summary>
        /// The instructions whose write of <paramref name="register"/> can be the value it holds just before the instruction at
        /// <paramref name="pc"/>; <paramref name="fromEntry"/> is set when some path from the method entry reaches
        /// <paramref name="pc"/> without a write (a parameter, or an undefined register).
        /// </summary>
        public List<DexInstruction> ReachingWrites(int register, int pc, out bool fromEntry)
        {
            fromEntry = false;
            var writes = new List<DexInstruction>();
            var seen = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(pc);
            while (work.Count > 0)
            {
                int at = work.Pop();
                if (!seen.Add(at)) continue;
                if (at == 0) fromEntry = true;
                if (normalPreds.TryGetValue(at, out HashSet<int> preds))
                {
                    foreach (int p in preds)
                    {
                        DexInstruction w = byPc[p];
                        if (w.Writes.Contains(register))
                        {
                            if (!writes.Contains(w)) writes.Add(w);
                        }
                        else
                        {
                            work.Push(p);
                        }
                    }
                }
                // Entering a handler: the state is the one before the instruction that threw.
                if (exceptionPreds.TryGetValue(at, out HashSet<int> throwers))
                {
                    foreach (int p in throwers) work.Push(p);
                }
            }
            return writes.OrderBy(w => w.Pc).ToList();
        }

        /// <summary>
        /// Every branch target, switch target and payload reference starts an instruction (payloads only where a payload is
        /// expected), the last instruction cannot fall through past the end, and every try range and handler address lines up
        /// with instruction starts. Throws <see cref="InvalidDataException"/> otherwise.
        /// </summary>
        private void CheckOffsets()
        {
            int length = code.Insns.Length;
            foreach (DexInstruction i in Instructions.Where(x => !x.IsPayload))
            {
                bool payloadRef = i.Opcode == 0x26 || i.IsSwitch;
                foreach (int target in i.ReferencedOffsets(byPc))
                {
                    if (!byPc.TryGetValue(target, out DexInstruction at))
                        throw new InvalidDataException(i.Name + " at " + DexInstruction.Hex(i.Pc) + " refers to " + DexInstruction.Hex(target) + ", not an instruction start");
                    bool isPayloadRef = payloadRef && target == i.Pc + i.BranchOffset;
                    if (at.IsPayload != isPayloadRef)
                        throw new InvalidDataException(i.Name + " at " + DexInstruction.Hex(i.Pc) + " refers to " + DexInstruction.Hex(target) +
                                                       (at.IsPayload ? ", a payload" : ", not its payload"));
                }
            }
            DexInstruction last = Instructions.LastOrDefault(x => !x.IsPayload);
            if (last != null && last.Pc + last.Size >= length && last.Successors(byPc).Contains(last.Pc + last.Size))
                throw new InvalidDataException("the last instruction (" + last.Name + " at " + DexInstruction.Hex(last.Pc) + ") falls through past the end");
            foreach (DexFile.TryItem t in code.Tries)
            {
                int end = t.StartAddr + t.InsnCount;
                if (!byPc.ContainsKey(t.StartAddr) || (end != length && !byPc.ContainsKey(end)))
                    throw new InvalidDataException("try " + DexInstruction.Hex(t.StartAddr) + "-" + DexInstruction.Hex(end) + " does not line up with instructions");
                foreach (int h in code.Handlers[t.HandlerOff].Addresses)
                {
                    if (!byPc.TryGetValue(h, out DexInstruction at) || at.IsPayload)
                        throw new InvalidDataException("handler " + DexInstruction.Hex(h) + " is not an instruction start");
                }
            }
        }

        private static void Add(Dictionary<int, HashSet<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out HashSet<int> set)) map[key] = set = new HashSet<int>();
            set.Add(value);
        }
    }
}
