using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DevezCode.Services;

/// <summary>grok 훅이 방별로 떨군 lastmsg/busy/sessions 파일을 감시.
/// CodexHookService 와 동일 패턴 (roomId 키).</summary>
public sealed class GrokHookService : IDisposable
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevezCode", "grok");
    private static string LastmsgDir => Path.Combine(BaseDir, "lastmsg");
    private static string BusyDir => Path.Combine(BaseDir, "busy");
    private static string SessionDir => Path.Combine(BaseDir, "sessions");

    private FileSystemWatcher? _lastmsgWatcher;
    private FileSystemWatcher? _busyWatcher;
    private FileSystemWatcher? _sessionWatcher;

    public event Action<string, string>? MessageChanged;
    public event Action<string, bool>? BusyChanged;
    public event Action<string, string>? GrokSessionChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(LastmsgDir);
            Directory.CreateDirectory(BusyDir);
            Directory.CreateDirectory(SessionDir);

            foreach (var f in Directory.EnumerateFiles(BusyDir, "*.txt"))
                try { File.Delete(f); } catch { }

            _lastmsgWatcher = new FileSystemWatcher(LastmsgDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _lastmsgWatcher.Changed += (_, e) => EmitLastmsg(e.FullPath);
            _lastmsgWatcher.Created += (_, e) => EmitLastmsg(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(LastmsgDir, "*.txt")) EmitLastmsg(f);

            _busyWatcher = new FileSystemWatcher(BusyDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _busyWatcher.Changed += (_, e) => EmitBusy(e.FullPath);
            _busyWatcher.Created += (_, e) => EmitBusy(e.FullPath);

            _sessionWatcher = new FileSystemWatcher(SessionDir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _sessionWatcher.Changed += (_, e) => EmitSession(e.FullPath);
            _sessionWatcher.Created += (_, e) => EmitSession(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(SessionDir, "*.txt")) EmitSession(f);
        }
        catch { /* 감시 실패해도 앱은 계속 */ }
    }

    private void EmitLastmsg(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg != null) MessageChanged?.Invoke(room, msg);
    }

    private void EmitBusy(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var status = TryRead(path);
        if (status == null || string.IsNullOrWhiteSpace(status)) return;
        BusyChanged?.Invoke(room, status.Equals("running", StringComparison.OrdinalIgnoreCase));
    }

    private void EmitSession(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var sid = TryRead(path);
        if (sid != null) GrokSessionChanged?.Invoke(room, sid);
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
        _sessionWatcher?.Dispose();
    }
}
