using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>가재코드(gjc) 확장(gajae-room-tracker.js)이 방별로 떨군 마지막 프롬프트
/// 파일(lastmsg\&lt;room&gt;.txt)을 감시해 헤더 "마지막 보낸 메시지"를 알린다.
/// <para>이전엔 세션 .jsonl 을 폴링했으나 gjc /new 는 새 세션을 메모리에만 만들고 파일을
/// 지연 기록해서 /new 직후 빈 세션을 감지할 수 없었다. 확장이 session_switch(reason=new)에서
/// 빈 문자열을 떨구므로 이 watcher 가 즉시 받아 헤더를 세션명으로 되돌린다.</para>
/// opencode 의 OpenCodeLastMessageService 와 동일 패턴(FileSystemWatcher). roomId 는 GUID("N")라
/// 파일명이 곧 roomId → 그대로 MessageChanged 로 흘린다.</summary>
public sealed class GajaeLastMessageService : IDisposable
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevezCode", "gajae", "lastmsg");

    private FileSystemWatcher? _watcher;

    /// <summary>(roomId, message) — gjc 세션이 마지막으로 보낸 프롬프트(1줄). 빈 문자열이면 세션명 표시.</summary>
    public event Action<string, string>? MessageChanged;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(Dir, "*.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => Emit(e.FullPath);
            _watcher.Created += (_, e) => Emit(e.FullPath);
            foreach (var f in Directory.EnumerateFiles(Dir, "*.txt")) Emit(f);
        }
        catch { /* 감시 실패해도 앱은 계속 — 헤더 부제만 안 뜸 */ }
    }

    private void Emit(string path)
    {
        var room = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(room)) return;
        var msg = TryRead(path);
        if (msg == null) return; // 읽기 실패만 무시. 빈 문자열은 유효(세션명 복귀 신호).
        MessageChanged?.Invoke(room, msg);
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
            catch (IOException) { System.Threading.Thread.Sleep(20); }
            catch { return null; }
        }
        return null;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
