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
}
