# gitsharp

A Git implementation written from scratch in C#. It reads and writes the
real Git on-disk formats, so a repository created with gitsharp can be opened
with `git` and vice versa: loose objects (zlib-compressed, SHA-1 addressed),
trees, commits, refs, and the binary `.git/index` file.

```sh
dotnet build -c Release
alias gitsharp="dotnet $PWD/GitSharp/bin/Release/net7.0/gitsharp.dll"

gitsharp init
echo hello > hello.txt
gitsharp add .
gitsharp commit -m "first commit"
gitsharp log --oneline
git log --oneline          # real git agrees
git fsck --strict          # and validates every object we wrote
```

## Commands

| Porcelain | |
| --- | --- |
| `init [dir]` | create an empty repository |
| `add <path>...` | stage files (`.` and directories are expanded, deletions are staged too) |
| `rm [--cached] <path>...` | unstage and delete |
| `commit -m <msg>` | write tree and commit objects, advance the branch |
| `status` | staged / unstaged / untracked, computed against HEAD, index and work tree |
| `log [--oneline] [-n N]` | walk parents from HEAD |
| `diff [--cached]` | unified diff of work tree vs index, or index vs HEAD |
| `branch [-d] [name]` | list, create, delete |
| `checkout [-b] <target>` | switch branch, create branch, or detach at a commit |
| `tag [name]` | lightweight tags |

| Plumbing | |
| --- | --- |
| `hash-object [-w] <file>` | blob id, optionally stored |
| `cat-file (-t\|-s\|-p) <id>` | type, size or pretty-printed content (abbreviated ids work) |
| `write-tree` | tree object from the index |
| `ls-tree [-r] <id>` | list a tree or a commit's tree |
| `ls-files` | list the index |

## How it works

- **Objects** (`Objects.cs`): an object is `"<type> <size>\0<content>"`. Its
  SHA-1 is the id; it is stored zlib-compressed under
  `.git/objects/aa/bbbb...`. Trees are `"<mode> <name>\0<20 raw sha bytes>"`
  records sorted the way Git sorts them (directories as if they had a
  trailing `/`). Commits are text headers plus a message.
- **Index** (`Index.cs`): the `DIRC` version 2 format, big-endian stat
  fields, 20-byte SHA, flags with the path length, 8-byte padding and a
  SHA-1 trailer over the whole file. Git's optional extensions are skipped.
- **Repository** (`Repository.cs`): finds `.git` by walking up, reads and
  writes refs and symbolic `HEAD`, resolves branches, tags, `HEAD` and
  abbreviated ids, and reads `user.name` / `user.email` from the config.
- **Commands** (`Commands.cs`): `status` flattens the HEAD tree and compares
  it with the index (staged changes), then hashes work tree files and compares
  them with the index (unstaged changes). `checkout` refuses to run over
  local changes, rewrites the work tree from the target tree, rebuilds the
  index and repoints `HEAD`.
- **Diff** (`Diff.cs`): longest-common-subsequence line diff rendered as
  unified hunks with three lines of context.

## Tests

```sh
dotnet test
```

The xunit suite covers object hashing against known Git ids, tree ordering,
index round-trips, every command, and interoperability both ways: the real
`git` binary reads our index, commits and tags (`git status`, `git log`,
`git show`, `git fsck --strict`), and gitsharp reads repositories made by
`git`.

## License

MIT
