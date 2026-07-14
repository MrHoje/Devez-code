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

    /// <summary>user.name / user.email 이 모두 설정돼 있는지(커밋 전 선제 검사).</summary>
    public static async Task<bool> HasIdentityAsync(string repoDir)
    {
        var name = await RunAsync(repoDir, "config", "user.name");
        var email = await RunAsync(repoDir, "config", "user.email");
        return name.Ok && !string.IsNullOrWhiteSpace(name.Output)
            && email.Ok && !string.IsNullOrWhiteSpace(email.Output);
    }

    /// <summary>전체 스테이징(add -A) 후 커밋(commit -m). add 실패 시 그 결과를 그대로 반환.</summary>
    public static async Task<GitResult> CommitAllAsync(string repoDir, string message)
    {
        var add = await RunAsync(repoDir, "add", "-A");
        if (!add.Ok) return add;
        return await RunAsync(repoDir, "commit", "-m", message);
    }

    /// <summary>현재 브랜치 푸시. upstream 이 있으면 push, 없으면 push -u origin &lt;branch&gt;.</summary>
    public static async Task<GitResult> PushAsync(string repoDir)
    {
        var br = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        if (!br.Ok) return br;
        var branch = br.Output.Trim();
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
            return new GitResult(false, "", "현재 브랜치를 확인할 수 없습니다(detached HEAD).");

        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        return up.Ok
            ? await RunAsync(repoDir, "push")
            : await RunAsync(repoDir, "push", "-u", "origin", branch);
    }

    /// <summary>푸시 버튼 상태용: upstream 유무와 보낼 커밋 수.
    /// upstream 없으면 (false, HEAD 존재 시 1 · 아니면 0) — 첫 푸시 허용 신호로 1 을 준다.</summary>
    public static async Task<(bool hasUpstream, int ahead)> PushStateAsync(string repoDir)
    {
        var up = await RunAsync(repoDir, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!up.Ok)
        {
            var head = await RunAsync(repoDir, "rev-parse", "--verify", "HEAD");
            return (false, head.Ok ? 1 : 0);
        }
        var r = await RunAsync(repoDir, "rev-list", "--count", "@{u}..HEAD");
        return (true, int.TryParse(r.Output.Trim(), out var n) ? n : 0);
    }
}
