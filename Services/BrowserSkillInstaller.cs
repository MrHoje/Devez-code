using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DevezCode.Services;

/// <summary>세션이 슬래시로 부를 수 있는 미니 브라우저 제어 스킬 문서의 설치·제거 담당.
/// <para>claude, codex, opencode 가 각각 읽는 전역 스킬 폴더에 앱이 직접 쓴다(항상 최신으로 덮어씀).
/// 브라우저 MCP 설정을 끄면 세 곳에서 함께 지운다.</para>
/// <para>전역 폴더라 DevezCode 밖 터미널에서도 이름은 보인다. 그 경우 브라우저 도구가 없으므로
/// 스킬 본문이 "앱 안에서만 동작한다"고 알리고 끝내도록 적어 둔다.</para></summary>
public static class BrowserSkillInstaller
{
    private const string SkillName = "devez-mini-browser";

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>codex 홈(CODEX_HOME 우선). CodexHookInstaller 와 같은 규칙.</summary>
    private static string CodexHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(UserProfile, ".codex")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
    }

    /// <summary>opencode 설정 폴더(XDG_CONFIG_HOME 우선). OpenCodePluginInstaller 와 같은 규칙.</summary>
    private static string OpenCodeConfigDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configHome = string.IsNullOrEmpty(xdg) ? Path.Combine(UserProfile, ".config") : xdg;
            return Path.Combine(configHome, "opencode");
        }
    }

    /// <summary>스킬 문서를 깔 에이전트별 폴더. 하나가 실패해도 나머지는 계속 진행한다.</summary>
    public static IEnumerable<string> SkillDirs
    {
        get
        {
            yield return Path.Combine(UserProfile, ".claude", "skills", SkillName);
            yield return Path.Combine(CodexHome, "skills", SkillName);
            yield return Path.Combine(OpenCodeConfigDir, "skills", SkillName);
        }
    }

    /// <summary>브라우저 MCP 사용 여부에 맞춰 스킬을 깔거나 지운다. 브리지 스크립트와 같은 시점에 호출.</summary>
    public static void Sync(bool enabled)
    {
        var content = enabled ? Content() : null;
        foreach (var dir in SkillDirs)
        {
            try
            {
                if (content != null) Write(dir, content);
                else Remove(dir);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"BrowserSkillInstaller sync failed ({dir}): " + ex.Message);
            }
        }
    }

    private static void Write(string dir, string content)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        // 내용이 같으면 건드리지 않는다(에이전트가 감시 중인 파일의 불필요한 mtime 변경 방지).
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void Remove(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static string Content() => """
        ---
        name: devez-mini-browser
        description: DevezCode 미니 브라우저 창(메인 창 위에 떠 있는 작은 브라우저)을 세션이 직접 열고 조작한다. 사용자가 미니 브라우저에 띄운 페이지를 읽거나 클릭·입력·캡처해 달라고 할 때 사용한다. DevezCode 세션 안에서만 동작한다.
        ---

        # 미니 브라우저 제어

        DevezCode 가 자동 생성하는 문서입니다. 직접 수정하지 마십시오. 앱 시작 시 덮어써집니다.

        ## 전제

        - 이 스킬은 DevezCode 세션 안에서만 동작한다. `browser_use_mini` 도구가 보이지 않으면 앱 밖에서
          실행 중인 것이므로, 그 사실만 알리고 다른 방법을 시도하지 않는다.
        - 미니 브라우저 창은 앱에 하나뿐이라 한 세션만 잡을 수 있다.

        ## 순서

        1. `browser_use_mini` 로 미니 브라우저를 이 세션에 연결한다. 창을 닫아 둔 상태면 다시 열린다.
        2. 연결 후에는 모든 브라우저 도구가 미니 창에 적용된다.
           - 페이지 내용 읽기: `browser_read`
           - 현재 주소와 제목: `browser_current`
           - 주소 이동 또는 검색: `browser_open`
           - 클릭과 입력: `browser_click`, `browser_fill`
           - 눈으로 확인이 필요할 때: `browser_screenshot`
        3. 다른 세션이 이미 미니 창을 잡고 있으면 거절된다. 그때는 사용자에게 알리고, 세션 전용 탭을
           쓸지 물어본다.

        ## 주의

        - 사용자가 보던 페이지를 임의로 다른 주소로 옮기지 않는다. 이동이 필요하면 먼저 물어본다.
        - 입력한 뒤 전송 버튼을 눌러야 하는 화면은 `browser_fill` 과 `browser_click` 을 반드시 나눠
          호출한다. 입력 직후 같은 호출에서 버튼을 찾으면 아직 렌더 전이라 찾지 못한다.
        - 설정 창이 열려 있는 동안에는 미니 브라우저를 쓸 수 없다. 그 사유가 오면 설정을 닫아 달라고
          안내하고 다시 시도한다.
        - 로그인 화면이나 개인 정보가 보이는 페이지에서는 화면 캡처와 본문 읽기를 최소한으로 한다.
        """;
}
