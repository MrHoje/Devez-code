using System.Diagnostics;
using System.IO;
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

    /// <summary>porcelain=v2 로 staged(X)·unstaged(Y) 를 분리 수집.</summary>
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
                res.Unstaged.Add(new Models.GitChange { Status = "?", Path = rest, IsUntracked = true, IsStaged = false });
                continue;
            }
            if (x != ' ' && x != '?')
                res.Staged.Add(new Models.GitChange { Status = MapCode(x), Path = rest, IsStaged = true });
            if (y != ' ' && y != '?')
                res.Unstaged.Add(new Models.GitChange { Status = MapCode(y), Path = rest, IsStaged = false });
        }
        return res;

        static string MapCode(char c) => c switch
        { 'A' => "A", 'D' => "D", 'M' => "M", 'R' => "R", 'C' => "R", _ => "M" };
    }

    /// <summary>git show &lt;rev&gt;:&lt;path&gt; — rev 예: "HEAD", ":"(인덱스). 실패/부재 시 빈 문자열.</summary>
    public static async Task<string> ShowFileAsync(string repoDir, string rev, string path)
    {
        var r = await RunAsync(repoDir, "show", $"{rev}:{path}");
        return r.Ok ? r.Output : "";
    }

    public static Task<GitResult> StageAsync(string repoDir, string path)
        => RunAsync(repoDir, "add", "--", path);

    public static async Task<GitResult> UnstageAsync(string repoDir, string path)
    {
        var r = await RunAsync(repoDir, "restore", "--staged", "--", path);
        if (r.Ok) return r;
        return await RunAsync(repoDir, "reset", "-q", "--", path); // 신규 파일 등 폴백
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
            return new GitResult(false, "", "현재 브랜치를 확인할 수 없습니다(detached HEAD).");
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
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!up.Ok) return new Models.BranchState { Branch = branch, HasUpstream = false };
        var counts = await RunAsync(repoDir, "rev-list", "--left-right", "--count", "@{u}...HEAD");
        int behind = 0, ahead = 0;
        var parts = counts.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Ok && parts.Length == 2) { int.TryParse(parts[0], out behind); int.TryParse(parts[1], out ahead); }
        return new Models.BranchState { Branch = branch, HasUpstream = true, Ahead = ahead, Behind = behind };
    }
}
