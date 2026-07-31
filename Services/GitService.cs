using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DevezCode.Services;

/// <summary>git CLI 를 프로세스로 호출해 상태/diff 를 읽는 얇은 래퍼. 서버/DB 없이 로컬 git 만 사용.</summary>
public static class GitService
{
    /// <summary>git 실행 결과.</summary>
    public readonly record struct GitResult(bool Ok, string Output, string Error);

    /// <summary>지정 저장소에서 git 명령을 실행하고 표준출력을 반환(UTF-8, 비ASCII 파일명 보존).</summary>
    public static async Task<GitResult> RunAsync(string repoDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 비ASCII 파일명이 \xxx 로 이스케이프되지 않도록.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new GitResult(false, "", "git 프로세스를 시작하지 못했습니다.");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var stdout = await outTask;
            var stderr = await errTask;
            return new GitResult(p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            // git 미설치(파일 없음) 등.
            return new GitResult(false, "", ex.Message);
        }
    }

    /// <summary>해당 폴더가 git 작업 트리 안인지.</summary>
    public static async Task<bool> IsRepoAsync(string repoDir)
    {
        if (string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) return false;
        var r = await RunAsync(repoDir, "rev-parse", "--is-inside-work-tree");
        return r.Ok && r.Output.Trim() == "true";
    }

    /// <summary>origin 원격 주소를 기본 브라우저에서 열 수 있는 HTTP(S) 주소로 변환한다.
    /// HTTPS 자격정보는 제거하고, 일반적인 SSH/scp 형식은 https://host/path 로 바꾼다.</summary>
    public static async Task<string?> GetOriginWebUrlAsync(string repoDir)
    {
        if (string.IsNullOrWhiteSpace(repoDir) || !Directory.Exists(repoDir)) return null;
        var result = await RunAsync(repoDir, "remote", "get-url", "origin");
        if (!result.Ok) return null;
        var remote = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();
        return ToWebUrl(remote);
    }

    private static string? ToWebUrl(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        remote = remote.Trim();

        // git@github.com:owner/repo.git 같은 scp 형식.
        if (!remote.Contains("://", StringComparison.Ordinal))
        {
            int at = remote.IndexOf('@');
            int colon = at >= 0 ? remote.IndexOf(':', at + 1) : -1;
            if (at > 0 && colon > at + 1)
                return BuildHttpsUrl(remote[(at + 1)..colon], remote[(colon + 1)..]);
            return null; // 로컬 경로 등은 웹에서 열 수 없음.
        }

        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is "http" or "https")
        {
            var builder = new UriBuilder(uri)
            {
                UserName = "",
                Password = "",
                Query = "",
                Fragment = "",
                Path = TrimGitSuffix(uri.AbsolutePath),
            };
            return builder.Uri.AbsoluteUri.TrimEnd('/');
        }

        if (uri.Scheme is "ssh" or "git")
            return BuildHttpsUrl(uri.Host, uri.AbsolutePath);

        return null;
    }

    private static string? BuildHttpsUrl(string host, string path)
    {
        host = host.Trim();
        path = TrimGitSuffix(path).Trim('/');
        if (host.Length == 0 || path.Length == 0) return null;
        return new UriBuilder(Uri.UriSchemeHttps, host) { Path = path }
            .Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string TrimGitSuffix(string path)
    {
        path = path.Trim().Replace('\\', '/').TrimEnd('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>porcelain=v1 의 XY 상태를 staged(X)·unstaged(Y) 로 분리 수집.</summary>
    public static async Task<Models.GitStatus> StatusAsync(string repoDir)
    {
        var res = new Models.GitStatus();
        var r = await RunAsync(repoDir, "status", "--porcelain=v1", "-u");
        if (!r.Ok) return res;
        foreach (var raw in r.Output.Split('\n'))
        {
            if (raw.Length < 4) continue;
            var x = raw[0]; var y = raw[1];
            var rest = raw[3..].Trim();
            if (rest.Length == 0) continue;
            var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rest = rest[(arrow + 4)..];

            if (x == '?' && y == '?')
            {
                // VS와 동일하게 신규 파일은 A(추가)로 표시하되, 실제 미추적 여부는 IsUntracked로 별도 보존한다.
                res.Unstaged.Add(new Models.GitChange { Status = "A", Path = rest, IsUntracked = true, IsStaged = false });
                continue;
            }

            // 병합 충돌은 XY 양쪽에 상태가 있어도 한 파일을 두 목록에 중복 표시하지 않는다.
            // porcelain v1 이 정의한 unmerged 조합을 VS와 같이 U 하나로 표시한다.
            if (IsUnmerged(x, y))
            {
                res.Unstaged.Add(new Models.GitChange { Status = "U", Path = rest, IsStaged = false });
                continue;
            }

            if (x != ' ' && x != '?')
                res.Staged.Add(new Models.GitChange { Status = MapCode(x), Path = rest, IsStaged = true });
            if (y != ' ' && y != '?')
                res.Unstaged.Add(new Models.GitChange { Status = MapCode(y), Path = rest, IsStaged = false });
        }
        return res;

        static string MapCode(char c) => c switch
        {
            'A' => "A", 'D' => "D", 'M' => "M", 'R' => "R",
            'C' => "C", 'T' => "T", 'U' => "U", _ => "M"
        };

        static bool IsUnmerged(char x, char y)
            => (x, y) is ('D', 'D') or ('A', 'U') or ('U', 'D') or ('U', 'A')
                or ('D', 'U') or ('A', 'A') or ('U', 'U');
    }

    /// <summary>git show &lt;rev&gt;:&lt;path&gt; — rev 예: "HEAD", ":"(인덱스). 실패/부재 시 빈 문자열.</summary>
    public static async Task<string> ShowFileAsync(string repoDir, string rev, string path)
    {
        // rev=":" 는 인덱스 → ":path"(HEAD 는 "HEAD:path"). "{rev}:{path}" 로 조합하면 "::path" 가 돼
        // git 이 fatal 로 빈 문자열을 반환하던 버그 수정.
        var obj = rev.EndsWith(":") ? rev + path : $"{rev}:{path}";
        var r = await RunAsync(repoDir, "show", obj);
        return r.Ok ? r.Output : "";
    }

    public static Task<GitResult> StageAsync(string repoDir, string path)
        => RunAsync(repoDir, "add", "--", path);

    /// <summary>전체 변경 스테이징(git add -A) — staged 항목이 없을 때 커밋 폴백용.</summary>
    public static Task<GitResult> StageAllAsync(string repoDir)
        => RunAsync(repoDir, "add", "-A");

    public static async Task<GitResult> UnstageAsync(string repoDir, string path)
    {
        var r = await RunAsync(repoDir, "restore", "--staged", "--", path);
        if (r.Ok) return r;
        return await RunAsync(repoDir, "reset", "-q", "--", path); // 신규 파일 등 폴백
    }

    /// <summary>전체 스테이징 해제. restore 미지원/초기 저장소는 reset 으로 폴백.</summary>
    public static async Task<GitResult> UnstageAllAsync(string repoDir)
    {
        var r = await RunAsync(repoDir, "restore", "--staged", "--", ":/");
        if (r.Ok) return r;
        return await RunAsync(repoDir, "reset", "-q");
    }

    /// <summary>변경 취소. 추적 파일은 checkout, untracked 는 파일 삭제.</summary>
    public static async Task<GitResult> DiscardAsync(string repoDir, string path, bool untracked)
    {
        if (untracked)
        {
            try
            {
                var full = System.IO.Path.Combine(repoDir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
                return new GitResult(true, "", "");
            }
            catch (Exception ex) { return new GitResult(false, "", ex.Message); }
        }
        return await RunAsync(repoDir, "checkout", "--", path);
    }

    /// <summary>폴더(경로 접두사) 단위 변경 취소. folderPath="" 는 저장소 전체.
    /// 하위 추적 파일은 folder pathspec 로 한 번에 checkout, untracked 신규 파일은 개별 삭제한다.
    /// changes 는 해당 폴더 아래 작업트리 변경 리프 목록(추적/untracked 판별용).</summary>
    public static async Task<GitResult> DiscardFolderAsync(
        string repoDir, string folderPath, IReadOnlyList<Models.GitChange> changes)
    {
        // 추적된 수정/삭제 파일이 하나라도 있으면 폴더 경로로 index 기준 복원.
        if (changes.Any(c => !c.IsUntracked))
        {
            var spec = string.IsNullOrEmpty(folderPath) ? "." : folderPath;
            var co = await RunAsync(repoDir, "checkout", "--", spec);
            if (!co.Ok) return co;
        }

        // untracked(신규) 파일은 checkout 대상이 아니므로 개별 삭제.
        foreach (var c in changes.Where(c => c.IsUntracked))
        {
            try
            {
                var full = Path.Combine(repoDir, c.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex) { return new GitResult(false, "", ex.Message); }
        }
        return new GitResult(true, "", "");
    }

    public static Task<GitResult> CommitAsync(string repoDir, string message)
        => RunAsync(repoDir, "commit", "-m", message);

    public static Task<GitResult> PullAsync(string repoDir) => RunAsync(repoDir, "pull");
    public static Task<GitResult> FetchAsync(string repoDir) => RunAsync(repoDir, "fetch");

    public static async Task<GitResult> PushAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        if (!br.Ok) return br;
        var branch = br.Output.Trim();
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            return new GitResult(false, "", "현재 브랜치가 분리된 상태라 푸시할 수 없습니다.");
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        return up.Ok ? await RunAsync(repoDir, "push")
                     : await RunAsync(repoDir, "push", "-u", "origin", branch);
    }

    public static async Task<bool> HasIdentityAsync(string repoDir)
    {
        var n = await RunAsync(repoDir, "config", "user.name");
        var e = await RunAsync(repoDir, "config", "user.email");
        return n.Ok && !string.IsNullOrWhiteSpace(n.Output) && e.Ok && !string.IsNullOrWhiteSpace(e.Output);
    }

    public static async Task<Models.BranchState> BranchStateAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = br.Ok ? br.Output.Trim() : null;
        if (string.IsNullOrEmpty(branch) || branch == "HEAD") branch = null;
        if (branch == null) return new Models.BranchState();
        var baseBranch = await DefaultBaseBranchAsync(repoDir);
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!up.Ok) return new Models.BranchState { Branch = branch, BaseBranch = baseBranch, HasUpstream = false };
        var upstream = up.Output.Trim();
        var counts = await RunAsync(repoDir, "rev-list", "--left-right", "--count", "@{u}...HEAD");
        int behind = 0, ahead = 0;
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Ok && parts.Length == 2) { int.TryParse(parts[0], out behind); int.TryParse(parts[1], out ahead); }
        return new Models.BranchState
        {
            Branch = branch,
            Upstream = upstream,
            BaseBranch = baseBranch,
            HasUpstream = true,
            Ahead = ahead,
            Behind = behind
        };
    }

    private static async Task<string?> DefaultBaseBranchAsync(string repoDir)
    {
        var head = await RunAsync(repoDir, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (head.Ok)
        {
            var value = head.Output.Trim();
            if (value.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)) value = value[7..];
            if (value.Length > 0) return value;
        }

        foreach (var candidate in new[] { "main", "master", "develop" })
        {
            var exists = await RunAsync(repoDir, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{candidate}");
            if (exists.Ok) return candidate;
        }
        return null;
    }

    /// <summary>현재 브랜치의 커밋 내역을 최신순으로 읽는다.</summary>
    public static async Task<Models.GitHistoryPage> CommitHistoryAsync(string repoDir, int skip, int take)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var head = await RunAsync(repoDir, "rev-parse", "--verify", "HEAD");
        if (!head.Ok)
        {
            var repo = await RunAsync(repoDir, "rev-parse", "--is-inside-work-tree");
            if (repo.Ok && repo.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return new Models.GitHistoryPage { Ok = true };
            return new Models.GitHistoryPage
            {
                Ok = false,
                Error = "커밋 내역을 불러오지 못했습니다. 잠시 후 다시 시도하세요."
            };
        }

        const string format = "%H%x1f%h%x1f%an%x1f%aI%x1f%P%x1f%D%x1f%s%x1f%b%x1e";
        var result = await RunAsync(
            repoDir,
            "log",
            "--topo-order",
            "--decorate=short",
            $"--skip={skip}",
            $"--max-count={take + 1}",
            $"--pretty=format:{format}");
        if (!result.Ok)
        {
            DiagLog.Write($"Git history load failed: {result.Error}");
            return new Models.GitHistoryPage
            {
                Ok = false,
                Error = "커밋 내역을 불러오지 못했습니다. 잠시 후 다시 시도하세요."
            };
        }

        var commits = new List<Models.GitCommitEntry>();
        foreach (var rawRecord in result.Output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = rawRecord.TrimStart('\r', '\n');
            var fields = record.Split('\x1f');
            if (fields.Length < 8) continue;
            _ = DateTimeOffset.TryParse(fields[3].Trim(), out var authoredAt);
            var parents = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new Models.GitCommitEntry
            {
                Sha = fields[0].Trim(),
                ShortSha = fields[1].Trim(),
                AuthorName = fields[2].Trim(),
                AuthoredAt = authoredAt,
                ParentCount = parents.Length,
                Refs = fields[5].Trim(),
                Subject = fields[6].Trim(),
                Body = fields[7].Trim(),
            });
        }

        var hasMore = commits.Count > take;
        if (hasMore) commits.RemoveRange(take, commits.Count - take);
        return new Models.GitHistoryPage { Ok = true, Commits = commits, HasMore = hasMore };
    }

    /// <summary>선택한 커밋의 본문과 파일별 추가/삭제 통계를 읽는다.</summary>
    public static async Task<(bool Ok, string Body, List<Models.GitCommitFile> Files)> CommitDetailsAsync(
        string repoDir,
        string sha)
    {
        var result = await RunAsync(
            repoDir,
            "show",
            "--format=%B%x1e",
            "--numstat",
            "--no-renames",
            "--no-color",
            sha);
        if (!result.Ok) return (false, "", new List<Models.GitCommitFile>());

        var marker = result.Output.IndexOf('\x1e');
        var body = marker >= 0 ? result.Output[..marker].Trim() : "";
        var stats = marker >= 0 ? result.Output[(marker + 1)..] : result.Output;
        var files = new List<Models.GitCommitFile>();
        foreach (var rawLine in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split(new[] { '\t' }, 3, StringSplitOptions.None);
            if (parts.Length != 3) continue;
            int? additions = int.TryParse(parts[0], out var added) ? added : null;
            int? deletions = int.TryParse(parts[1], out var deleted) ? deleted : null;
            files.Add(new Models.GitCommitFile
            {
                Path = parts[2],
                Additions = additions,
                Deletions = deletions,
            });
        }
        return (true, body, files);
    }
}
