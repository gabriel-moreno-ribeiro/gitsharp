using GitSharp;

const string usage = """
    usage: gitsharp <command> [args]

    porcelain:
      init [dir]                 create an empty repository
      add <path>...              stage files (directories and . are expanded)
      rm [--cached] <path>...    unstage (and delete) files
      commit -m <message>        record the index as a commit
      status                     show staged, unstaged and untracked changes
      log [--oneline] [-n N]     list commits from HEAD
      diff [--cached]            work tree vs index (or index vs HEAD)
      branch [-d] [name]         list, create or delete branches
      checkout [-b] <target>     switch branches or check out a commit
      tag [name]                 list or create lightweight tags

    plumbing:
      hash-object [-w] <file>    compute (and store) a blob id
      cat-file (-t|-s|-p) <id>   inspect an object
      write-tree                 write the index as a tree object
      ls-tree [-r] <id>          list a tree
      ls-files                   list the index
    """;

if (args.Length == 0) { Console.Error.WriteLine(usage); return 2; }

try
{
    var command = args[0];
    var rest = args[1..];
    if (command == "init")
    {
        var repo = Repository.Init(rest.Length > 0 ? rest[0] : ".");
        Console.WriteLine($"Initialized empty Git repository in {repo.GitDir}");
        return 0;
    }
    if (command is "-h" or "--help" or "help") { Console.WriteLine(usage); return 0; }

    var r = Repository.Discover(Directory.GetCurrentDirectory());
    var cmd = new Commands(r, Console.Out);
    switch (command)
    {
        case "hash-object":
            cmd.HashObject(rest.Last(), rest.Contains("-w"));
            break;
        case "cat-file":
            if (rest.Length != 2) throw new GitException("usage: cat-file (-t|-s|-p) <object>");
            cmd.CatFile(rest[0], rest[1]);
            break;
        case "write-tree":
            cmd.WriteTree();
            break;
        case "ls-tree":
            cmd.LsTree(rest.Last(), rest.Contains("-r"));
            break;
        case "ls-files":
            cmd.LsFiles();
            break;
        case "add":
            if (rest.Length == 0) throw new GitException("nothing specified, nothing added");
            cmd.Add(rest);
            break;
        case "rm":
            cmd.Remove(rest.Where(a => a != "--cached"), rest.Contains("--cached"));
            break;
        case "commit":
        {
            int m = Array.IndexOf(rest, "-m");
            if (m < 0 || m + 1 >= rest.Length) throw new GitException("usage: commit -m <message>");
            cmd.CommitChanges(rest[m + 1]);
            break;
        }
        case "status":
            cmd.Status();
            break;
        case "log":
        {
            int n = Array.IndexOf(rest, "-n");
            cmd.Log(n >= 0 && n + 1 < rest.Length ? int.Parse(rest[n + 1]) : int.MaxValue, rest.Contains("--oneline"));
            break;
        }
        case "diff":
            cmd.DiffChanges(rest.Contains("--cached"));
            break;
        case "branch":
            cmd.Branch(rest.FirstOrDefault(a => a != "-d"), rest.Contains("-d"));
            break;
        case "checkout":
        {
            var target = rest.FirstOrDefault(a => a != "-b") ?? throw new GitException("usage: checkout [-b] <target>");
            cmd.Checkout(target, rest.Contains("-b"));
            break;
        }
        case "tag":
            cmd.Tag(rest.FirstOrDefault());
            break;
        default:
            Console.Error.WriteLine($"gitsharp: '{command}' is not a gitsharp command.");
            Console.Error.WriteLine(usage);
            return 2;
    }
    return 0;
}
catch (GitException e)
{
    Console.Error.WriteLine($"fatal: {e.Message}");
    return 1;
}
