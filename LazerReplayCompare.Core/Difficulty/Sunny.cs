namespace LazerReplayCompare;

/// <summary>A chart's Sunny star rating, its share of hold notes and its key count.</summary>
public sealed record SunnyResult(double Star, double LnRatio, int Columns);

/// <summary>
/// The Sunny Rework star rating for osu!mania, ported from osumania_map_analyser's
/// js/rework/sunnyAlgorithm.js and js/rework/reworkMathCore.js (MIT, Leo_Black; the algorithm is
/// sunnyxxy's Star-Rating-Rebirth). The steps, their order and every constant follow the JS so the
/// numbers match it; only the star is computed (no graph, no PP metrics).
/// </summary>
public static class Sunny
{
    private readonly record struct N(int K, double H, double T);

    private static readonly double[] target_percentiles = [0.945, 0.935, 0.925, 0.915, 0.845, 0.835, 0.825, 0.815];

    private static readonly double[][] cross_matrix =
    [
        [-1],
        [0.075, 0.075],
        [0.125, 0.05, 0.125],
        [0.125, 0.125, 0.125, 0.125],
        [0.175, 0.25, 0.05, 0.25, 0.175],
        [0.175, 0.25, 0.175, 0.175, 0.25, 0.175],
        [0.225, 0.35, 0.25, 0.05, 0.25, 0.35, 0.225],
        [0.225, 0.35, 0.25, 0.225, 0.225, 0.25, 0.35, 0.225],
        [0.275, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.275],
        [0.275, 0.45, 0.35, 0.25, 0.275, 0.275, 0.25, 0.35, 0.45, 0.275],
        [0.325, 0.55, 0.45, 0.35, 0.25, 0.05, 0.25, 0.35, 0.45, 0.55, 0.325],
    ];

    /// <summary>
    /// The star rating at <paramref name="speedRate"/>. <paramref name="odFlag"/>: null for the map's
    /// OD, "HR" / "EZ", or an OD to use instead (Difficulty Adjust). Null when the file isn't a
    /// mania chart or can't be read. <paramref name="cvtFlag"/>: "HO" / "IN" to rate the chart Hold Off or Invert makes.
    /// </summary>
    public static SunnyResult? Calculate(string osuText, double speedRate = 1.0, string? odFlag = null, bool classicMod = false, string? cvtFlag = null) =>
        Calculate(SunnyParser.Parse(osuText), speedRate, odFlag, classicMod, cvtFlag);

    public static SunnyResult? Calculate(SunnyParser p, double speedRate = 1.0, string? odFlag = null, bool classicMod = false, string? cvtFlag = null)
    {
        if (p.Status != "OK") return null;
        p = p.Converted(cvtFlag);
        var lnRatio = p.LnRatio;
        var columnCount = p.ColumnCount;

        // ------------------------------------------------ preprocessFile
        double od;
        if (odFlag == null) od = p.Od;
        else if (odFlag == "HR") od = 6.462 + 0.715 * p.Od;
        else if (odFlag == "EZ") od = -20.761 + 2.566 * p.Od;
        else
        {
            od = SunnyParser.ParseFloat(odFlag);
            if (!double.IsFinite(od)) od = p.Od;
        }
        var timeScale = speedRate != 0 ? 1 / speedRate : 1;
        var seq = new List<N>(p.Columns.Count);
        for (var i = 0; i < p.Columns.Count; i++)
        {
            var h = Math.Floor(p.NoteStarts[i] * timeScale);
            var t = (p.NoteTypes[i] & 128) != 0 ? p.NoteEnds[i] : -1;
            t = t >= 0 ? Math.Floor(t * timeScale) : t;
            seq.Add(new N(p.Columns[i], h, t));
        }
        var x = 0.3 * Math.Sqrt((64.5 - Math.Ceiling(od * 3)) / 500);
        x = Math.Min(x, 0.6 * (x - 0.09) + 0.09);
        // Stable, by head then lane; the earliest note is dropped (as the C# original's loop from 1)
        var noteSeq = seq.OrderBy(n => n.H).ThenBy(n => n.K).ToList();
        if (noteSeq.Count > 0) noteSeq.RemoveAt(0);

        var K = columnCount;
        if (noteSeq.Count == 0 || K <= 0) return null;
        var byColumn = Enumerable.Range(0, K).Select(_ => new List<N>()).ToArray();
        foreach (var n in noteSeq)
            if (n.K >= 0 && n.K < K) byColumn[n.K].Add(n);
        var lnSeq = noteSeq.Where(n => n.T >= 0).ToList();
        var tailSeq = lnSeq.OrderBy(n => n.T).ToList();
        var T = Math.Max(noteSeq.Max(n => n.H), noteSeq.Max(n => n.T)) + 1;

        // ------------------------------------------------ corners
        var (allCorners, baseCorners, aCorners) = Corners(T, noteSeq);
        var keyUsage = KeyUsage(K, T, noteSeq, baseCorners);
        var activeColumns = new List<int>[baseCorners.Length];
        for (var i = 0; i < baseCorners.Length; i++)
        {
            var active = new List<int>();
            for (var k = 0; k < K; k++)
                if (keyUsage[k][i]) active.Add(k);
            activeColumns[i] = active;
        }
        var anchor = Anchor(K, KeyUsage400(K, T, noteSeq, baseCorners), baseCorners);

        var (deltaKs, jbarBase) = Jbar(K, x, byColumn, baseCorners);
        var jbar = InterpValues(allCorners, baseCorners, jbarBase);
        var xbar = InterpValues(allCorners, baseCorners, Xbar(K, x, byColumn, activeColumns, baseCorners));
        var lnRep = LnBodies(lnSeq, T);
        var pbar = InterpValues(allCorners, baseCorners, Pbar(x, noteSeq, lnRep, anchor, baseCorners));
        var abar = InterpValues(allCorners, aCorners, Abar(K, activeColumns, deltaKs, aCorners, baseCorners));
        var rbar = InterpValues(allCorners, baseCorners, Rbar(K, x, byColumn, tailSeq, baseCorners));
        var (cStep, cStepV2, ksStep) = CAndKs(K, noteSeq, keyUsage, baseCorners);
        var cArr = StepInterp(allCorners, baseCorners, cStep);
        var cArrV2 = StepInterp(allCorners, baseCorners, cStepV2);
        var ksArr = StepInterp(allCorners, baseCorners, ksStep);

        var n0 = allCorners.Length;
        var dAll = new double[n0];
        for (var i = 0; i < n0; i++)
        {
            var left = 0.4 * Math.Pow(Math.Pow(abar[i], 3 / ksArr[i]) * Math.Min(jbar[i], 8 + 0.85 * jbar[i]), 1.5);
            var right = 0.6 * Math.Pow(Math.Pow(abar[i], 2.0 / 3) * (0.8 * pbar[i] + rbar[i] * 35 / (cArr[i] + 8)), 1.5);
            var sAll = Math.Pow(left + right, 2.0 / 3);
            var tAll = Math.Pow(abar[i], 3 / ksArr[i]) * xbar[i] / (xbar[i] + sAll + 1);
            dAll[i] = 2.7 * Math.Pow(sAll, 0.5) * Math.Pow(tAll, 1.5) + sAll * 0.27;
        }

        var gaps = new double[n0];
        gaps[0] = (allCorners[1] - allCorners[0]) / 2;
        gaps[n0 - 1] = (allCorners[n0 - 1] - allCorners[n0 - 2]) / 2;
        for (var i = 1; i < n0 - 1; i++) gaps[i] = (allCorners[i + 1] - allCorners[i - 1]) / 2;

        var weightsSource = classicMod ? cArr : cArrV2;
        var effective = new double[n0];
        for (var i = 0; i < n0; i++) effective[i] = weightsSource[i] * gaps[i];
        // A stable sort by D (ties keep their order, as JS's sort does)
        var order = new int[n0];
        for (var i = 0; i < n0; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var c = dAll[a].CompareTo(dAll[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        var dSorted = order.Select(i => dAll[i]).ToArray();
        var wSorted = order.Select(i => effective[i]).ToArray();
        var cum = new double[n0];
        double running = 0;
        for (var i = 0; i < n0; i++)
        {
            running += wSorted[i];
            cum[i] = running;
        }
        var totalWeight = cum[n0 - 1];
        var norm = cum.Select(w => w / totalWeight).ToArray();
        var idx = target_percentiles.Select(t => BisectLeft(norm, t)).ToArray();
        var p93 = idx.Take(4).Select(i => dSorted[Math.Min(i, n0 - 1)]).Sum() / 4;
        var p83 = idx.Skip(4).Take(4).Select(i => dSorted[Math.Min(i, n0 - 1)]).Sum() / 4;
        double num = 0, den = 0;
        for (var i = 0; i < n0; i++)
        {
            num += Math.Pow(dSorted[i], 5) * wSorted[i];
            den += wSorted[i];
        }
        var weightedMean = Math.Pow(num / den, 1.0 / 5);

        var sr = 0.88 * p93 * 0.25 + 0.94 * p83 * 0.2 + weightedMean * 0.55;
        double lnLength = 0;
        foreach (var n in lnSeq) lnLength += Math.Min(n.T - n.H, 1000) / 200;
        var totalNotes = noteSeq.Count + 0.5 * lnLength;
        sr *= totalNotes / (totalNotes + 60);
        sr = sr <= 9 ? sr : 9 + (sr - 9) * (1 / 1.2);
        sr *= 0.975;
        // Degenerate charts (an OD past the scale, ...) give NaN or infinity; the analyser rejects
        // them too ("Invalid estimator output")
        if (!double.IsFinite(sr) || !double.IsFinite(lnRatio)) return null;
        return new SunnyResult(sr, lnRatio, columnCount);
    }

    // ------------------------------------------------ helpers (reworkMathCore.js)

    private static int BisectLeft(double[] arr, double target)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (arr[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int BisectRight(double[] arr, double target)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (arr[mid] <= target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int BisectLeft(List<double> arr, double target)
    {
        int lo = 0, hi = arr.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (arr[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double[] CumulativeSum(double[] x, double[] f)
    {
        var F = new double[x.Length];
        for (var i = 1; i < x.Length; i++) F[i] = F[i - 1] + f[i - 1] * (x[i] - x[i - 1]);
        return F;
    }

    private static double[] SmoothOnCorners(double[] x, double[] f, double window, double scale, bool average)
    {
        var F = CumulativeSum(x, f);
        var g = new double[f.Length];
        // The window's ends only move right, so each keeps its place in x instead of a binary search
        // (the same segment queryCumsum finds: the last corner at or before the end)
        int ia = 0, ib = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var s = x[i];
            var a = Math.Max(s - window, x[0]);
            var b = Math.Min(s + window, x[^1]);
            var val = Query(b, ref ib) - Query(a, ref ia);
            g[i] = average ? (b - a > 0 ? val / (b - a) : 0) : scale * val;
        }
        return g;

        double Query(double q, ref int j)
        {
            if (q <= x[0]) return 0;
            if (q >= x[^1]) return F[^1];
            while (j + 1 < x.Length && x[j + 1] <= q) j++;
            return F[j] + f[j] * (q - x[j]);
        }
    }

    private static double[] InterpValues(double[] newX, double[] oldX, double[] oldVals)
    {
        var output = new double[newX.Length];
        var idx = 0;
        for (var i = 0; i < newX.Length; i++)
        {
            var x = newX[i];
            if (x <= oldX[0])
            {
                output[i] = oldVals[0];
                continue;
            }
            if (x >= oldX[^1])
            {
                output[i] = oldVals[^1];
                continue;
            }
            while (idx + 1 < oldX.Length && oldX[idx + 1] < x) idx++;
            double x0 = oldX[idx], x1 = oldX[idx + 1], y0 = oldVals[idx], y1 = oldVals[idx + 1];
            if (x1 == x0)
            {
                output[i] = y0;
                continue;
            }
            var t = (x - x0) / (x1 - x0);
            output[i] = y0 + t * (y1 - y0);
        }
        return output;
    }

    /// <summary>Sunny's own step interpolation: an exact match takes the previous sample</summary>
    private static double[] StepInterp(double[] newX, double[] oldX, double[] oldVals)
    {
        var output = new double[newX.Length];
        var idx = 0;
        for (var i = 0; i < newX.Length; i++)
        {
            var x = newX[i];
            while (idx + 1 < oldX.Length && oldX[idx + 1] < x) idx++;
            output[i] = oldVals[Math.Clamp(idx, 0, oldVals.Length - 1)];
        }
        return output;
    }

    private static List<N> MergeByHead(List<N> a, List<N> b)
    {
        var result = new List<N>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].H <= b[j].H) result.Add(a[i++]);
            else result.Add(b[j++]);
        }
        while (i < a.Count) result.Add(a[i++]);
        while (j < b.Count) result.Add(b[j++]);
        return result;
    }

    private static double JackNerfer(double delta) => 1 - 7e-5 * Math.Pow(0.15 + Math.Abs(delta - 0.08), -4);

    // ------------------------------------------------ the steps (sunnyAlgorithm.js)

    private static (double[] All, double[] Base, double[] A) Corners(double T, List<N> noteSeq)
    {
        var times = new List<double>(noteSeq.Count * 2);
        foreach (var n in noteSeq)
        {
            times.Add(n.H);
            if (n.T >= 0) times.Add(n.T);
        }
        var heads = SortedUnique(times, 0, double.MaxValue);

        var baseList = new List<double>(heads.Length * 4 + 2);
        foreach (var s in heads)
        {
            baseList.Add(s);
            baseList.Add(s + 501);
            baseList.Add(s - 499);
            baseList.Add(s + 1);
        }
        baseList.Add(0);
        baseList.Add(T);
        var baseCorners = SortedUnique(baseList, 0, T);

        var aList = new List<double>(heads.Length * 3 + 2);
        foreach (var s in heads)
        {
            aList.Add(s);
            aList.Add(s + 1000);
            aList.Add(s - 1000);
        }
        aList.Add(0);
        aList.Add(T);
        var aCorners = SortedUnique(aList, 0, T);

        var all = new List<double>(baseCorners.Length + aCorners.Length);
        all.AddRange(baseCorners);
        all.AddRange(aCorners);
        return (SortedUnique(all, double.MinValue, double.MaxValue), baseCorners, aCorners);
    }

    /// <summary>The distinct values within [lo, hi], in order (the JS's Set, filter and sort)</summary>
    private static double[] SortedUnique(List<double> values, double lo, double hi)
    {
        values.Sort();
        var result = new List<double>(values.Count);
        foreach (var v in values)
        {
            if (v < lo || v > hi) continue;
            if (result.Count == 0 || result[^1] != v) result.Add(v);
        }
        return result.ToArray();
    }

    private static bool[][] KeyUsage(int K, double T, List<N> noteSeq, double[] baseCorners)
    {
        var usage = Enumerable.Range(0, K).Select(_ => new bool[baseCorners.Length]).ToArray();
        foreach (var n in noteSeq)
        {
            if (n.K < 0 || n.K >= K) continue;
            var start = Math.Max(n.H - 150, 0);
            var end = n.T < 0 ? n.H + 150 : Math.Min(n.T + 150, T - 1);
            var left = BisectLeft(baseCorners, start);
            var right = BisectLeft(baseCorners, end);
            for (var i = left; i < right; i++) usage[n.K][i] = true;
        }
        return usage;
    }

    private static double[][] KeyUsage400(int K, double T, List<N> noteSeq, double[] baseCorners)
    {
        var usage = Enumerable.Range(0, K).Select(_ => new double[baseCorners.Length]).ToArray();
        var coeff = 3.75 / (400.0 * 400.0);
        foreach (var n in noteSeq)
        {
            if (n.K < 0 || n.K >= K) continue;
            var start = Math.Max(n.H, 0);
            var end = n.T < 0 ? n.H : Math.Min(n.T, T - 1);
            var left400 = BisectLeft(baseCorners, start - 400);
            var left = BisectLeft(baseCorners, start);
            var right = BisectLeft(baseCorners, end);
            var right400 = BisectLeft(baseCorners, end + 400);
            var ramp = 3.75 + Math.Min(end - start, 1500) / 150;
            for (var i = left; i < right; i++) usage[n.K][i] += ramp;
            for (var i = left400; i < left; i++) usage[n.K][i] += 3.75 - coeff * Math.Pow(baseCorners[i] - start, 2);
            for (var i = right; i < right400; i++) usage[n.K][i] += 3.75 - coeff * Math.Pow(Math.Abs(baseCorners[i] - end), 2);
        }
        return usage;
    }

    private static double[] Anchor(int K, double[][] usage400, double[] baseCorners)
    {
        var anchor = new double[baseCorners.Length];
        var nonZero = new double[K];
        for (var i = 0; i < baseCorners.Length; i++)
        {
            // The lanes' non-zero values, largest first (an insertion sort: K is at most 10)
            var m = 0;
            for (var k = 0; k < K; k++)
            {
                var v = usage400[k][i];
                if (v == 0) continue;
                var at = m++;
                while (at > 0 && nonZero[at - 1] < v)
                {
                    nonZero[at] = nonZero[at - 1];
                    at--;
                }
                nonZero[at] = v;
            }
            if (m > 1)
            {
                double walk = 0, maxWalk = 0;
                for (var j = 0; j < m - 1; j++)
                {
                    walk += nonZero[j] * (1 - 4 * Math.Pow(0.5 - nonZero[j + 1] / nonZero[j], 2));
                    maxWalk += nonZero[j];
                }
                anchor[i] = walk / maxWalk;
            }
        }
        for (var i = 0; i < anchor.Length; i++) anchor[i] = 1 + Math.Min(anchor[i] - 0.18, 5 * Math.Pow(anchor[i] - 0.22, 3));
        return anchor;
    }

    private sealed record LnRep(double[] Points, double[] Cumsum, double[] Values);

    private static LnRep LnBodies(List<N> lnSeq, double T)
    {
        // A Map in insertion order; only its keys' set and sums matter
        var diff = new Dictionary<double, double>();
        void Add(double key, double v) => diff[key] = diff.GetValueOrDefault(key) + v;
        foreach (var n in lnSeq)
        {
            var t0 = Math.Min(n.H + 60, n.T);
            var t1 = Math.Min(n.H + 120, n.T);
            Add(t0, 1.3);
            Add(t1, -1.3 + 1);
            Add(n.T, -1);
        }
        var points = new HashSet<double> { 0, T };
        foreach (var k in diff.Keys) points.Add(k);
        var sorted = points.Order().ToArray();
        var values = new double[Math.Max(0, sorted.Length - 1)];
        var cumsum = new double[sorted.Length];
        double curr = 0;
        for (var i = 0; i < sorted.Length - 1; i++)
        {
            if (diff.TryGetValue(sorted[i], out var d)) curr += d;
            var v = Math.Min(curr, 2.5 + 0.5 * curr);
            values[i] = v;
            cumsum[i + 1] = cumsum[i] + (sorted[i + 1] - sorted[i]) * v;
        }
        return new LnRep(sorted, cumsum, values);
    }

    private static double LnSum(double a, double b, LnRep rep)
    {
        var i = BisectRight(rep.Points, a) - 1;
        var j = BisectRight(rep.Points, b) - 1;
        double V(int k) => k >= 0 && k < rep.Values.Length ? rep.Values[k] : double.NaN;
        if (i == j) return (b - a) * V(i);
        double total = 0;
        total += (rep.Points[i + 1] - a) * V(i);
        total += rep.Cumsum[j] - rep.Cumsum[i + 1];
        total += (b - rep.Points[j]) * V(j);
        return total;
    }

    private static (double[][] DeltaKs, double[] Jbar) Jbar(int K, double x, List<N>[] byColumn, double[] baseCorners)
    {
        var n = baseCorners.Length;
        var jks = new double[K][];
        var deltaKs = new double[K][];
        for (var k = 0; k < K; k++)
        {
            jks[k] = new double[n];
            deltaKs[k] = Enumerable.Repeat(1e9, n).ToArray();
            var notes = byColumn[k];
            for (var i = 0; i < notes.Count - 1; i++)
            {
                var start = notes[i].H;
                var end = notes[i + 1].H;
                var left = BisectLeft(baseCorners, start);
                var right = BisectLeft(baseCorners, end);
                if (left >= right) continue;
                var delta = 0.001 * (end - start);
                var val = Math.Pow(delta, -1) * Math.Pow(delta + 0.11 * Math.Pow(x, 0.25), -1);
                var jVal = val * JackNerfer(delta);
                for (var idx = left; idx < right; idx++)
                {
                    jks[k][idx] = jVal;
                    deltaKs[k][idx] = delta;
                }
            }
        }
        var jbarKs = jks.Select(j => SmoothOnCorners(baseCorners, j, 500, 0.001, false)).ToArray();
        var jbar = new double[n];
        for (var i = 0; i < n; i++)
        {
            double num = 0, den = 0;
            for (var k = 0; k < K; k++)
            {
                var w = 1 / deltaKs[k][i];
                num += Math.Pow(Math.Max(jbarKs[k][i], 0), 5) * w;
                den += w;
            }
            jbar[i] = Math.Pow(num / Math.Max(1e-9, den), 1.0 / 5);
        }
        return (deltaKs, jbar);
    }

    private static double[] Xbar(int K, double x, List<N>[] byColumn, List<int>[] activeColumns, double[] baseCorners)
    {
        var n = baseCorners.Length;
        if (K < 1 || K > 10) return new double[n];
        var xks = new double[K + 1][];
        var fastCross = new double[K + 1][];
        var cross = cross_matrix[K];
        for (var k = 0; k <= K; k++)
        {
            xks[k] = new double[n];
            fastCross[k] = new double[n];
            var pair = k == 0 ? byColumn[0] : k == K ? byColumn[K - 1] : MergeByHead(byColumn[k - 1], byColumn[k]);
            for (var i = 1; i < pair.Count; i++)
            {
                var start = pair[i - 1].H;
                var end = pair[i].H;
                var idxStart = BisectLeft(baseCorners, start);
                var idxEnd = BisectLeft(baseCorners, end);
                if (idxStart >= idxEnd) continue;
                var delta = 0.001 * (end - start);
                var val = 0.16 * Math.Pow(Math.Max(x, delta), -2);
                var leftActive = activeColumns[Math.Min(idxStart, activeColumns.Length - 1)];
                var rightActive = activeColumns[Math.Min(idxEnd, activeColumns.Length - 1)];
                if ((!leftActive.Contains(k - 1) && !rightActive.Contains(k - 1)) || (!leftActive.Contains(k) && !rightActive.Contains(k)))
                    val *= 1 - cross[k];
                var fast = Math.Max(0, 0.4 * Math.Pow(Math.Max(Math.Max(delta, 0.06), 0.75 * x), -2) - 80);
                for (var idx = idxStart; idx < idxEnd; idx++)
                {
                    xks[k][idx] = val;
                    fastCross[k][idx] = fast;
                }
            }
        }
        var xBase = new double[n];
        for (var i = 0; i < n; i++)
        {
            double sum1 = 0, sum2 = 0;
            for (var k = 0; k <= K; k++) sum1 += xks[k][i] * cross[k];
            for (var k = 0; k < K; k++) sum2 += Math.Sqrt(fastCross[k][i] * cross[k] * fastCross[k + 1][i] * cross[k + 1]);
            xBase[i] = sum1 + sum2;
        }
        return SmoothOnCorners(baseCorners, xBase, 500, 0.001, false);
    }

    private static double[] Pbar(double x, List<N> noteSeq, LnRep lnRep, double[] anchor, double[] baseCorners)
    {
        static double StreamBooster(double delta)
        {
            var expr = 7.5 / delta;
            return 160 < expr && expr < 360 ? 1 + 1.7e-7 * (expr - 160) * Math.Pow(expr - 360, 2) : 1;
        }

        var pStep = new double[baseCorners.Length];
        for (var i = 0; i < noteSeq.Count - 1; i++)
        {
            var hL = noteSeq[i].H;
            var hR = noteSeq[i + 1].H;
            var deltaTime = hR - hL;
            if (deltaTime < 1e-9)
            {
                var spike = 1000 * Math.Pow(0.02 * (4 / x - 24), 0.25);
                var l = BisectLeft(baseCorners, hL);
                var r = BisectRight(baseCorners, hL);
                for (var idx = l; idx < r; idx++) pStep[idx] += spike;
                continue;
            }
            var left = BisectLeft(baseCorners, hL);
            var right = BisectLeft(baseCorners, hR);
            if (left >= right) continue;
            var delta = 0.001 * deltaTime;
            var v = 1 + 6 * 0.001 * LnSum(hL, hR, lnRep);
            var b = StreamBooster(delta);
            var inc = delta < 2 * x / 3
                ? Math.Pow(delta, -1) * Math.Pow(0.08 * Math.Pow(x, -1) * (1 - 24 * Math.Pow(x, -1) * Math.Pow(delta - x / 2, 2)), 0.25) * JsMax(b, v)
                : Math.Pow(delta, -1) * Math.Pow(0.08 * Math.Pow(x, -1) * (1 - 24 * Math.Pow(x, -1) * Math.Pow(x / 6, 2)), 0.25) * JsMax(b, v);
            var incMax = JsMax(inc, inc * 2 - 10);
            for (var idx = left; idx < right; idx++) pStep[idx] += JsMin(inc * anchor[idx], incMax);
        }
        return SmoothOnCorners(baseCorners, pStep, 500, 0.001, false);
    }

    // JS Math.max / Math.min: NaN wins
    private static double JsMax(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b);
    private static double JsMin(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b);

    private static double[] Abar(int K, List<int>[] activeColumns, double[][] deltaKs, double[] aCorners, double[] baseCorners)
    {
        var n = baseCorners.Length;
        var dks = new double[Math.Max(0, K - 1)][];
        for (var k = 0; k < K - 1; k++) dks[k] = new double[n];
        for (var i = 0; i < n; i++)
        {
            var cols = activeColumns[i];
            for (var j = 0; j < cols.Count - 1; j++)
            {
                int k0 = cols[j], k1 = cols[j + 1];
                dks[k0][i] = Math.Abs(deltaKs[k0][i] - deltaKs[k1][i]) + 0.4 * Math.Max(0, Math.Max(deltaKs[k0][i], deltaKs[k1][i]) - 0.11);
            }
        }
        var aStep = Enumerable.Repeat(1.0, aCorners.Length).ToArray();
        for (var i = 0; i < aCorners.Length; i++)
        {
            var idx = BisectLeft(baseCorners, aCorners[i]);
            if (idx >= n) idx = n - 1;
            var cols = activeColumns[idx];
            for (var j = 0; j < cols.Count - 1; j++)
            {
                int k0 = cols[j], k1 = cols[j + 1];
                var d = dks[k0][idx];
                if (d < 0.02) aStep[i] *= Math.Min(0.75 + 0.5 * Math.Max(deltaKs[k0][idx], deltaKs[k1][idx]), 1);
                else if (d < 0.07) aStep[i] *= Math.Min(0.65 + 5 * d + 0.5 * Math.Max(deltaKs[k0][idx], deltaKs[k1][idx]), 1);
            }
        }
        return SmoothOnCorners(aCorners, aStep, 250, 1, true);
    }

    private static double[] Rbar(int K, double x, List<N>[] byColumn, List<N> tailSeq, double[] baseCorners)
    {
        var rStep = new double[baseCorners.Length];
        var times = byColumn.Select(c => c.Select(n => n.H).ToList()).ToArray();
        var iList = new double[tailSeq.Count];
        for (var i = 0; i < tailSeq.Count; i++)
        {
            var (k, hI, tI) = (tailSeq[i].K, tailSeq[i].H, tailSeq[i].T);
            // The next note in the lane after this hold (JS: noteSeqByColumn[k][bisectLeft(times, h) + 1])
            double hJ = 1e9;
            if (k >= 0 && k < K)
            {
                var at = BisectLeft(times[k], hI);
                if (at + 1 < byColumn[k].Count) hJ = byColumn[k][at + 1].H;
            }
            var iH = 0.001 * Math.Abs(tI - hI - 80) / x;
            var iT = 0.001 * Math.Abs(hJ - tI - 80) / x;
            iList[i] = 2 / (2 + Math.Exp(-5 * (iH - 0.75)) + Math.Exp(-5 * (iT - 0.75)));
        }
        for (var i = 0; i < tailSeq.Count - 1; i++)
        {
            var tStart = tailSeq[i].T;
            var tEnd = tailSeq[i + 1].T;
            var left = BisectLeft(baseCorners, tStart);
            var right = BisectLeft(baseCorners, tEnd);
            if (left >= right) continue;
            var deltaR = 0.001 * (tEnd - tStart);
            var r = 0.08 * Math.Pow(deltaR, -0.5) * Math.Pow(x, -1) * (1 + 0.8 * (iList[i] + iList[i + 1]));
            for (var idx = left; idx < right; idx++) rStep[idx] = r;
        }
        return SmoothOnCorners(baseCorners, rStep, 500, 0.001, false);
    }

    private static (double[] C, double[] CV2, double[] Ks) CAndKs(int K, List<N> noteSeq, bool[][] keyUsage, double[] baseCorners)
    {
        var hits = noteSeq.Select(n => n.H).Order().ToArray();
        var hitsV2 = noteSeq.SelectMany(n => n.T >= 0 ? new[] { n.H, n.T } : [n.H]).Order().ToArray();
        var n0 = baseCorners.Length;
        var c = new double[n0];
        var cV2 = new double[n0];
        int lo = 0, hi = 0, lo2 = 0, hi2 = 0;
        for (var i = 0; i < n0; i++)
        {
            var low = baseCorners[i] - 500;
            var high = baseCorners[i] + 500;
            while (lo < hits.Length && hits[lo] < low) lo++;
            while (hi < hits.Length && hits[hi] < high) hi++;
            c[i] = hi - lo;
            while (lo2 < hitsV2.Length && hitsV2[lo2] < low) lo2++;
            while (hi2 < hitsV2.Length && hitsV2[hi2] < high) hi2++;
            cV2[i] = hi2 - lo2;
        }
        var ks = new double[n0];
        for (var i = 0; i < n0; i++)
        {
            var count = 0;
            for (var k = 0; k < K; k++)
                if (keyUsage[k][i]) count++;
            ks[i] = Math.Max(count, 1);
        }
        return (c, cV2, ks);
    }
}
