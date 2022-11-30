# gitsharp

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

**EN:** a Git implementation in C# that speaks the real on-disk formats (loose zlib objects, trees with Git's ordering, commits, refs, the binary `DIRC` index), with porcelain (`add`, `commit`, `status`, `log`, `diff`, `branch`, `checkout`, `tag`) and plumbing commands. The xunit suite checks interoperability in both directions with the real `git`. MIT.
