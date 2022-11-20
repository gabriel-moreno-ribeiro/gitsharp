using System.Text;

namespace GitSharp;

/// <summary>Line-based diff (longest common subsequence) with unified output.</summary>
public static class Diff
{
    public enum Op { Equal, Insert, Delete }

    public sealed record Edit(Op Op, string Line);

    public static List<Edit> Lines(string a, string b)
    {
        var x = Split(a);
        var y = Split(b);
        int n = x.Length, m = y.Length;
        // lcs[i,j] = length of LCS of x[i..] and y[j..]
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<Edit>();
        int p = 0, q = 0;
        while (p < n && q < m)
        {
            if (x[p] == y[q]) { edits.Add(new Edit(Op.Equal, x[p])); p++; q++; }
            else if (lcs[p + 1, q] >= lcs[p, q + 1]) { edits.Add(new Edit(Op.Delete, x[p])); p++; }
            else { edits.Add(new Edit(Op.Insert, y[q])); q++; }
        }
        while (p < n) edits.Add(new Edit(Op.Delete, x[p++]));
        while (q < m) edits.Add(new Edit(Op.Insert, y[q++]));
        return edits;
    }

    private static string[] Split(string s)
    {
        if (s.Length == 0) return Array.Empty<string>();
        var lines = s.Split('\n');
        if (s.EndsWith('\n')) Array.Resize(ref lines, lines.Length - 1);
        return lines;
    }

    /// <summary>Formats edits as a unified diff with the given context size.</summary>
    public static string Unified(string pathA, string pathB, string a, string b, int context = 3)
    {
        var edits = Lines(a, b);
        if (edits.All(e => e.Op == Op.Equal)) return "";
        var sb = new StringBuilder();
        sb.Append("--- ").Append(pathA).Append('\n');
        sb.Append("+++ ").Append(pathB).Append('\n');

        // group edits into hunks
        int i = 0;
        int oldLine = 1, newLine = 1;
        while (i < edits.Count)
        {
            if (edits[i].Op == Op.Equal) { i++; oldLine++; newLine++; continue; }
            int start = Math.Max(0, i - context);
            int end = i;
            int lastChange = i;
            while (end < edits.Count && (edits[end].Op != Op.Equal || end - lastChange <= context * 2))
            {
                if (edits[end].Op != Op.Equal) lastChange = end;
                end++;
            }
            end = Math.Min(edits.Count, lastChange + context + 1);

            int hunkOldStart = oldLine - (i - start), hunkNewStart = newLine - (i - start);
            int oldCount = 0, newCount = 0;
            var body = new StringBuilder();
            for (int k = start; k < end; k++)
            {
                var e = edits[k];
                switch (e.Op)
                {
                    case Op.Equal: body.Append(' ').Append(e.Line).Append('\n'); oldCount++; newCount++; break;
                    case Op.Delete: body.Append('-').Append(e.Line).Append('\n'); oldCount++; break;
                    case Op.Insert: body.Append('+').Append(e.Line).Append('\n'); newCount++; break;
                }
            }
            sb.Append($"@@ -{hunkOldStart},{oldCount} +{hunkNewStart},{newCount} @@\n").Append(body);
            for (int k = i; k < end; k++)
            {
                if (edits[k].Op != Op.Insert) oldLine++;
                if (edits[k].Op != Op.Delete) newLine++;
            }
            i = end;
        }
        return sb.ToString();
    }
}
