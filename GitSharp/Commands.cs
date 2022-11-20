using System.Text;

namespace GitSharp;

/// <summary>The porcelain and plumbing commands, each writing to an output writer.</summary>
public sealed class Commands
{
    private readonly Repository _repo;
    private readonly TextWriter _out;

    public Commands(Repository repo, TextWriter output)
    {
        _repo = repo;
        _out = output;
    }

    // ---- plumbing ------------------------------------------------------------

    public string HashObject(string file, bool write)
    {
        var sha = Objects.HashBytes("blob", File.ReadAllBytes(file), write, _repo);
        _out.WriteLine(sha);
        return sha;
    }

    public void CatFile(string mode, string id)
    {
        var sha = id.Length == 40 ? id : ResolveAny(id);
        var (type, content) = Objects.Read(_repo, sha);
        switch (mode)
        {
            case "-t": _out.WriteLine(type); break;
            case "-s": _out.WriteLine(content.Length); break;
            case "-p":
                if (type == "tree")
                {
                    foreach (var e in Tree.Parse(content))
                        _out.WriteLine($"{(e.IsTree ? "040000" : e.Mode)} {(e.IsTree ? "tree" : "blob")} {e.Sha}\t{e.Name}");
                }
                else
                {
                    _out.Write(Encoding.UTF8.GetString(content));
                }
                break;
            default: throw new GitException("usage: cat-file (-t|-s|-p) <object>");
        }
    }

    private string ResolveAny(string id)
    {
        try { return _repo.ResolveCommit(id); }
        catch (GitException) { return _repo.ResolveObjectId(id); }
    }

    public string WriteTree()
    {
        var index = Index.Read(_repo);
        var files = index.Entries.ToDictionary(kv => kv.Key, kv => (kv.Value.ModeString, kv.Value.Sha), StringComparer.Ordinal);
        var sha = Tree.Build(_repo, files);
        _out.WriteLine(sha);
        return sha;
    }

    public void LsTree(string id, bool recursive)
    {
        var sha = ResolveAny(id);
        var (type, content) = Objects.Read(_repo, sha);
        if (type == "commit") sha = Commit.Parse(content).Tree;
        if (recursive)
        {
            foreach (var kv in Tree.Flatten(_repo, sha))
                _out.WriteLine($"{kv.Value.Mode} blob {kv.Value.Sha}\t{kv.Key}");
        }
        else
        {
            foreach (var e in Tree.Parse(Objects.Read(_repo, sha).Content))
                _out.WriteLine($"{(e.IsTree ? "040000" : e.Mode)} {(e.IsTree ? "tree" : "blob")} {e.Sha}\t{e.Name}");
        }
    }

    public void LsFiles()
    {
        foreach (var path in Index.Read(_repo).Entries.Keys) _out.WriteLine(path);
    }

    // ---- porcelain -------------------------------------------------------------

    public void Add(IEnumerable<string> paths)
    {
        var index = Index.Read(_repo);
        foreach (var p in ExpandPaths(paths))
        {
            var full = Path.Combine(_repo.WorkTree, p);
            if (File.Exists(full)) index.Entries[p] = Index.FromFile(_repo, p);
            else if (index.Entries.ContainsKey(p)) index.Entries.Remove(p); // "add" of a deleted file stages the deletion
            else throw new GitException($"pathspec '{p}' did not match any files");
        }
        index.Write(_repo);
    }

    public void Remove(IEnumerable<string> paths, bool cached)
    {
        var index = Index.Read(_repo);
        foreach (var p in paths.Select(NormalizePath))
        {
            if (!index.Entries.Remove(p)) throw new GitException($"pathspec '{p}' did not match any files");
            var full = Path.Combine(_repo.WorkTree, p);
            if (!cached && File.Exists(full)) File.Delete(full);
        }
        index.Write(_repo);
    }

    /// <summary>Expands "." and directories into the files they contain (relative, sorted).</summary>
    private IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var index = Index.Read(_repo);
        foreach (var raw in paths)
        {
            var rel = NormalizePath(raw);
            var full = Path.Combine(_repo.WorkTree, rel);
            if (Directory.Exists(full))
            {
                var prefix = rel == "." ? "" : rel + "/";
                foreach (var f in _repo.WorkTreeFiles().Where(f => prefix == "" || f.StartsWith(prefix, StringComparison.Ordinal))) result.Add(f);
                // deleted files under the directory that are still in the index
                foreach (var tracked in index.Entries.Keys.Where(k => (prefix == "" || k.StartsWith(prefix, StringComparison.Ordinal)) && !File.Exists(Path.Combine(_repo.WorkTree, k))))
                    result.Add(tracked);
            }
            else
            {
                result.Add(rel);
            }
        }
        return result;
    }

    private string NormalizePath(string raw)
    {
        var full = Path.GetFullPath(raw, Directory.GetCurrentDirectory());
        var rel = _repo.RelativePath(full);
        return rel == "." ? "." : rel;
    }

    public string CommitChanges(string message, DateTimeOffset? when = null)
    {
        var index = Index.Read(_repo);
        if (index.Entries.Count == 0) throw new GitException("nothing to commit (index is empty)");
        var files = index.Entries.ToDictionary(kv => kv.Key, kv => (kv.Value.ModeString, kv.Value.Sha), StringComparer.Ordinal);
        var treeSha = Tree.Build(_repo, files);
        var parent = _repo.HeadCommit();
        if (parent is not null)
        {
            var parentCommit = Commit.Parse(Objects.Read(_repo, parent).Content);
            if (parentCommit.Tree == treeSha) throw new GitException("nothing to commit, working tree clean");
        }
        var (name, email) = _repo.Identity();
        var sig = Commit.Signature(name, email, when ?? DateTimeOffset.Now);
        var commit = new Commit { Tree = treeSha, Author = sig, Committer = sig, Message = message.TrimEnd('\n') + "\n" };
        if (parent is not null) commit.Parents.Add(parent);
        var sha = Objects.HashBytes("commit", commit.Serialize(), true, _repo);
        _repo.UpdateHead(sha);
        var branch = _repo.CurrentBranch() ?? "detached HEAD";
        var firstLine = message.Split('\n')[0];
        _out.WriteLine($"[{branch}{(parent is null ? " (root-commit)" : "")} {sha[..7]}] {firstLine}");
        return sha;
    }

    public void Log(int max = int.MaxValue, bool oneline = false)
    {
        var sha = _repo.HeadCommit();
        if (sha is null) throw new GitException("your current branch does not have any commits yet");
        int count = 0;
        while (sha is not null && count++ < max)
        {
            var commit = Commit.Parse(Objects.Read(_repo, sha).Content);
            if (oneline)
            {
                _out.WriteLine($"{sha[..7]} {commit.Message.Split('\n')[0]}");
            }
            else
            {
                var (name, email, when) = Commit.ParseSignature(commit.Author);
                _out.WriteLine($"commit {sha}");
                _out.WriteLine($"Author: {name} <{email}>");
                _out.WriteLine($"Date:   {when:ddd MMM d HH:mm:ss yyyy zzz}".Replace(":", "", 0));
                _out.WriteLine();
                foreach (var line in commit.Message.TrimEnd('\n').Split('\n')) _out.WriteLine("    " + line);
                _out.WriteLine();
            }
            sha = commit.Parents.FirstOrDefault();
        }
    }

    public sealed record StatusReport(
        SortedDictionary<string, string> Staged,   // path => "new file" | "modified" | "deleted"
        SortedDictionary<string, string> Unstaged, // path => "modified" | "deleted"
        List<string> Untracked);

    public StatusReport ComputeStatus()
    {
        var index = Index.Read(_repo);
        var head = _repo.HeadCommit();
        var headFiles = head is null
            ? new SortedDictionary<string, (string Mode, string Sha)>(StringComparer.Ordinal)
            : Tree.Flatten(_repo, Commit.Parse(Objects.Read(_repo, head).Content).Tree);

        var staged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in index.Entries)
        {
            if (!headFiles.TryGetValue(kv.Key, out var h)) staged[kv.Key] = "new file";
            else if (h.Sha != kv.Value.Sha || h.Mode != kv.Value.ModeString) staged[kv.Key] = "modified";
        }
        foreach (var path in headFiles.Keys.Where(p => !index.Entries.ContainsKey(p))) staged[path] = "deleted";

        var unstaged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in index.Entries)
        {
            var full = Path.Combine(_repo.WorkTree, kv.Key);
            if (!File.Exists(full)) { unstaged[kv.Key] = "deleted"; continue; }
            var sha = Objects.HashBytes("blob", File.ReadAllBytes(full), false, null);
            if (sha != kv.Value.Sha || Repository.FileMode(full) != kv.Value.ModeString) unstaged[kv.Key] = "modified";
        }
        var untracked = _repo.WorkTreeFiles().Where(f => !index.Entries.ContainsKey(f)).ToList();
        return new StatusReport(staged, unstaged, untracked);
    }

    public void Status()
    {
        var branch = _repo.CurrentBranch();
        _out.WriteLine(branch is not null ? $"On branch {branch}" : $"HEAD detached at {_repo.HeadCommit()?[..7]}");
        var report = ComputeStatus();
        if (report.Staged.Count > 0)
        {
            _out.WriteLine("Changes to be committed:");
            foreach (var kv in report.Staged) _out.WriteLine($"\t{kv.Value + ":",-12}{kv.Key}");
            _out.WriteLine();
        }
        if (report.Unstaged.Count > 0)
        {
            _out.WriteLine("Changes not staged for commit:");
            foreach (var kv in report.Unstaged) _out.WriteLine($"\t{kv.Value + ":",-12}{kv.Key}");
            _out.WriteLine();
        }
        if (report.Untracked.Count > 0)
        {
            _out.WriteLine("Untracked files:");
            foreach (var f in report.Untracked) _out.WriteLine($"\t{f}");
            _out.WriteLine();
        }
        if (report.Staged.Count == 0 && report.Unstaged.Count == 0 && report.Untracked.Count == 0)
            _out.WriteLine("nothing to commit, working tree clean");
    }

    /// <summary>Unified diff of the work tree against the index (or, with cached, the index against HEAD).</summary>
    public void DiffChanges(bool cached)
    {
        var index = Index.Read(_repo);
        if (cached)
        {
            var head = _repo.HeadCommit();
            var headFiles = head is null
                ? new SortedDictionary<string, (string Mode, string Sha)>(StringComparer.Ordinal)
                : Tree.Flatten(_repo, Commit.Parse(Objects.Read(_repo, head).Content).Tree);
            var paths = new SortedSet<string>(headFiles.Keys.Concat(index.Entries.Keys), StringComparer.Ordinal);
            foreach (var p in paths)
            {
                var a = headFiles.TryGetValue(p, out var h) ? Blob(h.Sha) : "";
                var b = index.Entries.TryGetValue(p, out var e) ? Blob(e.Sha) : "";
                PrintDiff(p, a, b, headFiles.ContainsKey(p), index.Entries.ContainsKey(p));
            }
        }
        else
        {
            foreach (var kv in index.Entries)
            {
                var full = Path.Combine(_repo.WorkTree, kv.Key);
                var b = File.Exists(full) ? File.ReadAllText(full) : "";
                PrintDiff(kv.Key, Blob(kv.Value.Sha), b, true, File.Exists(full));
            }
        }
    }

    private string Blob(string sha) => Encoding.UTF8.GetString(Objects.Read(_repo, sha).Content);

    private void PrintDiff(string path, string a, string b, bool existsA, bool existsB)
    {
        if (a == b) return;
        _out.WriteLine($"diff --git a/{path} b/{path}");
        if (!existsA) _out.WriteLine("new file");
        if (!existsB) _out.WriteLine("deleted file");
        _out.Write(Diff.Unified(existsA ? "a/" + path : "/dev/null", existsB ? "b/" + path : "/dev/null", a, b));
    }

    public void Branch(string? name, bool delete = false)
    {
        if (name is null)
        {
            var current = _repo.CurrentBranch();
            foreach (var b in _repo.Branches()) _out.WriteLine((b == current ? "* " : "  ") + b);
            return;
        }
        var path = Path.Combine(_repo.GitDir, "refs", "heads", name);
        if (delete)
        {
            if (name == _repo.CurrentBranch()) throw new GitException($"cannot delete the current branch '{name}'");
            if (!File.Exists(path)) throw new GitException($"branch '{name}' not found");
            File.Delete(path);
            _out.WriteLine($"Deleted branch {name}");
            return;
        }
        if (File.Exists(path)) throw new GitException($"a branch named '{name}' already exists");
        var head = _repo.HeadCommit() ?? throw new GitException("not a valid object name: 'HEAD'");
        _repo.WriteRef("refs/heads/" + name, head);
    }

    public void Checkout(string target, bool createBranch = false)
    {
        if (createBranch)
        {
            Branch(target);
            _repo.PointHeadAtBranch(target);
            _out.WriteLine($"Switched to a new branch '{target}'");
            return;
        }
        var report = ComputeStatus();
        if (report.Unstaged.Count > 0 || report.Staged.Count > 0)
            throw new GitException("your local changes would be overwritten by checkout; commit them first");

        var isBranch = File.Exists(Path.Combine(_repo.GitDir, "refs", "heads", target));
        var sha = _repo.ResolveCommit(target);
        var commit = Commit.Parse(Objects.Read(_repo, sha).Content);
        var files = Tree.Flatten(_repo, commit.Tree);

        // remove tracked files that disappear, write the ones from the target
        var index = Index.Read(_repo);
        foreach (var path in index.Entries.Keys.Where(p => !files.ContainsKey(p)))
        {
            var full = Path.Combine(_repo.WorkTree, path);
            if (File.Exists(full)) File.Delete(full);
            DeleteEmptyParents(full);
        }
        var newIndex = new Index();
        foreach (var kv in files)
        {
            var full = Path.Combine(_repo.WorkTree, kv.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Objects.Read(_repo, kv.Value.Sha).Content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(full, kv.Value.Mode == "100755"
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            newIndex.Entries[kv.Key] = Index.FromFile(_repo, kv.Key);
        }
        newIndex.Write(_repo);

        if (isBranch)
        {
            _repo.PointHeadAtBranch(target);
            _out.WriteLine($"Switched to branch '{target}'");
        }
        else
        {
            _repo.DetachHead(sha);
            _out.WriteLine($"HEAD is now at {sha[..7]} {commit.Message.Split('\n')[0]}");
        }
    }

    private void DeleteEmptyParents(string full)
    {
        var dir = Path.GetDirectoryName(full);
        while (dir is not null && dir.Length > _repo.WorkTree.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    public void Tag(string? name)
    {
        var dir = Path.Combine(_repo.GitDir, "refs", "tags");
        if (name is null)
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)) _out.WriteLine(Path.GetFileName(f));
            return;
        }
        var head = _repo.HeadCommit() ?? throw new GitException("not a valid object name: 'HEAD'");
        if (File.Exists(Path.Combine(dir, name))) throw new GitException($"tag '{name}' already exists");
        _repo.WriteRef("refs/tags/" + name, head);
    }
}
