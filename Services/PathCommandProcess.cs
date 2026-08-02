using System.Diagnostics;
using System.Text;

namespace DevezCode.Services;

/// <summary>PATH/PATHEXT 에 등록된 CLI를 콘솔 창 없이 실행하는 시작 정보 생성기.</summary>
internal static class PathCommandProcess
{
    public static ProcessStartInfo Create(
        string command,
        IEnumerable<string> arguments,
        bool redirectOutput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };

        if (OperatingSystem.IsWindows())
        {
            // Process.Start(UseShellExecute=false)는 npm의 codex.cmd 같은 PATHEXT 명령을
            // 직접 찾지 못한다. cmd.exe가 PATH/PATHEXT 해석을 담당하게 한다.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
                ? comSpec
                : "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = command;
        }

        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        if (redirectOutput)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        return psi;
    }
}
