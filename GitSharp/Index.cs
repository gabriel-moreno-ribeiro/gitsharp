using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSharp;

/// <summary>One staged file in the index.</summary>
public sealed class IndexEntry
{
    public uint CtimeSec, CtimeNsec, MtimeSec, MtimeNsec, Dev, Ino, Mode, Uid, Gid, Size;
    public string Sha = "";
    public string Path = "";

    public string ModeString => Convert.ToString(Mode, 8);
}

/// <summary>
/// Reads and writes .git/index in git's own version 2 format (the "DIRC"
/// file), so that a repository staged with gitsharp is understood by git and
/// vice versa. Extensions (like the cached tree) are skipped on read and not
/// written.
/// </summary>
public sealed class Index
{
    public SortedDictionary<string, IndexEntry> Entries { get; } = new(StringComparer.Ordinal);

    public static Index Read(Repository repo)
    {
        var index = new Index();
        var path = System.IO.Path.Combine(repo.GitDir, "index");
        if (!File.Exists(path)) return index;
        var data = File.ReadAllBytes(path);
        if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 4) != "DIRC") throw new GitException("index file is corrupt");
        var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (version != 2) throw new GitException($"unsupported index version {version}");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        var expected = Convert.ToHexString(SHA1.HashData(data.AsSpan(0, data.Length - 20))).ToLowerInvariant();
        var actual = Convert.ToHexString(data.AsSpan(data.Length - 20)).ToLowerInvariant();
        if (expected != actual) throw new GitException("index checksum mismatch");

        int pos = 12;
        for (int i = 0; i < count; i++)
        {
            var e = new IndexEntry();
            var s = data.AsSpan(pos);
            e.CtimeSec = BinaryPrimitives.ReadUInt32BigEndian(s);
            e.CtimeNsec = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
            e.MtimeSec = BinaryPrimitives.ReadUInt32BigEndian(s[8..]);
            e.MtimeNsec = BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
            e.Dev = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);
            e.Ino = BinaryPrimitives.ReadUInt32BigEndian(s[20..]);
            e.Mode = BinaryPrimitives.ReadUInt32BigEndian(s[24..]);
            e.Uid = BinaryPrimitives.ReadUInt32BigEndian(s[28..]);
            e.Gid = BinaryPrimitives.ReadUInt32BigEndian(s[32..]);
            e.Size = BinaryPrimitives.ReadUInt32BigEndian(s[36..]);
            e.Sha = Convert.ToHexString(s.Slice(40, 20)).ToLowerInvariant();
            var flags = BinaryPrimitives.ReadUInt16BigEndian(s[60..]);
            int nameLen = flags & 0xFFF;
            int nameStart = pos + 62;
            int nameEnd = nameLen < 0xFFF ? nameStart + nameLen : Array.IndexOf(data, (byte)0, nameStart);
            e.Path = Encoding.UTF8.GetString(data, nameStart, nameEnd - nameStart);
            int entryLen = 62 + (nameEnd - nameStart);
            int padded = (entryLen + 8) & ~7; // at least one NUL, pad to 8
            pos += padded;
            index.Entries[e.Path] = e;
        }
        return index;
    }

    public void Write(Repository repo)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4];
        void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(buf, v); ms.Write(buf); }

        ms.Write(Encoding.ASCII.GetBytes("DIRC"));
        U32(2);
        U32((uint)Entries.Count);
        foreach (var e in Entries.Values)
        {
            U32(e.CtimeSec); U32(e.CtimeNsec); U32(e.MtimeSec); U32(e.MtimeNsec);
            U32(e.Dev); U32(e.Ino); U32(e.Mode); U32(e.Uid); U32(e.Gid); U32(e.Size);
            ms.Write(Convert.FromHexString(e.Sha));
            var name = Encoding.UTF8.GetBytes(e.Path);
            ushort flags = (ushort)Math.Min(name.Length, 0xFFF);
            ms.WriteByte((byte)(flags >> 8));
            ms.WriteByte((byte)(flags & 0xFF));
            ms.Write(name);
            int entryLen = 62 + name.Length;
            int padded = (entryLen + 8) & ~7;
            for (int i = entryLen; i < padded; i++) ms.WriteByte(0);
        }
        var body = ms.ToArray();
        var checksum = SHA1.HashData(body);
        var path = System.IO.Path.Combine(repo.GitDir, "index");
        using var fs = File.Create(path);
        fs.Write(body);
        fs.Write(checksum);
    }

    /// <summary>Creates an entry for a work tree file, hashing (and storing) its content.</summary>
    public static IndexEntry FromFile(Repository repo, string relativePath)
    {
        var full = System.IO.Path.Combine(repo.WorkTree, relativePath);
        var content = File.ReadAllBytes(full);
        var sha = Objects.HashBytes("blob", content, true, repo);
        var info = new FileInfo(full);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc);
        var ctime = new DateTimeOffset(info.CreationTimeUtc > info.LastWriteTimeUtc ? info.CreationTimeUtc : info.LastWriteTimeUtc);
        return new IndexEntry
        {
            CtimeSec = (uint)ctime.ToUnixTimeSeconds(),
            CtimeNsec = (uint)((ctime.Ticks % TimeSpan.TicksPerSecond) * 100),
            MtimeSec = (uint)mtime.ToUnixTimeSeconds(),
            MtimeNsec = (uint)((mtime.Ticks % TimeSpan.TicksPerSecond) * 100),
            Mode = Convert.ToUInt32(Repository.FileMode(full), 8),
            Size = (uint)Math.Min(info.Length, uint.MaxValue),
            Sha = sha,
            Path = relativePath,
        };
    }
}
