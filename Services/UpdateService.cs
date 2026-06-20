using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace DevezCode.Services;

public record UpdateInfo(
    string Version, string Url, string Notes = "", bool IsUrgent = false,
    string PatchUrl = "", string PatchFrom = "", string Sha256 = "");

public static class UpdateService
{
    // csproj <Version> 을 단일 소스로 사용 (FileVersion 과도 일치 → version.json 과 교차검증 가능).
    public static readonly string CurrentVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    // devez-publish R2 버킷을 공유하되 객체 키만 DevezCode 전용으로 분리 (devez 객체와 충돌 없음).
    private const string VersionUrl = "https://pub-37da98a9e72d4514aed3533375fb7368.r2.dev/DevezCode_version.json";

    /// <summary>업데이트 다운로드를 허용할 신뢰 호스트(R2 버킷). 이 외 호스트·비 https URL 은 거부 → MITM/리다이렉트 강등 차단.</summary>
    private const string TrustedHost = "pub-37da98a9e72d4514aed3533375fb7368.r2.dev";

    /// <summary>https 이고 호스트가 신뢰 버킷인 URL만 허용. version.json 이 url/patchUrl 을 임의 도메인·http 로 바꿔치기하는 것을 막는다.</summary>
    public static bool IsTrustedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps
           && string.Equals(u.Host, TrustedHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>자동 교체 실패 시 수동 재설치용 최신 인스톨러 URL. (R2에 이 키로 인스톨러를 올려둬야 함)</summary>
    public const string InstallerUrl = "https://pub-37da98a9e72d4514aed3533375fb7368.r2.dev/DevezCode_Setup.zip";

    /// <summary>single-file self-extract 폴더에 전개된 zstd.exe 경로 (델타 복원용).</summary>
    private static string ZstdPath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Tools", "zstd.exe");

    /// <summary>version.json 본문을 UpdateInfo 로 파싱. version·url 누락 시 null.</summary>
    public static UpdateInfo? ParseVersionJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var ve) ? ve.GetString() ?? "" : "";
        var url = root.TryGetProperty("url", out var ue) ? ue.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return null;
        var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
        var urgent = root.TryGetProperty("urgent", out var u) && u.ValueKind == JsonValueKind.True;
        var patchUrl = root.TryGetProperty("patchUrl", out var pu) ? pu.GetString() ?? "" : "";
        var patchFrom = root.TryGetProperty("patchFrom", out var pf) ? pf.GetString() ?? "" : "";
        var sha256 = root.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
        return new UpdateInfo(version, url, notes, urgent, patchUrl, patchFrom, sha256);
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // r2.dev 엣지 캐시가 구버전 version.json 을 돌려주면 실시간 푸시 직후에도 업데이트가
            // 안 보일 수 있어 캐시버스터를 붙인다 (1KB 파일이라 원본 조회 비용은 무시 수준).
            var json = await http.GetStringAsync($"{VersionUrl}?t={Environment.TickCount64}");
            var info = ParseVersionJson(json);
            if (info is null) return null;
            // 통짜 url 이 신뢰 호스트·https 가 아니면 업데이트를 제시하지 않는다(다운로드 단계에서도 재차 거부됨).
            if (!IsTrustedUrl(info.Url)) return null;
            if (Version.Parse(info.Version) <= Version.Parse(CurrentVersion)) return null;
            return info;
        }
        catch { return null; }
    }

    /// <summary>현재 버전에 정확히 맞는 델타이고 patchUrl·sha256가 모두 있을 때만 델타 경로 사용.</summary>
    public static bool ShouldUseDelta(string currentVersion, UpdateInfo info)
        => !string.IsNullOrEmpty(info.PatchUrl)
        && !string.IsNullOrEmpty(info.Sha256)
        && info.PatchFrom == currentVersion;

    /// <summary>파일의 SHA256을 소문자 hex 문자열로 반환.</summary>
    public static string Sha256Hex(string filePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>파일 해시 == 기대 해시. 배포 시 복붙 공백·대소문자 실수를 흡수한다
    /// (불일치는 오류 표시 없이 전원 통짜 폴백이라 운영자가 알아채기 어렵기 때문).</summary>
    public static bool HashMatches(string filePath, string expectedSha256)
        => string.Equals(Sha256Hex(filePath), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>url을 dest로 스트리밍 다운로드. 진행률을 [from, to] 구간으로 매핑해 보고.</summary>
    private static async Task DownloadFileAsync(
        string url, string dest, IProgress<double> progress, double from, double to)
    {
        if (!IsTrustedUrl(url))
            throw new InvalidOperationException($"신뢰할 수 없는 업데이트 URL 입니다: {url}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src = await response.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buf = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            if (total > 0) progress.Report(from + (to - from) * downloaded / total);
        }
    }

    /// <summary>baseExe + patch → outExe 복원. zstd.exe 부재·타임아웃·실패 시 false → 호출부가 통짜로 폴백.</summary>
    private static async Task<bool> TryRestoreFromPatchAsync(string baseExe, string patchPath, string outExe)
    {
        if (!File.Exists(ZstdPath)) return false;
        // RedirectStandardError 는 쓰지 않는다: stderr 를 읽지 않은 채 리디렉트하면 zstd 가 경고를 많이
        // 출력할 때 파이프 버퍼(64KB)가 차서 프로세스가 블록되고 WaitForExitAsync 가 영구 대기한다.
        // WPF 앱은 콘솔이 없어 리디렉트하지 않으면 stderr 가 그대로 버려지므로 안전하다.
        // --long=27(128MB window): 생성 측(배포.md)도 27 "이하"만 허용 — 27을 초과해 생성하면 복원이 실패한다.
        var psi = new ProcessStartInfo
        {
            FileName = ZstdPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("--long=27");
        psi.ArgumentList.Add($"--patch-from={baseExe}");
        psi.ArgumentList.Add(patchPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outExe);
        psi.ArgumentList.Add("-f");
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        try
        {
            // 느린 디스크·CPU 대비 넉넉한 상한. 초과 시 kill 하고 통짜 폴백(무한 대기 방지).
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill 은 비동기 — 종료를 기다리지 않으면 zstd 가 outExe 핸들을 아직 쥔 채로
            // 폴백의 File.Create(tempExe) 가 공유 위반을 던질 수 있다.
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); } catch { }
            return false;
        }
        return proc.ExitCode == 0 && File.Exists(outExe);
    }

    public static async Task DownloadAndRelaunchAsync(UpdateInfo info, IProgress<double> progress)
    {
        var tempExe = Path.Combine(Path.GetTempPath(), "DevezCode_update.exe");
        var currentExe = Environment.ProcessPath!;
        bool ready = false;

        // 1) 델타 경로: 직전 버전 사용자만. 실패하면 통짜로 폴백.
        if (ShouldUseDelta(CurrentVersion, info))
        {
            var patchPath = Path.Combine(Path.GetTempPath(), "DevezCode_update.patch");
            try
            {
                await DownloadFileAsync(info.PatchUrl, patchPath, progress, 0.0, 0.85);
                progress.Report(0.9);
                if (await TryRestoreFromPatchAsync(currentExe, patchPath, tempExe)
                    && HashMatches(tempExe, info.Sha256))
                {
                    ready = true;
                    progress.Report(1.0);
                }
            }
            catch { ready = false; }
            finally
            {
                // 다운로드·복원이 예외로 끊겨도 임시 patch 가 %TEMP% 에 남지 않게 한다.
                try { File.Delete(patchPath); } catch { }
            }
        }

        // 2) 통짜 폴백 (델타 불가·다운로드 실패·복원 실패·해시 불일치)
        if (!ready)
        {
            // 무결성 정보 필수: sha256 가 없으면(미서명 빌드에선 서명검증도 생략되므로) 무검증 실행이 되어
            // 적용을 중단한다 → 호출부가 수동 재설치를 안내. 배포 시 version.json 에 sha256 을 반드시 포함할 것.
            if (string.IsNullOrEmpty(info.Sha256))
                throw new InvalidOperationException("업데이트 무결성 정보(sha256)가 없어 적용을 중단합니다.");
            await DownloadFileAsync(info.Url, tempExe, progress, 0.0, 1.0);
            // 통짜 결과물은 델타 복원 결과와 동일 exe → 같은 sha256. 불일치면 전송 변조/오류이므로 중단.
            if (!HashMatches(tempExe, info.Sha256))
            {
                try { File.Delete(tempExe); } catch { }
                throw new InvalidOperationException("업데이트 파일 해시가 일치하지 않습니다.");
            }
        }

        // 3) 진위: 코드서명 검증. version.json 채널과 독립된 신뢰 앵커(MS 루트 CA + 게시자).
        //    서명된 빌드에서는 강제, 미서명 개발 빌드에서는 자동 생략(VerifyMatchesCurrent 참조).
        if (!Authenticode.VerifyMatchesCurrent(tempExe, out var sigReason))
        {
            try { File.Delete(tempExe); } catch { }
            throw new InvalidOperationException($"업데이트 서명 검증 실패: {sigReason}");
        }

        // ── 이하 PowerShell 교체/재실행 스크립트 ──
        var newSize = new FileInfo(tempExe).Length;
        var script = Path.Combine(Path.GetTempPath(), "devezcode_update.ps1");
        // 경로의 작은따옴표를 PowerShell 리터럴 규칙('' → ')으로 이스케이프 (경로 보간 깨짐·명령 주입 방지).
        var srcLit = tempExe.Replace("'", "''");
        var dstLit = currentExe.Replace("'", "''");
        // copy 성공(크기 일치)을 확인한 뒤에만 새 exe 재실행.
        // 실패 시 옛 exe를 재실행하지 않고, 받아둔 새 exe를 직접 실행해 사용자가 최소한 최신 버전을 쓰게 한다.
        File.WriteAllText(script,
            $"$src = '{srcLit}'\n" +
            $"$dst = '{dstLit}'\n" +
            $"$expected = {newSize}\n" +
            $"$ok = $false\n" +
            $"for ($i = 0; $i -lt 20; $i++) {{\n" +
            $"    Start-Sleep -Milliseconds 700\n" +
            $"    try {{\n" +
            $"        Copy-Item -Force $src $dst -ErrorAction Stop\n" +
            $"        if ((Get-Item $dst).Length -eq $expected) {{ $ok = $true; break }}\n" +
            $"    }} catch {{ }}\n" +
            $"}}\n" +
            $"if ($ok) {{\n" +
            $"    Start-Process $dst -ArgumentList '--updated' -WorkingDirectory (Split-Path $dst)\n" +
            $"    Remove-Item $src -ErrorAction SilentlyContinue\n" +
            $"}} else {{\n" +
            $"    # 교체 실패(백신 잠금·프로세스 점유 등) — 받아둔 새 exe를 실패 플래그로 실행\n" +
            $"    # → 앱이 '자동 업데이트 미적용' 안내 + 수동 재설치 유도를 띄운다.\n" +
            $"    Start-Process $src -ArgumentList '--update-failed' -WorkingDirectory (Split-Path $src)\n" +
            $"}}\n");

        var pfX64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var needsElevation = currentExe.StartsWith(pfX64, StringComparison.OrdinalIgnoreCase)
                          || currentExe.StartsWith(pfX86, StringComparison.OrdinalIgnoreCase);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = needsElevation,
            Verb = needsElevation ? "runas" : "",
            CreateNoWindow = !needsElevation,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
