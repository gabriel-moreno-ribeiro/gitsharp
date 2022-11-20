using System.Text;

namespace GitSharp;

/// <summary>Locates and manipulates the .git directory: refs, HEAD, config.</summary>
public sealed class Repository
{
    public string WorkTree { get; }
    public string GitDir { get; }

    public Repository(string workTree)
    {
        WorkTree = Path.GetFullPath(workTree);
        GitDir = Path.Combine(WorkTree, ".git");
    }

    /// <summary>Walks up from a directory until a .git folder is found.</summary>
    public static Repository Discover(string start)
    {
        var dir = Path.GetFullPath(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return new Repository(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new GitException("not a git repository (or any of the parent directories)");
    }

    public static Repository Init(string workTree)
    {
        var repo = new Repository(workTree);
        if (Directory.Exists(repo.GitDir)) throw new GitException($"reinitialized existing repository in {repo.GitDir}");
        Directory.CreateDirectory(Path.Combine(repo.GitDir, "objects", "info"));
        Directory.CreateDirectory(Path.Combine(repo.GitDir, "objects", "pack"));
        Directory.CreateDirectory(Path.Combine(repo.GitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(repo.GitDir, "refs", "tags"));
        File.WriteAllText(Path.Combine(repo.GitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo.GitDir, "config"),
            "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n");
        File.WriteAllText(Path.Combine(repo.GitDir, "description"), "Unnamed repository; edit this file to name the repository.\n");
        return repo;
    }

    public string ObjectPath(string sha) => Path.Combine(GitDir, "objects", sha[..2], sha[2..]);

    /// <summary>Expands an abbreviated object id to the full 40 characters.</summary>
    public string ResolveObjectId(string prefix)
    {
        if (prefix.Length == 40) return prefix;
        if (prefix.Length < 4) throw new GitException($"ambiguous object id {prefix}");
        var dir = Path.Combine(GitDir, "objects", prefix[..2]);
        if (!Directory.Exists(dir)) throw new GitException($"object {prefix} not found");
        var matches = Directory.GetFiles(dir)
            .Select(f => prefix[..2] + Path.GetFileName(f))
            .Where(s => s.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0) throw new GitException($"object {prefix} not found");
        if (matches.Count > 1) throw new GitException($"ambiguous object id {prefix}");
        return matches[0];
    }

    // ---- refs -------------------------------------------------------------

    public string? ReadRef(string name)
    {
        var path = Path.Combine(GitDir, name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        var content = File.ReadAllText(path).Trim();
        if (content.StartsWith("ref: ")) return ReadRef(content[5..]);
        return content;
    }

    public void WriteRef(string name, string sha)
    {
        var path = Path.Combine(GitDir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sha + "\n");
    }

    /// <summary>The branch HEAD points to (e.g. "main"), or null when detached.</summary>
    public string? CurrentBranch()
    {
        var head = File.ReadAllText(Path.Combine(GitDir, "HEAD")).Trim();
        return head.StartsWith("ref: refs/heads/") ? head["ref: refs/heads/".Length..] : null;
    }

    public string? HeadCommit() => ReadRef("HEAD");

    public void UpdateHead(string sha)
    {
        var branch = CurrentBranch();
        if (branch is not null) WriteRef("refs/heads/" + branch, sha);
        else File.WriteAllText(Path.Combine(GitDir, "HEAD"), sha + "\n");
    }

    public void PointHeadAtBranch(string branch)
    {
        File.WriteAllText(Path.Combine(GitDir, "HEAD"), $"ref: refs/heads/{branch}\n");
    }

    public void DetachHead(string sha)
    {
        File.WriteAllText(Path.Combine(GitDir, "HEAD"), sha + "\n");
    }

    public IEnumerable<string> Branches()
    {
        var dir = Path.Combine(GitDir, "refs", "heads");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return Path.GetRelativePath(dir, f).Replace('\\', '/');
        }
    }

    /// <summary>Resolves a branch name, tag, HEAD or (abbreviated) sha to a commit id.</summary>
    public string ResolveCommit(string name)
    {
        if (name == "HEAD") return HeadCommit() ?? throw new GitException("HEAD does not point to a commit yet");
        foreach (var candidate in new[] { "refs/heads/" + name, "refs/tags/" + name, name })
        {
            var sha = ReadRef(candidate);
            if (sha is not null) return sha;
        }
        if (name.Length >= 4 && name.All(Uri.IsHexDigit)) return ResolveObjectId(name.ToLowerInvariant());
        throw new GitException($"unknown revision '{name}'");
    }

    // ---- config -------------------------------------------------------------

    /// <summary>Reads a value like user.name from .git/config, ~/.gitconfig or the environment.</summary>
    public string? Config(string section, string key)
    {
        foreach (var file in new[] { Path.Combine(GitDir, "config"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig") })
        {
            if (!File.Exists(file)) continue;
            string? current = null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { current = line.Trim('[', ']').Trim().ToLowerInvariant(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0 || current != section) continue;
                if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    public (string Name, string Email) Identity()
    {
        var name = Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME") ?? Config("user", "name") ?? Environment.UserName;
        var email = Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL") ?? Config("user", "email") ?? $"{Environment.UserName}@{Environment.MachineName}";
        return (name, email);
    }

    public string RelativePath(string absolute)
    {
        return Path.GetRelativePath(WorkTree, Path.GetFullPath(absolute)).Replace('\\', '/');
    }

    /// <summary>All files in the work tree (relative, forward slashes), skipping .git.</summary>
    public IEnumerable<string> WorkTreeFiles()
    {
        return Directory.EnumerateFiles(WorkTree, "*", SearchOption.AllDirectories)
            .Select(RelativePath)
            .Where(p => p != ".git" && !p.StartsWith(".git/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    public static string FileMode(string path)
    {
        if (OperatingSystem.IsWindows()) return "100644";
        var mode = File.GetUnixFileMode(path);
        return (mode & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
    }
}
