using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DevezCode.Services;

/// <summary>안티그래비티(agy) 훅이 방별로 떨군 busy/sessions 파일을 감시.
/// GrokHookService 와 동일 패턴 (roomId 키). agy 훅에는 UserPromptSubmit 이 없어
/// lastmsg/waiting 파일은 없다 — busy 는 PreToolUse(running)/Stop(idle) 기준.</summary>
public sealed class AntigravityHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "antigravity");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _sessionWatcher;

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
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
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
    }
}
