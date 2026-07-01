using System.Globalization;
using System.IO;

namespace DevezCode.Services.Thermal;

/// <summary>
/// GUI(비관리자) ↔ 온도 헬퍼(관리자) 간 파일 기반 IPC.
/// 헬퍼는 스케줄러가 분리 실행하므로 stdout 캡처가 불가 → 파일로 통신한다.
///   reading.txt   : 헬퍼가 기록하는 최신 CPU 온도(°C). mtime 으로 신선도 판정.
///   consumer.beat : GUI 가 주기적으로 touch 하는 하트비트. 끊기면 헬퍼가 자동 종료.
/// %LOCALAPPDATA%\DevezCode\thermal\ 아래에 둔다(전이성 런타임 파일 → Roaming 아님).
/// </summary>
public static class ThermalIpc
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevezCode", "thermal");

    private static string ReadingFile => Path.Combine(Dir, "reading.txt");
    private static string BeatFile     => Path.Combine(Dir, "consumer.beat");

    private static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* best effort */ }
    }

    // ── 헬퍼 측: 온도 기록 ────────────────────────────────────────
    public static void WriteReading(float celsius)
    {
        EnsureDir();
        try { File.WriteAllText(ReadingFile, celsius.ToString("0.0", CultureInfo.InvariantCulture)); }
        catch { /* 다음 주기에 재시도 */ }
    }

    /// <summary>이전 세션의 낡은 값을 프로브 전에 제거(신선한 첫 판독만 인정하기 위함).</summary>
    public static void ClearReading()
    {
        try { if (File.Exists(ReadingFile)) File.Delete(ReadingFile); } catch { /* 무시 */ }
    }

    // ── GUI 측: 온도 읽기 ─────────────────────────────────────────
    /// <summary>최신 온도와 파일 나이(초)를 반환. 파일 없음/파싱 실패면 false.</summary>
    public static bool TryReadReading(out float celsius, out double ageSeconds)
    {
        celsius = 0f; ageSeconds = double.MaxValue;
        try
        {
            if (!File.Exists(ReadingFile)) return false;
            var text = File.ReadAllText(ReadingFile).Trim();
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out celsius)) return false;
            ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(ReadingFile)).TotalSeconds;
            return true;
        }
        catch { return false; }
    }

    // ── 하트비트 ──────────────────────────────────────────────────
    /// <summary>GUI 가 살아있음을 알린다(매 폴링마다 호출).</summary>
    public static void TouchConsumer()
    {
        EnsureDir();
        try
        {
            if (File.Exists(BeatFile)) File.SetLastWriteTimeUtc(BeatFile, DateTime.UtcNow);
            else File.WriteAllText(BeatFile, "");
        }
        catch { /* best effort */ }
    }

    /// <summary>헬퍼 측: 소비자(GUI)가 최근에 살아있었는지. 끊기면 헬퍼는 스스로 종료한다.</summary>
    public static bool IsConsumerAlive()
    {
        try
        {
            if (!File.Exists(BeatFile)) return false;
            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(BeatFile)).TotalSeconds < 12;
        }
        catch { return false; }
    }
}
