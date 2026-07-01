using System.Diagnostics;
using System.IO;

namespace DevezCode.Services.Thermal;

/// <summary>
/// 온도 헬퍼를 "최고 권한"으로 UAC 없이 띄우기 위한 작업 스케줄러 연동.
///   최초 1회: 최고 권한 작업 등록(runas → UAC 1회).
///   이후:     schtasks /run 으로 트리거(무프롬프트, 재부팅 후에도 유지).
/// 인스톨러가 비관리자(PrivilegesRequired=lowest)라 설치 시 등록이 불가하므로 런타임에서 처리한다.
/// </summary>
public static class ThermalHelperLauncher
{
    private const string TaskName = "DevezCodeThermalHelper";

    /// <summary>작업이 있으면 실행, 없으면 등록(UAC 1회) 후 실행. 성공 시 true.</summary>
    public static bool EnsureRunning()
    {
        if (!IsRegistered())
        {
            if (!Register()) return false; // 등록 실패/사용자 UAC 취소
        }
        return RunTask();
    }

    private static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "";

    // ── 조회 ──────────────────────────────────────────────────────
    private static bool IsRegistered()
        => RunSchtasks(new[] { "/query", "/tn", TaskName }, elevated: false, out _);

    // ── 실행(무프롬프트) ──────────────────────────────────────────
    private static bool RunTask()
        => RunSchtasks(new[] { "/run", "/tn", TaskName }, elevated: false, out _);

    // ── 등록(runas → UAC 1회) ─────────────────────────────────────
    private static bool Register()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return false;

        // /tr 인용 지옥을 피하려 XML(Command/Arguments 분리)로 등록한다.
        string xmlPath = Path.Combine(Path.GetTempPath(), "devezcode_thermal_task.xml");
        try { File.WriteAllText(xmlPath, BuildTaskXml(exe), System.Text.Encoding.Unicode); }
        catch { return false; }

        // /xml 로 등록 — RunLevel=HighestAvailable 라 관리자 권한 필요 → runas.
        bool ok = RunSchtasks(new[] { "/create", "/tn", TaskName, "/xml", xmlPath, "/f" },
                              elevated: true, out _);
        try { File.Delete(xmlPath); } catch { }
        return ok;
    }

    /// <summary>InteractiveToken + HighestAvailable 로 현재 사용자 세션에서 관리자 권한 실행.
    /// 트리거는 없음(온디맨드 /run 전용). 다중 인스턴스 금지.</summary>
    private static string BuildTaskXml(string exe)
    {
        string user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}") ?? "";
        string cmd  = System.Security.SecurityElement.Escape(exe) ?? "";
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>DevezCode CPU 온도 판독 헬퍼</Description>
          </RegistrationInfo>
          <Triggers />
          <Principals>
            <Principal id="Author">
              <UserId>{user}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{cmd}</Command>
              <Arguments>--thermal-helper</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    // ── schtasks 실행 헬퍼 ────────────────────────────────────────
    private static bool RunSchtasks(string[] args, bool elevated, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = elevated, // runas 는 ShellExecute 필요
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated) psi.Verb = "runas";
            else { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            exitCode = p.HasExited ? p.ExitCode : -1;
            return exitCode == 0;
        }
        catch
        {
            // 사용자가 UAC 취소(Win32Exception 1223) 포함 — 모두 실패 처리.
            return false;
        }
    }
}
