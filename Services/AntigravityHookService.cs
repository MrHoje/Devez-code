using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy) 훅이 방별로 떨군 busy/sessions 파일을 감시.
/// GrokHookService 와 동일 패턴 (roomId 키). agy 훅에는 UserPromptSubmit 이 없어
/// lastmsg/waiting 파일은 없다 — busy 는 PreToolUse/PostToolUse(running)/Stop(idle) 기준.
/// 실측(2026-07-13): agy 는 Stop/SessionStart 훅 프로세스를 조기 취소할 수 있어(--print 확인)
/// idle 전이가 유실될 수 있다 — stale failsafe 타이머가 오래 갱신 없는 running 을 idle 로 강제 전이.</summary>
public sealed class AntigravityHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "antigravity");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    /// <summary>running 파일이 이 시간 넘게 갱신 없으면 idle 로 강제 전이(Stop 훅 유실 대비).
    /// 도구 사이 간격이 긴 작업(장시간 빌드 등)에서 이보다 길게 조용하면 스피너가 일찍 꺼질 수
    /// 있으나, 다음 PreToolUse/PostToolUse 가 다시 켠다 — stuck-ON 보다 낫다.</summary>
    private static readonly TimeSpan StaleBusyTtl = TimeSpan.FromSeconds(120);

    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _sessionWatcher;
    private System.Threading.Timer? _staleTimer;

    public event Action<string, bool>? BusyChanged;
    public event Action<string, string>? SessionChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(SessionDir);

            // 이전 실행이 남긴 busy 상태는 신뢰 불가 — 시작 시 리셋(스피너 stuck-ON 방지).
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }

            _busyWatcher = new FileSystemWatcher(BusyDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _busyWatcher.Changed += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Created += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Renamed += (_, e) => EmitBusy(e.FullPath);

            _sessionWatcher = new FileSystemWatcher(SessionDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _sessionWatcher.Changed += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Created += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Renamed += (_, e) => EmitSession(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(SessionDir, "*.txt")) EmitSession(f);

            _staleTimer = new System.Threading.Timer(_ => SweepStaleBusy(), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
    }

    /// <summary>오래 갱신 없는 running 파일을 idle 로 강제 전이. 파일을 실제로 고쳐 써서
    /// (원자적 temp+move) 워처 경유로 BusyChanged(false) 가 자연히 발화되게 한다.
    /// mtime 은 매 스윕마다 새로 읽는다(캐시된 stale mtime 함정 방지).</summary>
    private void SweepStaleBusy()
    {
        try
        {
            if (!Directory.Exists(BusyDir)) return;
            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
            {
                try
                {
                    var status = TryRead(f);
                    if (status == null || !status.Equals("running", StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < StaleBusyTtl) continue;

                    var tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tmp, "idle");
                    File.Move(tmp, f, overwrite: true);
                }
                catch { /* 다음 스윕 */ }
            }
        }
        catch { }
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null || string.IsNullOrWhiteSpace(status))
        {
            // 빈 값 무시 금지 — 재확인 후 idle 확정 (.knowledge/훅-상태파일-원자적쓰기.md).
            ReEmitBusyAfterSettleAsync(path, room);
            return;
        }
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
    }

    private async void ReEmitBusyAfterSettleAsync(string path, string room)
    {
        await System.Threading.Tasks.Task.Delay(120);
        var status = TryRead(path);
        BusyChanged?.Invoke(room,
            !string.IsNullOrWhiteSpace(status)
            && status.Equals("running", StringComparison.OrdinalIgnoreCase));
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var sid = TryRead(path);
        if (sid != null) SessionChanged?.Invoke(room, sid);
    }

    private static string? TryRead(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd().Trim();
            }
            catch (IOException) { Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public static string? LoadTrackedSessionId(string roomId)
    {
        try
        {
            var path = Path.Combine(SessionDir, Sanitize(roomId) + ".txt");
            if (!File.Exists(path)) return null;
            var sid = File.ReadAllText(path).Trim();
            return Guid.TryParse(sid, out _) ? sid : null;
        }
        catch { return null; }
    }

    /// <summary>훅 스크립트 roomSafe 규칙과 동일: 비-워드 문자 제거.</summary>
    private static string Sanitize(string roomId)
    {
        var sb = new StringBuilder(roomId.Length);
        foreach (var c in roomId)
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        return sb.ToString();
    }

    public void Dispose()
    {
        _busyWatcher?.Dispose();
        _sessionWatcher?.Dispose();
        _staleTimer?.Dispose();
    }
}
