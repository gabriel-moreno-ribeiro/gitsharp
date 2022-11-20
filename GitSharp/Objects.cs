using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GitSharp;

/// <summary>
/// Loose object storage compatible with real git: an object is
/// "&lt;type&gt; &lt;size&gt;\0&lt;content&gt;", hashed with SHA-1 and stored
/// zlib-compressed at .git/objects/xx/yyyy....
/// </summary>
public static class Objects
{
    public static string HashBytes(string type, byte[] content, bool write, Repository? repo)
    {
        var header = Encoding.ASCII.GetBytes($"{type} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, full, 0, header.Length);
        Buffer.BlockCopy(content, 0, full, header.Length, content.Length);
        var sha = Convert.ToHexString(SHA1.HashData(full)).ToLowerInvariant();
        if (write)
        {
            if (repo is null) throw new InvalidOperationException("no repository to write to");
            var path = repo.ObjectPath(sha);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var fs = File.Create(path);
                using var z = new ZLibStream(fs, CompressionLevel.Optimal);
                z.Write(full);
            }
        }
        return sha;
    }

    public static (string Type, byte[] Content) Read(Repository repo, string sha)
    {
        sha = repo.ResolveObjectId(sha);
        var path = repo.ObjectPath(sha);
        if (!File.Exists(path)) throw new GitException($"object {sha} not found");
        using var fs = File.OpenRead(path);
        using var z = new ZLibStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var raw = ms.ToArray();
        int nul = Array.IndexOf(raw, (byte)0);
        var header = Encoding.ASCII.GetString(raw, 0, nul).Split(' ');
        var content = raw[(nul + 1)..];
        if (int.Parse(header[1]) != content.Length) throw new GitException($"object {sha} is corrupt");
        return (header[0], content);
    }
}

public sealed record TreeEntry(string Mode, string Name, string Sha)
{
    public bool IsTree => Mode == "40000";
}

public static class Tree
{
    /// <summary>Parses "mode name\0sha20" records.</summary>
    public static List<TreeEntry> Parse(byte[] content)
    {
        var entries = new List<TreeEntry>();
        int i = 0;
        while (i < content.Length)
        {
            int space = Array.IndexOf(content, (byte)' ', i);
            int nul = Array.IndexOf(content, (byte)0, space);
            var mode = Encoding.ASCII.GetString(content, i, space - i);
            var name = Encoding.UTF8.GetString(content, space + 1, nul - space - 1);
            var sha = Convert.ToHexString(content, nul + 1, 20).ToLowerInvariant();
            entries.Add(new TreeEntry(mode, name, sha));
            i = nul + 21;
        }
        return entries;
    }

    /// <summary>Serialises entries in git's sort order (directories sort as "name/").</summary>
    public static byte[] Serialize(IEnumerable<TreeEntry> entries)
    {
        using var ms = new MemoryStream();
        foreach (var e in entries.OrderBy(e => e.IsTree ? e.Name + "/" : e.Name, StringComparer.Ordinal))
        {
            ms.Write(Encoding.ASCII.GetBytes($"{e.Mode} "));
            ms.Write(Encoding.UTF8.GetBytes(e.Name));
            ms.WriteByte(0);
            ms.Write(Convert.FromHexString(e.Sha));
        }
        return ms.ToArray();
    }

    /// <summary>Flattens a tree recursively into path => (mode, sha).</summary>
    public static SortedDictionary<string, (string Mode, string Sha)> Flatten(Repository repo, string treeSha, string prefix = "")
    {
        var result = new SortedDictionary<string, (string, string)>(StringComparer.Ordinal);
        var (type, content) = Objects.Read(repo, treeSha);
        if (type != "tree") throw new GitException($"{treeSha} is not a tree");
        foreach (var e in Parse(content))
        {
            var path = prefix + e.Name;
            if (e.IsTree)
            {
                foreach (var kv in Flatten(repo, e.Sha, path + "/")) result[kv.Key] = kv.Value;
            }
            else
            {
                result[path] = (e.Mode, e.Sha);
            }
        }
        return result;
    }

    /// <summary>Builds (and writes) nested tree objects from flat path => (mode, sha) entries. Returns the root sha.</summary>
    public static string Build(Repository repo, IDictionary<string, (string Mode, string Sha)> files)
    {
        return BuildDir(repo, files, "");
    }

    private static string BuildDir(Repository repo, IDictionary<string, (string Mode, string Sha)> files, string dir)
    {
        var entries = new List<TreeEntry>();
        var subdirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kv in files)
        {
            if (!kv.Key.StartsWith(dir, StringComparison.Ordinal)) continue;
            var rest = kv.Key[dir.Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0) entries.Add(new TreeEntry(kv.Value.Mode, rest, kv.Value.Sha));
            else subdirs.Add(rest[..slash]);
        }
        foreach (var sub in subdirs)
        {
            entries.Add(new TreeEntry("40000", sub, BuildDir(repo, files, dir + sub + "/")));
        }
        return Objects.HashBytes("tree", Serialize(entries), true, repo);
    }
}

public sealed class Commit
{
    public string Tree = "";
    public List<string> Parents = new();
    public string Author = "";
    public string Committer = "";
    public string Message = "";

    public static Commit Parse(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        var c = new Commit();
        int blank = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = blank >= 0 ? text[..blank] : text;
        c.Message = blank >= 0 ? text[(blank + 2)..] : "";
        foreach (var line in headers.Split('\n'))
        {
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            var key = line[..space];
            var value = line[(space + 1)..];
            switch (key)
            {
                case "tree": c.Tree = value; break;
                case "parent": c.Parents.Add(value); break;
                case "author": c.Author = value; break;
                case "committer": c.Committer = value; break;
            }
        }
        return c;
    }

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("tree ").Append(Tree).Append('\n');
        foreach (var p in Parents) sb.Append("parent ").Append(p).Append('\n');
        sb.Append("author ").Append(Author).Append('\n');
        sb.Append("committer ").Append(Committer).Append('\n');
        sb.Append('\n');
        sb.Append(Message);
        if (!Message.EndsWith('\n')) sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>"Name &lt;email&gt; 1700000000 +0000"</summary>
    public static string Signature(string name, string email, DateTimeOffset when)
    {
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{name} <{email}> {when.ToUnixTimeSeconds()} {sign}{offset.Hours:00}{offset.Minutes:00}";
    }

    public static (string Name, string Email, DateTimeOffset When) ParseSignature(string sig)
    {
        int lt = sig.IndexOf('<');
        int gt = sig.IndexOf('>');
        var name = sig[..lt].Trim();
        var email = sig[(lt + 1)..gt];
        var rest = sig[(gt + 1)..].Trim().Split(' ');
        var when = DateTimeOffset.FromUnixTimeSeconds(long.Parse(rest[0]));
        if (rest.Length > 1 && rest[1].Length == 5)
        {
            var hours = int.Parse(rest[1][1..3]);
            var minutes = int.Parse(rest[1][3..5]);
            var off = new TimeSpan(hours, minutes, 0);
            if (rest[1][0] == '-') off = -off;
            when = when.ToOffset(off);
        }
        return (name, email, when);
    }
}

public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
}
