using System.Diagnostics;
using System.Text;
using GitSharp;
using Xunit;

namespace GitSharp.Tests;

/// <summary>A scratch repository on disk, deleted after each test.</summary>
public sealed class Sandbox : IDisposable
{
    public string Dir { get; }
    public Repository Repo { get; }
    public StringWriter Out { get; } = new();
    public Commands Cmd { get; }

    public Sandbox()
    {
        Dir = Path.Combine(Path.GetTempPath(), "gitsharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Repo = Repository.Init(Dir);
        File.AppendAllText(Path.Combine(Repo.GitDir, "config"), "[user]\n\tname = Test User\n\temail = test@example.com\n");
        Cmd = new Commands(Repo, Out);
        Environment.CurrentDirectory = Dir;
    }

    public void Write(string rel, string content)
    {
        var full = Path.Combine(Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Output()
    {
        var s = Out.ToString();
        Out.GetStringBuilder().Clear();
        return s;
    }

    /// <summary>Runs the real git binary inside the sandbox (for interoperability checks).</summary>
    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"] = Dir;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = Path.GetTempPath();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class ObjectTests
{
    [Fact]
    public void BlobHashMatchesGit()
    {
        // "hello\n" as a git blob has a well known id
        var sha = Objects.HashBytes("blob", Encoding.ASCII.GetBytes("hello\n"), false, null);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", sha);
        var empty = Objects.HashBytes("blob", Array.Empty<byte>(), false, null);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", empty);
    }

    [Fact]
    public void WriteAndReadObjects()
    {
        using var sb = new Sandbox();
        var sha = Objects.HashBytes("blob", Encoding.ASCII.GetBytes("content"), true, sb.Repo);
        Assert.True(File.Exists(sb.Repo.ObjectPath(sha)));
        var (type, content) = Objects.Read(sb.Repo, sha);
        Assert.Equal("blob", type);
        Assert.Equal("content", Encoding.ASCII.GetString(content));
        Assert.Equal(sha, sb.Repo.ResolveObjectId(sha[..7]));
        Assert.Throws<GitException>(() => Objects.Read(sb.Repo, "0000000000000000000000000000000000000000"));
    }

    [Fact]
    public void TreeRoundTripsAndSortsLikeGit()
    {
        var entries = new List<TreeEntry>
        {
            new("100644", "b.txt", "ce013625030ba8dba906f756967f9e9ca394464a"),
            new("40000", "a", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"),
            new("100644", "a.txt", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"),
            new("100644", "a-b", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"),
        };
        var parsed = Tree.Parse(Tree.Serialize(entries));
        // git orders by name, but directories compare as "name/"
        Assert.Equal(new[] { "a-b", "a.txt", "a", "b.txt" }, parsed.Select(e => e.Name).ToArray());
        Assert.Equal("40000", parsed[2].Mode);
        Assert.True(parsed[2].IsTree);
    }

    [Fact]
    public void CommitRoundTrip()
    {
        var stamp = new DateTimeOffset(2022, 11, 20, 10, 30, 0, TimeSpan.FromHours(-3));
        var sig = Commit.Signature("Ana Silva", "ana@example.com", stamp);
        Assert.Equal($"Ana Silva <ana@example.com> {stamp.ToUnixTimeSeconds()} -0300", sig);
        var c = new Commit { Tree = new string('a', 40), Author = sig, Committer = sig, Message = "first\n\nbody line\n" };
        c.Parents.Add(new string('b', 40));
        var parsed = Commit.Parse(c.Serialize());
        Assert.Equal(c.Tree, parsed.Tree);
        Assert.Equal(c.Parents, parsed.Parents);
        Assert.Equal("first\n\nbody line\n", parsed.Message);
        var (name, email, when) = Commit.ParseSignature(parsed.Author);
        Assert.Equal("Ana Silva", name);
        Assert.Equal("ana@example.com", email);
        Assert.Equal(-3, when.Offset.TotalHours);
        Assert.Equal(stamp.ToUnixTimeSeconds(), when.ToUnixTimeSeconds());
    }
}

public class IndexTests
{
    [Fact]
    public void IndexRoundTrip()
    {
        using var sb = new Sandbox();
        sb.Write("a.txt", "A");
        sb.Write("dir/b.txt", "B");
        var index = new Index();
        index.Entries["a.txt"] = Index.FromFile(sb.Repo, "a.txt");
        index.Entries["dir/b.txt"] = Index.FromFile(sb.Repo, "dir/b.txt");
        index.Write(sb.Repo);
        var read = Index.Read(sb.Repo);
        Assert.Equal(new[] { "a.txt", "dir/b.txt" }, read.Entries.Keys.ToArray());
        Assert.Equal(index.Entries["a.txt"].Sha, read.Entries["a.txt"].Sha);
        Assert.Equal(1u, read.Entries["a.txt"].Size);
        Assert.Equal("100644", read.Entries["a.txt"].ModeString);
    }

    [Fact]
    public void RealGitReadsOurIndex()
    {
        using var sb = new Sandbox();
        sb.Write("hello.txt", "hello\n");
        sb.Cmd.Add(new[] { "hello.txt" });
        var listed = sb.Git("ls-files", "-s");
        Assert.Contains("100644 ce013625030ba8dba906f756967f9e9ca394464a 0\thello.txt", listed);
    }

    [Fact]
    public void WeReadRealGitIndex()
    {
        using var sb = new Sandbox();
        sb.Write("x.txt", "x");
        sb.Write("sub/y.txt", "y");
        sb.Git("add", ".");
        var index = Index.Read(sb.Repo);
        Assert.Equal(new[] { "sub/y.txt", "x.txt" }, index.Entries.Keys.ToArray());
        Assert.Equal(Objects.HashBytes("blob", Encoding.ASCII.GetBytes("x"), false, null), index.Entries["x.txt"].Sha);
    }
}

public class CommandTests
{
    [Fact]
    public void AddCommitLogStatus()
    {
        using var sb = new Sandbox();
        sb.Write("README.md", "# project\n");
        sb.Write("src/main.c", "int main() {}\n");
        sb.Cmd.Status();
        Assert.Contains("Untracked files:", sb.Output());

        sb.Cmd.Add(new[] { "." });
        sb.Cmd.Status();
        var status = sb.Output();
        Assert.Contains("new file:   README.md", status);
        Assert.Contains("new file:   src/main.c", status);

        var first = sb.Cmd.CommitChanges("initial commit");
        Assert.Contains("(root-commit)", sb.Output());
        Assert.Equal(first, sb.Repo.HeadCommit());
        Assert.Equal("main", sb.Repo.CurrentBranch());

        sb.Cmd.Status();
        Assert.Contains("nothing to commit, working tree clean", sb.Output());

        sb.Write("README.md", "# project\nmore\n");
        sb.Cmd.Status();
        Assert.Contains("modified:   README.md", sb.Output());
        sb.Cmd.Add(new[] { "README.md" });
        var second = sb.Cmd.CommitChanges("update readme");
        sb.Output();

        sb.Cmd.Log(oneline: true);
        var log = sb.Output();
        Assert.Equal($"{second[..7]} update readme\n{first[..7]} initial commit\n", log);

        var commit = Commit.Parse(Objects.Read(sb.Repo, second).Content);
        Assert.Equal(new[] { first }, commit.Parents);
        Assert.StartsWith("Test User <test@example.com>", commit.Author);
    }

    [Fact]
    public void CommitWithoutChangesFails()
    {
        using var sb = new Sandbox();
        Assert.Throws<GitException>(() => sb.Cmd.CommitChanges("empty"));
        sb.Write("a", "a");
        sb.Cmd.Add(new[] { "a" });
        sb.Cmd.CommitChanges("one");
        Assert.Throws<GitException>(() => sb.Cmd.CommitChanges("again"));
    }

    [Fact]
    public void RealGitUnderstandsOurCommits()
    {
        using var sb = new Sandbox();
        sb.Write("a.txt", "alpha\n");
        sb.Write("dir/b.txt", "beta\n");
        sb.Cmd.Add(new[] { "." });
        var sha = sb.Cmd.CommitChanges("made by gitsharp");
        sb.Write("a.txt", "alpha\n");

        Assert.Equal("clean", sb.Git("status", "--porcelain").Length == 0 ? "clean" : sb.Git("status", "--porcelain"));
        Assert.Contains("made by gitsharp", sb.Git("log", "--oneline"));
        Assert.Equal(sha, sb.Git("rev-parse", "HEAD").Trim());
        Assert.Contains("dir/b.txt", sb.Git("ls-tree", "-r", "HEAD"));
        Assert.Equal("beta\n", sb.Git("show", "HEAD:dir/b.txt"));
        // fsck validates object hashes and formats
        var fsck = sb.Git("fsck", "--strict");
        Assert.DoesNotContain("error", fsck);
    }

    [Fact]
    public void WeUnderstandRealGitCommits()
    {
        using var sb = new Sandbox();
        sb.Write("one.txt", "1\n");
        sb.Git("add", ".");
        sb.Git("-c", "user.name=Real Git", "-c", "user.email=git@example.com", "commit", "-q", "-m", "from git");
        sb.Write("two.txt", "2\n");
        sb.Git("add", ".");
        sb.Git("-c", "user.name=Real Git", "-c", "user.email=git@example.com", "commit", "-q", "-m", "second from git");

        sb.Cmd.Log(oneline: true);
        var log = sb.Output();
        Assert.Contains("second from git", log);
        Assert.Contains("from git", log);
        sb.Cmd.Status();
        Assert.Contains("nothing to commit", sb.Output());
        sb.Cmd.LsTree("HEAD", true);
        Assert.Contains("two.txt", sb.Output());
        sb.Cmd.CatFile("-p", "HEAD");
        Assert.Contains("Real Git <git@example.com>", sb.Output());
    }

    [Fact]
    public void PlumbingCommands()
    {
        using var sb = new Sandbox();
        sb.Write("f.txt", "hello\n");
        var sha = sb.Cmd.HashObject(Path.Combine(sb.Dir, "f.txt"), write: true);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", sha);
        sb.Output();
        sb.Cmd.CatFile("-t", sha);
        Assert.Equal("blob\n", sb.Output());
        sb.Cmd.CatFile("-s", sha);
        Assert.Equal("6\n", sb.Output());
        sb.Cmd.CatFile("-p", sha[..8]);
        Assert.Equal("hello\n", sb.Output());

        sb.Cmd.Add(new[] { "f.txt" });
        var tree = sb.Cmd.WriteTree();
        sb.Output();
        Assert.Equal(sb.Git("write-tree").Trim(), tree);
        sb.Cmd.LsTree(tree, false);
        Assert.Equal($"100644 blob {sha}\tf.txt\n", sb.Output());
        sb.Cmd.LsFiles();
        Assert.Equal("f.txt\n", sb.Output());
    }

    [Fact]
    public void RemoveAndStagedDeletion()
    {
        using var sb = new Sandbox();
        sb.Write("keep.txt", "k");
        sb.Write("gone.txt", "g");
        sb.Cmd.Add(new[] { "." });
        sb.Cmd.CommitChanges("two files");
        sb.Output();
        sb.Cmd.Remove(new[] { "gone.txt" }, cached: false);
        Assert.False(File.Exists(Path.Combine(sb.Dir, "gone.txt")));
        sb.Cmd.Status();
        Assert.Contains("deleted:    gone.txt", sb.Output());
        sb.Cmd.CommitChanges("remove gone");
        sb.Output();
        sb.Cmd.LsTree("HEAD", true);
        var tree = sb.Output();
        Assert.Contains("keep.txt", tree);
        Assert.DoesNotContain("gone.txt", tree);

        File.Delete(Path.Combine(sb.Dir, "keep.txt"));
        sb.Cmd.Status();
        Assert.Contains("Changes not staged for commit:", sb.Output());
        sb.Cmd.Add(new[] { "." });
        sb.Cmd.Status();
        Assert.Contains("Changes to be committed:", sb.Output());
    }

    [Fact]
    public void BranchesCheckoutAndTags()
    {
        using var sb = new Sandbox();
        sb.Write("file.txt", "v1\n");
        sb.Cmd.Add(new[] { "." });
        var c1 = sb.Cmd.CommitChanges("v1");
        sb.Output();

        sb.Cmd.Checkout("feature", createBranch: true);
        Assert.Equal("feature", sb.Repo.CurrentBranch());
        sb.Write("file.txt", "v2\n");
        sb.Write("extra/new.txt", "new\n");
        sb.Cmd.Add(new[] { "." });
        var c2 = sb.Cmd.CommitChanges("v2 on feature");
        sb.Output();

        sb.Cmd.Branch(null);
        Assert.Equal("* feature\n  main\n", sb.Output());

        sb.Cmd.Checkout("main");
        Assert.Equal("main", sb.Repo.CurrentBranch());
        Assert.Equal("v1\n", File.ReadAllText(Path.Combine(sb.Dir, "file.txt")));
        Assert.False(File.Exists(Path.Combine(sb.Dir, "extra/new.txt")), "file from the other branch removed");
        Assert.False(Directory.Exists(Path.Combine(sb.Dir, "extra")), "empty directory removed");
        Assert.Equal(c1, sb.Repo.HeadCommit());

        sb.Cmd.Checkout("feature");
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(sb.Dir, "file.txt")));
        Assert.True(File.Exists(Path.Combine(sb.Dir, "extra/new.txt")));
        Assert.Equal(c2, sb.Repo.HeadCommit());

        sb.Cmd.Checkout(c1[..8]);
        Assert.Null(sb.Repo.CurrentBranch());
        Assert.Equal(c1, sb.Repo.HeadCommit());
        Assert.Contains("HEAD is now at", sb.Output());

        sb.Cmd.Checkout("feature");
        sb.Output();
        sb.Cmd.Tag("v2.0");
        sb.Cmd.Tag(null);
        Assert.Equal("v2.0\n", sb.Output());
        Assert.Equal(c2, sb.Repo.ResolveCommit("v2.0"));
        Assert.Contains("v2.0", sb.Git("tag"));

        sb.Write("file.txt", "dirty\n");
        Assert.Throws<GitException>(() => sb.Cmd.Checkout("main"));

        sb.Write("file.txt", "v2\n"); // undo the local change
        sb.Cmd.Checkout("main");
        Assert.Equal("v1\n", File.ReadAllText(Path.Combine(sb.Dir, "file.txt")));
    }

    [Fact]
    public void BranchDeleteAndErrors()
    {
        using var sb = new Sandbox();
        sb.Write("f", "f");
        sb.Cmd.Add(new[] { "f" });
        sb.Cmd.CommitChanges("c");
        sb.Cmd.Branch("topic");
        Assert.Throws<GitException>(() => sb.Cmd.Branch("topic"));
        Assert.Throws<GitException>(() => sb.Cmd.Branch("main", delete: true));
        sb.Cmd.Branch("topic", delete: true);
        Assert.Throws<GitException>(() => sb.Cmd.Checkout("topic"));
        Assert.Throws<GitException>(() => sb.Cmd.Add(new[] { "nope.txt" }));
    }

    [Fact]
    public void DiffShowsChanges()
    {
        using var sb = new Sandbox();
        sb.Write("poem.txt", "roses are red\nviolets are blue\nsugar is sweet\n");
        sb.Cmd.Add(new[] { "." });
        sb.Cmd.CommitChanges("poem");
        sb.Output();
        sb.Write("poem.txt", "roses are red\nviolets are purple\nsugar is sweet\nand so are you\n");
        sb.Cmd.DiffChanges(cached: false);
        var diff = sb.Output();
        Assert.Contains("diff --git a/poem.txt b/poem.txt", diff);
        Assert.Contains("-violets are blue", diff);
        Assert.Contains("+violets are purple", diff);
        Assert.Contains("+and so are you", diff);
        Assert.Contains("@@ -1,3 +1,4 @@", diff);

        sb.Cmd.DiffChanges(cached: true);
        Assert.Equal("", sb.Output());
        sb.Cmd.Add(new[] { "poem.txt" });
        sb.Cmd.DiffChanges(cached: true);
        Assert.Contains("+violets are purple", sb.Output());
    }
}

public class DiffTests
{
    [Fact]
    public void LcsEdits()
    {
        var edits = Diff.Lines("a\nb\nc\n", "a\nc\nd\n");
        Assert.Equal(new[] { Diff.Op.Equal, Diff.Op.Delete, Diff.Op.Equal, Diff.Op.Insert }, edits.Select(e => e.Op).ToArray());
        Assert.Empty(Diff.Unified("a", "b", "same\n", "same\n"));
    }

    [Fact]
    public void UnifiedHunks()
    {
        var a = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i)) + "\n";
        var b = a.Replace("line 10", "LINE 10");
        var u = Diff.Unified("a/f", "b/f", a, b);
        Assert.StartsWith("--- a/f\n+++ b/f\n@@ -7,7 +7,7 @@\n", u);
        Assert.Contains("-line 10\n+LINE 10\n", u);
        Assert.DoesNotContain("line 1\n", u.Split("@@")[2]);
    }
}
