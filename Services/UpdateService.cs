using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace DevezCode.Services;

public record UpdateReleaseNote(string Version, string Notes);

/// <summary>Program Files 설치본의 exe 교체에 필요한 관리자 권한 승격을 사용자가 거부(UAC 취소)했을 때.
/// 일반 실패(다운로드·해시·서명·교체)와 원인이 완전히 달라 안내 문구도 달라야 하므로 별도 타입으로 구분한다.</summary>
public sealed class UpdateElevationDeniedException(string message) : Exception(message);

public record UpdateInfo(
    string Version, string Url, string Notes = "", bool IsUrgent = false,
    string PatchUrl = "", string PatchFrom = "", string Sha256 = "",
    IReadOnlyList<UpdateReleaseNote>? Releases = null);

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
        var notes = root.TryGetProperty("notes", out var n) ? ReadNotes(n) : "";
        var urgent = root.TryGetProperty("urgent", out var u) && u.ValueKind == JsonValueKind.True;
        var patchUrl = root.TryGetProperty("patchUrl", out var pu) ? pu.GetString() ?? "" : "";
        var patchFrom = root.TryGetProperty("patchFrom", out var pf) ? pf.GetString() ?? "" : "";
        var sha256 = root.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
        var releases = new List<UpdateReleaseNote>();
        if (root.TryGetProperty("releases", out var re) && re.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in re.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var releaseVersion = item.TryGetProperty("version", out var rv) ? rv.GetString() ?? "" : "";
                var releaseNotes = item.TryGetProperty("notes", out var rn) ? ReadNotes(rn) : "";
                if (!string.IsNullOrWhiteSpace(releaseVersion) && !string.IsNullOrWhiteSpace(releaseNotes))
                    releases.Add(new UpdateReleaseNote(releaseVersion, releaseNotes));
            }
        }
        return new UpdateInfo(version, url, notes, urgent, patchUrl, patchFrom, sha256, releases);
    }

    private static string ReadNotes(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n", element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(note => !string.IsNullOrWhiteSpace(note))),
            _ => "",
        };

    /// <summary>현재 버전 이후부터 최신 버전까지의 노트만 최신순으로 반환.
    /// releases 가 없는 기존 version.json 은 최상위 notes 로 폴백한다.</summary>
    public static IReadOnlyList<UpdateReleaseNote> GetReleaseNotesSince(string currentVersion, UpdateInfo info)
    {
        var fallback = string.IsNullOrWhiteSpace(info.Notes)
            ? Array.Empty<UpdateReleaseNote>()
            : new[] { new UpdateReleaseNote(info.Version, info.Notes) };

        if (info.Releases is not { Count: > 0 }
            || !Version.TryParse(currentVersion, out var current)
            || !Version.TryParse(info.Version, out var latest))
            return fallback;

        var applicable = info.Releases
            .Select(release => (Release: release,
                Parsed: Version.TryParse(release.Version, out var parsed) ? parsed : null))
            .Where(item => item.Parsed is not null && item.Parsed > current && item.Parsed <= latest)
            .GroupBy(item => item.Parsed!)
            .Select(group => group.First())
            .OrderByDescending(item => item.Parsed)
            .Select(item => item.Release)
            .ToArray();
        return applicable.Length > 0 ? applicable : fallback;
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

    /// <summary>R2 엣지의 고정 객체 키 캐시를 피하기 위한 요청별 URL. version.json 뿐 아니라
    /// exe·patch도 배포 직후 이전 바이트를 받으면 최신 sha256 검증에서 실패하므로 같은 원칙을 적용한다.</summary>
    private static string WithCacheBuster(string url)
    {
        var uri = new UriBuilder(url);
        var query = uri.Query.TrimStart('?');
        uri.Query = string.IsNullOrEmpty(query)
            ? $"cb={Guid.NewGuid():N}"
            : $"{query}&cb={Guid.NewGuid():N}";
        return uri.Uri.AbsoluteUri;
    }

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
                await DownloadFileAsync(WithCacheBuster(info.PatchUrl), patchPath, progress, 0.0, 0.85);
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
            await DownloadFileAsync(WithCacheBuster(info.Url), tempExe, progress, 0.0, 1.0);
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
            // 재시도 상한은 안전 종료 소요(스냅샷 cap 3s + grace 2.5s + 훅 flush cap 5s + Dispose 마진)보다
            // 넉넉해야 한다. 짧으면 exe 잠금이 풀리기 전에 소진돼 멀쩡한 업데이트가 --update-failed 로 빠진다.
            $"for ($i = 0; $i -lt 40; $i++) {{\n" +
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

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = needsElevation,
                Verb = needsElevation ? "runas" : "",
                CreateNoWindow = !needsElevation,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        // ERROR_CANCELLED(1223) = UAC 프롬프트를 사용자가 취소하거나 정책이 승격을 막은 경우.
        // 여기서 구분하지 않으면 호출부가 "자동 업데이트 실패"로 뭉쳐 표시해 원인 오진단을 유발한다.
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            try { File.Delete(tempExe); } catch { }
            try { File.Delete(script); } catch { }
            throw new UpdateElevationDeniedException("업데이트 적용에 필요한 관리자 권한 승격이 거부되었습니다.");
        }

        // Shutdown() 을 직접 부르면 MainWindow.OnWindowClosing 의 안전 종료(스냅샷·오버레이·graceful)를
        // 우회한다 → 세션이 안전 경로를 못 타고 App.OnExit 폴백으로만 정리된다. 메인 창을 Close() 해
        // 안전 경로를 태운다. 단 ShutdownMode 기본값(OnLastWindowClose)에서는 토스트(NotificationPopup)나
        // 결과 모달 등 다른 창이 열려 있으면 메인 창만 닫혀도 프로세스가 안 내려가 exe 잠금이 유지되고,
        // 교체 스크립트가 재시도를 소진해 멀쩡한 업데이트가 실패 처리된다 → Closed 에서 Shutdown() 을
        // 명시 호출해 종료를 보장한다(이 시점엔 안전 경로가 이미 완주했으므로 우회가 아니다).
        Application.Current.Dispatcher.Invoke(() =>
        {
            var mw = Application.Current.MainWindow;
            if (mw != null)
            {
                if (mw is MainWindow m) m.ForceQuit = true; // '닫기 시 최소화' 무시하고 실제 종료
                mw.Closed += (_, _) => Application.Current.Shutdown();
                mw.Close();
            }
            else Application.Current.Shutdown();
        });
    }
}
