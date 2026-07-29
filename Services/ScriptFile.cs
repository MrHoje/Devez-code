using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>생성해서 외부 셸에 넘기는 스크립트 파일의 인코딩 규칙.
///
/// 셸은 파일 바이트를 자기 규칙으로 디코딩하므로 .NET 기본값(BOM 없는 UTF-8)으로 그냥 쓰면
/// 스크립트에 박힌 한글이 깨진다. 실제 사고: 사용자명이 한글인 PC(C:\Users\김이영)에서
/// 업데이트 교체 스크립트의 경로가 깨져 자동 업데이트가 조용히 실패했다.
///
/// 실측 결과(Windows 11, 한국어):
/// - `powershell.exe` 5.1 은 BOM 이 없는 `.ps1` 을 시스템 ANSI(=CP949)로 디코딩한다 → **BOM 필수**.
/// - `cmd.exe` 는 배치를 **그 시점 콘솔 코드페이지**로 디코딩하고, `chcp` 를 만나면 그 다음 줄부터
///   새 코드페이지로 다시 디코딩한다. DevezCode ConPTY 기본은 949, 외부 터미널은 65001 이라
///   어떤 고정 인코딩(UTF-8 이든 CP949 든)도 한쪽 환경에서 깨진다 → **ASCII 프롤로그로 코드페이지를
///   먼저 못박고 본문은 UTF-8** 이 유일하게 양쪽에서 성립한다.</summary>
public static class ScriptFile
{
    /// <summary>.ps1 전용 — BOM 있는 UTF-8.</summary>
    public static readonly Encoding Ps1 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>.cmd 전용 — BOM 없는 UTF-8. (BOM 은 cmd.exe 가 첫 명령으로 오해한다.)</summary>
    public static readonly Encoding Cmd = new UTF8Encoding(false);

    /// <summary>런치 배치 첫 줄. 순수 ASCII 라 어떤 콘솔 코드페이지에서도 동일하게 읽히고,
    /// 이 줄 이후 본문은 항상 UTF-8 로 디코딩된다(→ 한글 경로가 어느 환경에서도 안 깨진다).
    /// `@` 는 이 줄이 터미널에 에코되지 않게 한다(`@echo off` 보다 앞이므로 필요).</summary>
    /// (chcp.com 을 못 찾는 비정상 PATH 에서도 에러 문구가 터미널에 뜨지 않게 stderr 도 버린다.
    ///  실패하면 이전과 동일하게 콘솔 기본 코드페이지로 해석될 뿐이라 회귀는 아니다.)
    private const string CmdPrologue = "@chcp 65001>nul 2>nul\r\n";

    public static void WritePs1(string path, string content) => File.WriteAllText(path, content, Ps1);

    /// <summary>DevezCode 가 소유한 ConPTY 에서 실행할 런치 배치를 쓴다.
    ///
    /// 본문에 비ASCII 문자가 <b>있을 때만</b> 코드페이지 프롤로그를 붙인다. 순수 ASCII 본문은
    /// 어떤 코드페이지에서도 같은 바이트로 읽히므로 고칠 게 없고, 프롤로그를 붙이면 멀쩡한
    /// 환경의 콘솔 코드페이지만 바꾸는 셈이 된다(= 지금까지 정상 동작한 PC 에 불필요한 변화).
    /// → ASCII 본문은 종전과 <b>바이트 단위로 동일한 파일</b>이 되고, 한글 경로가 섞인 경우에만
    ///   프롤로그로 UTF-8 해석을 못박는다.
    ///
    /// 에이전트 훅처럼 <b>남의 콘솔을 상속해 실행되는</b> .cmd 에는 쓰지 말 것 — 그 콘솔의
    /// 코드페이지를 바꿔 에이전트 렌더링에 영향을 준다.</summary>
    public static void WriteLaunchCmd(string path, string content)
        => File.WriteAllText(path, IsAscii(content) ? content : CmdPrologue + content, Cmd);

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c > 0x7F) return false;
        return true;
    }

    /// <summary>.ps1 을 다시 써야 하는지 판단. 내용이 같아도 <b>BOM 이 없으면 다시 쓴다</b> —
    /// BOM 없이 설치된 구버전 파일은 내용 비교만으로는 절대 갱신되지 않아 버그가 남는다.</summary>
    public static bool Ps1NeedsWrite(string path, string content)
    {
        if (!File.Exists(path)) return true;
        if (!HasUtf8Bom(path)) return true;
        try { return File.ReadAllText(path, Ps1) != content; }
        catch { return true; }
    }

    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            return fs.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                   && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch { return false; }
    }
}
