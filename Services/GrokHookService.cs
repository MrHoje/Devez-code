using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevezCode.Services;

/// <summary>Grok 훅이 방별로 떨군 lastmsg/busy/sessions 파일을 감시.
/// CodexHookService 와 동일 패턴 (roomId 키).</summary>
public sealed class GrokHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "grok");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string WaitingDir => Path.Combine(BaseDir, "waiting");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private static string SessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".txt");
    private static string PreviousSessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".prev.txt");
    private static string RootSessionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".root.txt");
    private static string SessionTransitionPath(string roomId) =>
        Path.Combine(SessionDir, Sanitize(roomId) + ".ended.txt");

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _waitingWatcher;
    private FileSystemWatcher? _sessionWatcher;

    public event Action<string, string>? MessageChanged;
    public event Action<string, bool>? BusyChanged;
    public event Action<string, bool>? WaitingChoiceChanged;
    public event Action<string, string>? GrokSessionChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(LastmsgDir);
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(WaitingDir);
            Directory.CreateDirectory(SessionDir);

            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.EnumerateFiles(WaitingDir, "*.txt"))
                try { File.Delete(f); } catch { }

            _lastmsgWatcher = new FileSystemWatcher(LastmsgDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _lastmsgWatcher.Changed += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Created += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Renamed += (_, e) => EmitLastmsg(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(LastmsgDir, "*.txt")) EmitLastmsg(f);

            _busyWatcher = new FileSystemWatcher(BusyDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _busyWatcher.Changed += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Created += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Renamed += (_, e) => EmitBusy(e.FullPath);

            _waitingWatcher = new FileSystemWatcher(WaitingDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _waitingWatcher.Changed += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Created += (_, e) => EmitWaiting(e.FullPath);
            _waitingWatcher.Renamed += (_, e) => EmitWaiting(e.FullPath);

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

    private void EmitLastmsg(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg != null) MessageChanged?.Invoke(room, NormalizeLastMessage(msg));
    }

    /// <summary>Grok 훅 payload가 추가하는 user_query 래퍼를 제거.
    /// 기존 lastmsg 파일과 구버전 훅 출력도 헤더에 노출되지 않게 앱에서 한 번 더 방어한다.</summary>
    internal static string NormalizeLastMessage(string message)
    {
        var match = Regex.Match(message,
            @"^\s*<user_query>\s*(.*?)\s*</user_query>\s*(?:…|\.\.\.)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) message = match.Groups[1].Value;
        return Regex.Replace(message, @"\s+", " ").Trim();
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null || string.IsNullOrWhiteSpace(status))
        {
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

    private void EmitWaiting(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (string.IsNullOrWhiteSpace(status))
        {
            _ = ReEmitWaitingAfterSettleAsync(path, room);
            return;
        }
        WaitingChoiceChanged?.Invoke(room,
            status.Equals("waiting", StringComparison.OrdinalIgnoreCase));
    }

    private async System.Threading.Tasks.Task ReEmitWaitingAfterSettleAsync(string path, string room)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(120).ConfigureAwait(false);
            var status = TryRead(path);
            WaitingChoiceChanged?.Invoke(room,
                !string.IsNullOrWhiteSpace(status)
                && status!.Equals("waiting", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        // 복구용 sidecar(.prev.txt/.ended.txt)는 현재 방의 세션 변경 이벤트가 아니다.
        if (room.EndsWith(".prev", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".root", StringComparison.OrdinalIgnoreCase)
            || room.EndsWith(".ended", StringComparison.OrdinalIgnoreCase)) return;
        var sid = TryRead(path);
        if (sid != null && Guid.TryParse(sid, out var parsed))
            GrokSessionChanged?.Invoke(room, parsed.ToString());
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
        return LoadSessionId(SessionPath(roomId));
    }

    /// <summary>루트 세션 전환 직전 ID. 상속된 DEVEZCODE_ROOM_ID 로 내부 Grok 프로세스가
    /// 잘못 발화해도 정상 대화를 되살릴 수 있는 복구 후보.</summary>
    public static string? LoadPreviousTrackedSessionId(string roomId)
    {
        return LoadSessionId(PreviousSessionPath(roomId));
    }

    /// <summary>현재 ID가 새 root-session fence를 통과해 기록된 값인지.</summary>
    public static bool IsRootTrackedSession(string roomId, string? sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return false;
        return string.Equals(LoadSessionId(RootSessionPath(roomId)), parsed.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>검증된 ID 로 현재 추적값을 복구. 다음 정상 SessionStart 가 stale 전환 마커의
    /// 영향을 받지 않도록 마커도 제거한다.</summary>
    public static void RestoreTrackedSessionId(string roomId, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed)) return;
        try
        {
            Directory.CreateDirectory(SessionDir);
            WriteAtomic(RootSessionPath(roomId), parsed.ToString());
            WriteAtomic(SessionPath(roomId), parsed.ToString());
            try { File.Delete(SessionTransitionPath(roomId)); } catch { }
        }
        catch { }
    }

    /// <summary>현재/이전/전환 추적값 제거. 실제 대화 파일은 건드리지 않는다.</summary>
    public static void ResetTrackedSessionIds(string roomId)
    {
        foreach (var path in new[]
        {
            SessionPath(roomId), PreviousSessionPath(roomId), RootSessionPath(roomId),
            SessionTransitionPath(roomId)
        })
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string? LoadSessionId(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var sid = File.ReadAllText(path).Trim();
            return Guid.TryParse(sid, out var parsed) ? parsed.ToString() : null;
        }
        catch { return null; }
    }

    private static void WriteAtomic(string path, string value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, value, Encoding.ASCII);
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>훅 스크립트 roomSafe 규칙과 동일: 비-워드 문자 제거 (underscore 치환 아님).</summary>
    private static string Sanitize(string roomId)
    {
        var sb = new StringBuilder(roomId.Length);
        foreach (var c in roomId)
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        return sb.ToString();
    }

    public void Dispose()
    {
        _lastmsgWatcher?.Dispose();
        _busyWatcher?.Dispose();
        _waitingWatcher?.Dispose();
        _sessionWatcher?.Dispose();
    }
}
