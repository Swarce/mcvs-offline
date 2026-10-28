using System;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// The ETC1 block search (see <see cref="Etc1"/>), one instance per thread: pinned scratch memory, no allocation per
    /// block.
    /// <list type="number">
    /// <item><b>Screening.</b> For both orientations, the subblock means quantised to 5 bits with every table (differential
    /// mode); individual mode (4 bits) only for an orientation whose two means are too far apart for the 3-bit delta.</item>
    /// <item><b>Refinement</b> of the best candidate (and of the runner-up when its screening error is within 10 %): per
    /// subblock, for the best tables of the screening, the least-squares base for the chosen modifiers (the mean minus the
    /// mean modifier) with every floor/ceiling quantisation of it, twice. A differential delta out of range is resolved by
    /// optimising one subblock inside the other's reach, both ways round.</item>
    /// </list>
    /// The error of a subblock for an expanded base (r, g, b) and modifier m at pixel p is
    /// Σc (bc + m − pc)² = Σc (bc − pc)² + 2m·sp + 3m² with sp = (r + g + b) − (pr + pg + pb) while no channel clamps, so
    /// with the subblock's sums precomputed the base term is O(1) and each pixel's best modifier follows from one number;
    /// bases whose modifiers can clamp (near 0 or 255) take the exact per-channel path.
    /// </summary>
    internal sealed unsafe class Etc1Encoder
    {
        /// <summary>A screening error at or below this (about 1 per channel and pixel) takes the short search.</summary>
        internal const int SmoothError = 48;

        private const int FloorBias = 512;

        private static readonly int[] ModifierTable = Pinned(Flatten(Etc1.Modifiers));
        private static readonly int[] SubblockTable = Pinned(Flatten(new[] { Etc1.Subblocks[0][0], Etc1.Subblocks[0][1], Etc1.Subblocks[1][0], Etc1.Subblocks[1][1] }));
        private static readonly int[] Floor4 = Pinned(BuildFloor(4));
        private static readonly int[] Floor5 = Pinned(BuildFloor(5));

        /// <summary>The block to encode: 16 pixels (y * 4 + x), R, G, B each.</summary>
        public readonly int[] Input;

        /// <summary>Tables refined per subblock (the best ones of the nearest-base pass), 1 to 3.</summary>
        internal int Tables = 2;

        // Pinned scratch, addressed through the pointers below.
        private readonly int[] memory;
        private readonly int* input;            // 48
        private readonly int* sums;             // per subblock id (flip * 2 + subblock): R, G, B, sum of squares (16)
        private readonly int* pixelSum;         // per subblock id: each pixel's R + G + B (32)
        private readonly int* trial;            // 8 modifier indices
        private readonly int* current;          // 8
        private readonly int* selectors;        // 16
        private readonly int* tableError;       // 8
        private readonly int* cache;            // per subblock id and mode: 8 table errors (64)
        private readonly int* cacheValid;       // per subblock id and mode (8)
        private readonly int* screen;           // per candidate flip * 2 + mode (4)
        private readonly double[] mean = new double[12];
        private readonly int* mods;
        private readonly int* subblocks;

        /// <summary>One subblock solution: quantised base (4 or 5 bits per channel), table, modifier indices, error.</summary>
        private struct Solution
        {
            public int R, G, B, Table, Error;
            public int Sel0, Sel1, Sel2, Sel3, Sel4, Sel5, Sel6, Sel7;
        }

        public Etc1Encoder()
        {
            Input = GC.AllocateArray<int>(48, true);
            memory = GC.AllocateArray<int>(256, true);
            fixed (int* p = Input) input = p;
            fixed (int* p = memory)
            {
                sums = p;
                pixelSum = p + 16;
                trial = p + 48;
                current = p + 56;
                selectors = p + 64;
                tableError = p + 80;
                cache = p + 88;
                cacheValid = p + 152;
                screen = p + 160;
            }
            fixed (int* p = ModifierTable) mods = p;
            fixed (int* p = SubblockTable) subblocks = p;
        }

        public void EncodeBlock(byte[] output, int offset)
        {
            for (int id = 0; id < 4; id++)
            {
                int* idx = subblocks + id * 8;
                int r = 0, g = 0, b = 0, q = 0;
                for (int i = 0; i < 8; i++)
                {
                    int* p = input + idx[i] * 3;
                    int pr = p[0], pg = p[1], pb = p[2];
                    r += pr;
                    g += pg;
                    b += pb;
                    q += pr * pr + pg * pg + pb * pb;
                    pixelSum[id * 8 + i] = pr + pg + pb;
                }
                sums[id * 4] = r;
                sums[id * 4 + 1] = g;
                sums[id * 4 + 2] = b;
                sums[id * 4 + 3] = q;
                mean[id * 3] = r / 8.0;
                mean[id * 3 + 1] = g / 8.0;
                mean[id * 3 + 2] = b / 8.0;
            }
            for (int i = 0; i < 8; i++) cacheValid[i] = 0;

            // Screening.
            for (int flip = 0; flip < 2; flip++)
            {
                int s0 = flip * 2, s1 = s0 + 1;
                int r0 = Nearest(mean[s0 * 3], 5), g0 = Nearest(mean[s0 * 3 + 1], 5), b0 = Nearest(mean[s0 * 3 + 2], 5);
                int r1 = Nearest(mean[s1 * 3], 5), g1 = Nearest(mean[s1 * 3 + 1], 5), b1 = Nearest(mean[s1 * 3 + 2], 5);
                bool fits = InDelta(r1 - r0) && InDelta(g1 - g0) && InDelta(b1 - b0);
                int e0 = Screen(s0, 1, Expand5(r0), Expand5(g0), Expand5(b0), true);
                if (!fits)
                {
                    r1 = Math.Max(r0 - 4, Math.Min(r0 + 3, r1));
                    g1 = Math.Max(g0 - 4, Math.Min(g0 + 3, g1));
                    b1 = Math.Max(b0 - 4, Math.Min(b0 + 3, b1));
                }
                screen[flip * 2 + 1] = e0 + Screen(s1, 1, Expand5(r1), Expand5(g1), Expand5(b1), fits);
                if (fits)
                {
                    screen[flip * 2] = int.MaxValue;
                }
                else
                {
                    screen[flip * 2] = Screen(s0, 0, Nearest(mean[s0 * 3], 4) * 17, Nearest(mean[s0 * 3 + 1], 4) * 17, Nearest(mean[s0 * 3 + 2], 4) * 17, true) +
                                       Screen(s1, 0, Nearest(mean[s1 * 3], 4) * 17, Nearest(mean[s1 * 3 + 1], 4) * 17, Nearest(mean[s1 * 3 + 2], 4) * 17, true);
                }
            }
            int first = 0;
            for (int i = 1; i < 4; i++)
            {
                if (screen[i] < screen[first]) first = i;
            }
            int second = first == 0 ? 1 : 0;
            for (int i = 0; i < 4; i++)
            {
                if (i != first && screen[i] < screen[second]) second = i;
            }

            // Refinement. Blocks the screening already fits almost exactly (smooth lightmaps, flat colours) take the short search.
            bool smooth = screen[first] <= SmoothError;
            int tables = smooth ? 1 : Tables;
            Solution bestA, bestB;
            Refine(first >> 1, (first & 1) == 1, tables, out bestA, out bestB);
            int bestCandidate = first;
            if (!smooth && screen[second] != int.MaxValue && bestA.Error + bestB.Error > 0 && screen[second] <= screen[first] + screen[first] / 10 + 8)
            {
                Solution a, b;
                Refine(second >> 1, (second & 1) == 1, tables, out a, out b);
                if (a.Error + b.Error < bestA.Error + bestB.Error)
                {
                    bestA = a;
                    bestB = b;
                    bestCandidate = second;
                }
            }
            Write(output, offset, bestCandidate >> 1, (bestCandidate & 1) == 1, bestA, bestB);
        }

        /// <summary>The best error over the tables for one base; the per-table errors are kept for the refinement when exact.</summary>
        private int Screen(int s, int mode, int r, int g, int b, bool keep)
        {
            int best = int.MaxValue;
            int* c = cache + (s * 2 + mode) * 8;
            for (int t = 0; t < 8; t++)
            {
                int e = Evaluate(s, r, g, b, t, null);
                c[t] = e;
                if (e < best) best = e;
            }
            cacheValid[s * 2 + mode] = keep ? 1 : 0;
            return best;
        }

        private void Refine(int flip, bool differential, int tables, out Solution a, out Solution b)
        {
            int s0 = flip * 2, s1 = s0 + 1;
            int bits = differential ? 5 : 4;
            int max = (1 << bits) - 1;
            a = Optimize(s0, bits, tables, 0, max, 0, max, 0, max);
            b = Optimize(s1, bits, tables, 0, max, 0, max, 0, max);
            if (!differential || Fits(a, b)) return;
            // The delta is out of range: optimise one subblock inside the other's reach, both ways round.
            Solution b2 = Optimize(s1, bits, tables, Math.Max(0, a.R - 4), Math.Min(max, a.R + 3), Math.Max(0, a.G - 4), Math.Min(max, a.G + 3),
                                   Math.Max(0, a.B - 4), Math.Min(max, a.B + 3));
            Solution a2 = Optimize(s0, bits, tables, Math.Max(0, b.R - 3), Math.Min(max, b.R + 4), Math.Max(0, b.G - 3), Math.Min(max, b.G + 4),
                                   Math.Max(0, b.B - 3), Math.Min(max, b.B + 4));
            // And from the screening's own pair: the first subblock at its nearest base, the second within its reach.
            int nr = Nearest(mean[s0 * 3], bits), ng = Nearest(mean[s0 * 3 + 1], bits), nb = Nearest(mean[s0 * 3 + 2], bits);
            Solution a3 = Optimize(s0, bits, tables, nr, nr, ng, ng, nb, nb);
            Solution b3 = Optimize(s1, bits, tables, Math.Max(0, nr - 4), Math.Min(max, nr + 3), Math.Max(0, ng - 4), Math.Min(max, ng + 3),
                                   Math.Max(0, nb - 4), Math.Min(max, nb + 3));
            int e1 = a.Error + b2.Error, e2 = a2.Error + b.Error, e3 = a3.Error + b3.Error;
            if (e1 <= e2 && e1 <= e3)
            {
                b = b2;
            }
            else if (e2 <= e3)
            {
                a = a2;
            }
            else
            {
                a = a3;
                b = b3;
            }
        }

        private static bool Fits(Solution a, Solution b)
        {
            return InDelta(b.R - a.R) && InDelta(b.G - a.G) && InDelta(b.B - a.B);
        }

        private static bool InDelta(int d) => d >= -4 && d <= 3;

        /// <summary>The best base (inside the given quantised ranges), table and modifier indices for one subblock.</summary>
        private Solution Optimize(int s, int bits, int tables, int rLo, int rHi, int gLo, int gHi, int bLo, int bHi)
        {
            var best = new Solution { Error = int.MaxValue };
            double ar = mean[s * 3], ag = mean[s * 3 + 1], ab = mean[s * 3 + 2];
            int nr = Nearest(ar, bits), ng = Nearest(ag, bits), nb = Nearest(ab, bits);
            int qr = Clamp(nr, rLo, rHi), qg = Clamp(ng, gLo, gHi), qb = Clamp(nb, bLo, bHi);
            int er = Expand(qr, bits), eg = Expand(qg, bits), eb = Expand(qb, bits);

            // Stage 1: the nearest base with every table (from the screening when it evaluated exactly this base).
            int mode = bits == 5 ? 1 : 0;
            bool cached = cacheValid[s * 2 + mode] != 0 && qr == nr && qg == ng && qb == nb;
            int* c = cache + (s * 2 + mode) * 8;
            for (int t = 0; t < 8; t++) tableError[t] = cached ? c[t] : Evaluate(s, er, eg, eb, t, null);
            int t1 = 0, t2 = -1, t3 = -1;
            for (int t = 1; t < 8; t++)
            {
                if (tableError[t] < tableError[t1]) { t3 = t2; t2 = t1; t1 = t; }
                else if (t2 < 0 || tableError[t] < tableError[t2]) { t3 = t2; t2 = t; }
                else if (t3 < 0 || tableError[t] < tableError[t3]) { t3 = t; }
            }

            // Stage 2: the least-squares base for the modifier choices and every floor/ceiling quantisation of it, then once
            // more from the best of those, for the best tables.
            for (int pick = 0; pick < tables && pick < 3; pick++)
            {
                int t = pick == 0 ? t1 : pick == 1 ? t2 : t3;
                int* m = mods + t * 4;
                int cr = qr, cg = qg, cb = qb;
                int e0 = Evaluate(s, er, eg, eb, t, current);
                Consider(ref best, qr, qg, qb, t, e0, current);
                if (best.Error == 0) return best;
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    int msum = 0;
                    for (int i = 0; i < 8; i++) msum += m[current[i]];
                    double shift = msum / 8.0;
                    int fr = Floor(ar - shift, bits), fg = Floor(ag - shift, bits), fb = Floor(ab - shift, bits);
                    int xr = cr, xg = cg, xb = cb, xe = int.MaxValue;
                    for (int combo = 0; combo < 8; combo++)
                    {
                        int r = Clamp(fr + (combo & 1), rLo, rHi);
                        int g = Clamp(fg + ((combo >> 1) & 1), gLo, gHi);
                        int b = Clamp(fb + ((combo >> 2) & 1), bLo, bHi);
                        int e = Evaluate(s, Expand(r, bits), Expand(g, bits), Expand(b, bits), t, trial);
                        Consider(ref best, r, g, b, t, e, trial);
                        if (e < xe)
                        {
                            xe = e;
                            xr = r;
                            xg = g;
                            xb = b;
                            for (int i = 0; i < 8; i++) current[i] = trial[i];
                        }
                    }
                    if (best.Error == 0) return best;
                    if (xr == cr && xg == cg && xb == cb) break;
                    cr = xr;
                    cg = xg;
                    cb = xb;
                }
            }
            return best;
        }

        private static void Consider(ref Solution best, int r, int g, int b, int t, int error, int* sel)
        {
            if (error >= best.Error) return;
            best.R = r;
            best.G = g;
            best.B = b;
            best.Table = t;
            best.Error = error;
            best.Sel0 = sel[0];
            best.Sel1 = sel[1];
            best.Sel2 = sel[2];
            best.Sel3 = sel[3];
            best.Sel4 = sel[4];
            best.Sel5 = sel[5];
            best.Sel6 = sel[6];
            best.Sel7 = sel[7];
        }

        /// <summary>
        /// Squared RGB error of subblock <paramref name="s"/> for an expanded base and table, choosing each pixel's best
        /// modifier (written to <paramref name="sel"/> unless null).
        /// </summary>
        private int Evaluate(int s, int r, int g, int b, int t, int* sel)
        {
            int* m = mods + t * 4;
            int small = m[0], large = m[1];
            int lo = r < g ? (r < b ? r : b) : (g < b ? g : b);
            int hi = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int* sum = sums + s * 4;
            if (lo >= large && hi + large <= 255)
            {
                int total = 8 * (r * r + g * g + b * b) - 2 * (r * sum[0] + g * sum[1] + b * sum[2]) + sum[3];
                int baseSum = r + g + b;
                int sq0 = 3 * small * small, sq1 = 3 * large * large;
                int* ps = pixelSum + s * 8;
                for (int i = 0; i < 8; i++)
                {
                    int sp2 = 2 * (baseSum - ps[i]);
                    // Modifier m costs 2m*sp + 3m^2: +small, +large, -small, -large.
                    int a0 = sp2 * small, a1 = sp2 * large;
                    int c0 = sq0 + a0, c1 = sq1 + a1, c2 = sq0 - a0, c3 = sq1 - a1;
                    int k = 0, c = c0;
                    if (c1 < c) { c = c1; k = 1; }
                    if (c2 < c) { c = c2; k = 2; }
                    if (c3 < c) { c = c3; k = 3; }
                    total += c;
                    if (sel != null) sel[i] = k;
                }
                return total;
            }
            return EvaluateClamped(s, r, g, b, m, sel);
        }

        private int EvaluateClamped(int s, int r, int g, int b, int* m, int* sel)
        {
            int* pal = stackalloc int[12];
            for (int k = 0; k < 4; k++)
            {
                int d = m[k];
                pal[k * 3] = Clamp(r + d, 0, 255);
                pal[k * 3 + 1] = Clamp(g + d, 0, 255);
                pal[k * 3 + 2] = Clamp(b + d, 0, 255);
            }
            int* idx = subblocks + s * 8;
            int total = 0;
            for (int i = 0; i < 8; i++)
            {
                int* p = input + idx[i] * 3;
                int pr = p[0], pg = p[1], pb = p[2];
                int bestK = 0, bestE = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = pal[k * 3] - pr, dg = pal[k * 3 + 1] - pg, db = pal[k * 3 + 2] - pb;
                    int e = dr * dr + dg * dg + db * db;
                    if (e < bestE)
                    {
                        bestE = e;
                        bestK = k;
                    }
                }
                total += bestE;
                if (sel != null) sel[i] = bestK;
            }
            return total;
        }

        private void Write(byte[] output, int offset, int flip, bool differential, Solution a, Solution b)
        {
            uint hi;
            if (differential)
                hi = (uint)(a.R << 27 | ((b.R - a.R) & 7) << 24 | a.G << 19 | ((b.G - a.G) & 7) << 16 | a.B << 11 | ((b.B - a.B) & 7) << 8);
            else
                hi = (uint)(a.R << 28 | b.R << 24 | a.G << 20 | b.G << 16 | a.B << 12 | b.B << 8);
            hi |= (uint)(a.Table << 5 | b.Table << 2 | (differential ? 2 : 0) | flip);
            Unpack(a, subblocks + flip * 16);
            Unpack(b, subblocks + flip * 16 + 8);
            uint lo = 0;
            for (int i = 0; i < 16; i++)
            {
                int bit = (i & 3) * 4 + (i >> 2);   // x * 4 + y
                int k = selectors[i];
                lo |= (uint)((k >> 1) & 1) << (16 + bit);
                lo |= (uint)(k & 1) << bit;
            }
            output[offset] = (byte)(hi >> 24);
            output[offset + 1] = (byte)(hi >> 16);
            output[offset + 2] = (byte)(hi >> 8);
            output[offset + 3] = (byte)hi;
            output[offset + 4] = (byte)(lo >> 24);
            output[offset + 5] = (byte)(lo >> 16);
            output[offset + 6] = (byte)(lo >> 8);
            output[offset + 7] = (byte)lo;
        }

        private void Unpack(Solution s, int* idx)
        {
            selectors[idx[0]] = s.Sel0;
            selectors[idx[1]] = s.Sel1;
            selectors[idx[2]] = s.Sel2;
            selectors[idx[3]] = s.Sel3;
            selectors[idx[4]] = s.Sel4;
            selectors[idx[5]] = s.Sel5;
            selectors[idx[6]] = s.Sel6;
            selectors[idx[7]] = s.Sel7;
        }

        private static int Expand(int q, int bits) => bits == 5 ? (q << 3) | (q >> 2) : q * 17;

        private static int Expand5(int q) => (q << 3) | (q >> 2);

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The largest quantised value whose expansion is at most <paramref name="v"/> (0 when none).</summary>
        private static int Floor(double v, int bits)
        {
            int i = (int)Math.Floor(v) + FloorBias;
            int[] table = bits == 5 ? Floor5 : Floor4;
            return table[i < 0 ? 0 : i >= table.Length ? table.Length - 1 : i];
        }

        private static int Nearest(double v, int bits)
        {
            int max = (1 << bits) - 1;
            int f = Floor(v, bits);
            if (f < max && Expand(f + 1, bits) - v < v - Expand(f, bits)) return f + 1;
            return f;
        }

        private static int[] BuildFloor(int bits)
        {
            int max = (1 << bits) - 1;
            var table = new int[FloorBias + 256 + FloorBias];
            for (int i = 0; i < table.Length; i++)
            {
                int v = i - FloorBias;
                int q = 0;
                while (q < max && Expand(q + 1, bits) <= v) q++;
                table[i] = q;
            }
            return table;
        }

        private static int[] Flatten(int[][] rows)
        {
            int n = 0;
            foreach (int[] r in rows) n += r.Length;
            var flat = new int[n];
            int o = 0;
            foreach (int[] r in rows)
            {
                Array.Copy(r, 0, flat, o, r.Length);
                o += r.Length;
            }
            return flat;
        }

        private static int[] Pinned(int[] values)
        {
            int[] pinned = GC.AllocateArray<int>(values.Length, true);
            Array.Copy(values, pinned, values.Length);
            return pinned;
        }
    }
}
