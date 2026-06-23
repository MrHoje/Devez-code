using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>설정/워크스페이스 JSON 의 손실 없는 영속을 위한 원자적 쓰기 + 폴백 읽기.
/// 비원자적 File.WriteAllText 는 truncate 후 기록 도중 크래시/강제종료/정전 시
/// 0바이트·부분 파일을 남겨 다음 로드에서 전체 데이터가 유실된다. 이를 막는다.</summary>
public static class AtomicFile
{
    /// <summary>임시 파일에 쓰고 디스크까지 flush 한 뒤 원자적으로 교체한다.
    /// 교체 시 직전 정상본을 path.bak 으로 보존(읽기 폴백용).</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var bak = path + ".bak";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(content);
            sw.Flush();
            fs.Flush(true); // OS 캐시까지 물리 디스크로 — 정전/크래시에도 tmp 는 온전
        }

        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, bak, ignoreMetadataErrors: true); return; } // 원자 교체 + 백업
            catch (IOException) { /* 일부 FS/잠금 — Move 폴백 */ }
            catch (UnauthorizedAccessException) { /* 백업 생성 실패 — Move 폴백 */ }
            try { if (File.Exists(path)) File.Copy(path, bak, overwrite: true); } catch { } // 폴백 전 수동 백업
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>본 파일 → .bak 순으로 읽어 validate 를 통과하는 첫 내용을 반환.
    /// 둘 다 실패하면 손상 본 파일을 .corrupt-&lt;ticks&gt; 로 격리(원본 보존)하고 null 반환.
    /// validate: 내용이 정상 파싱되는지(예외 던지면 손상으로 간주). corrupted: 격리 발생 여부.</summary>
    public static string? ReadValidated(string path, Func<string, bool> validate, out bool corrupted)
    {
        corrupted = false;
        var bak = path + ".bak";

        foreach (var candidate in new[] { path, bak })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate);
                if (validate(text)) return text;
            }
            catch { /* 다음 후보 시도 */ }
        }

        // 본 파일이 존재하는데 어느 후보도 통과 못함 → 손상. 원본을 덮어쓰지 않도록 격리.
        try
        {
            if (File.Exists(path))
            {
                corrupted = true;
                var quarantine = path + ".corrupt-" + DateTime.UtcNow.Ticks;
                File.Move(path, quarantine);
            }
        }
        catch { /* 격리 실패해도 빈 시작으로 진행 */ }
        return null;
    }
}
