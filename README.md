# gitsharp

> 🇺🇸 [English version below](#english)

Git em C#. Não um wrapper: ele lê e escreve os formatos de disco de verdade, então um repositório criado com o gitsharp abre no `git` e vice-versa. Objetos soltos (zlib, endereçados por SHA-1), trees, commits, refs e o arquivo binário `.git/index`.

Foi o projeto que me fez parar de ter medo do Git. Depois de escrever o `status` na mão você entende exatamente o que é "staged" e o que é "working tree", e nunca mais precisa daquele diagrama.

```sh
dotnet build -c Release
alias gitsharp="dotnet $PWD/GitSharp/bin/Release/net7.0/gitsharp.dll"

gitsharp init
echo hello > hello.txt
gitsharp add .
gitsharp commit -m "first commit"
gitsharp log --oneline
git log --oneline          # o git de verdade concorda
git fsck --strict          # e valida cada objeto que a gente escreveu
```

| Porcelana | |
| --- | --- |
| `init [dir]` | cria um repositório vazio |
| `add <path>...` | stage (expande `.` e diretórios, inclui remoções) |
| `rm [--cached] <path>...` | unstage e apaga |
| `commit -m <msg>` | escreve tree e commit, avança o branch |
| `status` | staged / unstaged / untracked contra HEAD, index e working tree |
| `log [--oneline] [-n N]` | anda pelos pais a partir do HEAD |
| `diff [--cached]` | diff unificado |
| `branch [-d] [name]`, `checkout [-b] <alvo>`, `tag [name]` | |

| Plumbing | |
| --- | --- |
| `hash-object [-w]`, `cat-file (-t\|-s\|-p)`, `write-tree`, `ls-tree [-r]`, `ls-files` | |

## Os formatos

- **Objetos** (`Objects.cs`): `"<tipo> <tamanho>\0<conteúdo>"`, SHA-1 é o id, guardado com zlib em `.git/objects/aa/bbbb...`. Trees são registros `"<modo> <nome>\0<20 bytes de sha>"` ordenados do jeito que o Git ordena (diretórios como se tivessem `/` no fim, o que me custou uma tarde).
- **Index** (`Index.cs`): o formato `DIRC` versão 2, campos de stat em big-endian, SHA de 20 bytes, flags com o tamanho do path, padding de 8 bytes e um SHA-1 no fim sobre o arquivo inteiro.
- **Repository** (`Repository.cs`): acha o `.git` subindo diretórios, lê e escreve refs e o `HEAD` simbólico, resolve branches, tags, ids abreviados, e `user.name`/`user.email` do config.
- **Diff** (`Diff.cs`): LCS por linha renderizado como hunks unificados com três linhas de contexto.

Testes: `dotnet test`. A suíte xunit compara hashes com ids conhecidos do Git, testa cada comando e a interoperabilidade nos dois sentidos: o `git` de verdade lê nosso index, commits e tags (`git status`, `git log`, `git show`, `git fsck --strict`), e o gitsharp lê repositórios feitos pelo `git`.

---

## English

Git in C#. Not a wrapper: it reads and writes the real on-disk formats, so a repository created with gitsharp opens in `git` and vice versa. Loose objects (zlib, addressed by SHA-1), trees, commits, refs and the binary `.git/index` file.

It was the project that made me stop being afraid of Git. After writing `status` by hand you understand exactly what "staged" and "working tree" are, and you never need that diagram again.

```sh
dotnet build -c Release
alias gitsharp="dotnet $PWD/GitSharp/bin/Release/net7.0/gitsharp.dll"

gitsharp init
echo hello > hello.txt
gitsharp add .
gitsharp commit -m "first commit"
gitsharp log --oneline
git log --oneline          # the real git agrees
git fsck --strict          # and validates every object we wrote
```

| Porcelain | |
| --- | --- |
| `init [dir]` | creates an empty repository |
| `add <path>...` | stage (expands `.` and directories, includes removals) |
| `rm [--cached] <path>...` | unstage and delete |
| `commit -m <msg>` | writes tree and commit, advances the branch |
| `status` | staged / unstaged / untracked against HEAD, index and working tree |
| `log [--oneline] [-n N]` | walks the parents starting from HEAD |
| `diff [--cached]` | unified diff |
| `branch [-d] [name]`, `checkout [-b] <target>`, `tag [name]` | |

| Plumbing | |
| --- | --- |
| `hash-object [-w]`, `cat-file (-t\|-s\|-p)`, `write-tree`, `ls-tree [-r]`, `ls-files` | |

## The formats

- **Objects** (`Objects.cs`): `"<type> <size>\0<content>"`, the SHA-1 is the id, stored with zlib in `.git/objects/aa/bbbb...`. Trees are `"<mode> <name>\0<20 bytes of sha>"` records sorted the way Git sorts them (directories as if they had a `/` at the end, which cost me an afternoon).
- **Index** (`Index.cs`): the `DIRC` format version 2, big-endian stat fields, 20-byte SHA, flags with the path length, 8-byte padding and a SHA-1 at the end over the whole file.
- **Repository** (`Repository.cs`): finds `.git` walking up directories, reads and writes refs and the symbolic `HEAD`, resolves branches, tags, abbreviated ids, and `user.name`/`user.email` from the config.
- **Diff** (`Diff.cs`): line-based LCS rendered as unified hunks with three lines of context.

Tests: `dotnet test`. The xunit suite compares hashes with known Git ids, tests every command and the interoperability in both directions: the real `git` reads our index, commits and tags (`git status`, `git log`, `git show`, `git fsck --strict`), and gitsharp reads repositories made by `git`.

MIT.
