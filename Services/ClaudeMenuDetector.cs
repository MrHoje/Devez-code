using System.Text;
using System.Text.RegularExpressions;

namespace DevezCode.Services;

/// <summary>입력 대기 선택지 메뉴(1·2·3… 번호 옵션)를 터미널 화면 텍스트에서 감지한다.
/// claude / gjc(가재) / opencode 의 번호 메뉴를 모두 처리(박스 문자 │·┃, 푸터 힌트 차이를 흡수).
/// Discord 자동 전송과 좌측/패널 "입력 대기 ❗" 표시가 동일한 로직을 공유하도록 분리.
/// alt-screen 은 시각적 줄바꿈이 사라져 텍스트가 들러붙으므로, 연속 번호 마커로 직접 쪼갠다.</summary>
public static class ClaudeMenuDetector
{
    // 옵션 마커: 텍스트 어디서든 "N." / "N)" (alt-screen 은 줄바꿈을 ANSI 커서이동으로 처리 → \n 신뢰 불가,
    // 그래서 줄 단위가 아니라 번호 마커로 직접 분할한다).
    private static readonly Regex OptionMarker = new(@"(\d+)[\.\)]", RegexOptions.Compiled);

    /// <summary>선택지 메뉴가 화면에 떠 있는지만 빠르게 판정(텍스트 가공 불필요 시 사용).</summary>
    public static bool HasMenu(string? screen) => Extract(screen) != null;

    /// <summary>최근 터미널 화면에서 입력 대기 선택지 메뉴를 깔끔하게 추출한다(질문 + 번호 옵션).
    /// 없으면 null. 반환: (디스코드 표시 텍스트, 최대 옵션 번호, "Type something" 옵션 번호[없으면 0]).</summary>
    public static (string text, int maxOpt, int typeOpt)? Extract(string? screen)
    {
        if (string.IsNullOrEmpty(screen)) return null;
        var all = screen.Replace("\r", "").Split('\n');
        int take = Math.Min(45, all.Length);
        int from0 = all.Length - take;

        // 화면 끝부분을 한 줄로 평탄화. 박스 세로/모서리·커서 문자는 공백으로,
        // 단 가로 구분선('─')은 질문 경계 판단에 쓰므로 유지한다.
        // (claude 메뉴는 ╭ 박스가 아니라 긴 '──────' 구분선으로 그려지고, alt-screen 리페인트로
        //  이전 렌더 히스토리가 위쪽에 잔뜩 쌓인다 → '─' 구분선 기준으로 현재 메뉴만 잘라낸다.)
        bool hasCursor = false;
        var sbFlat = new StringBuilder();
        for (int i = from0; i < all.Length; i++)
        {
            var line = all[i];
            if (line.Contains('❯') || line.Contains('›')) hasCursor = true;
            var c = line.Replace('│', ' ').Replace('┃', ' ') // ┃ = opencode 박스 세로선
                .Replace('╮', ' ').Replace('╭', ' ').Replace('╯', ' ').Replace('╰', ' ')
                .Replace('┌', ' ').Replace('┐', ' ').Replace('└', ' ').Replace('┘', ' ')
                .Replace('├', ' ').Replace('┤', ' ').Replace('|', ' ')
                .Replace('❯', ' ').Replace('›', ' ').Replace('☐', ' ').Replace('☑', ' ');
            sbFlat.Append(' ').Append(c);
        }
        // '─' 는 보존하되 연속 '─' 는 한 글자로 줄이고, 그 외 공백만 압축.
        var flat = Regex.Replace(sbFlat.ToString(), "─+", "─");
        flat = Regex.Replace(flat, @"[^\S─]+", " ").Trim();

        // 푸터 힌트("Enter to select · ↑/↓ to navigate · Esc to cancel" 등)는 마지막 옵션에 들러붙으므로
        // 가장 먼저 나오는 힌트 위치에서 잘라낸다(힌트는 항상 옵션 뒤에 온다).
        int cut = flat.Length;
        foreach (var kw in new[] { "Enter to select", "↑/↓", "↑ /↓", "↑↓", "to navigate", "Esc to cancel", "esc to interrupt", "to interrupt",
                                   "enter submit", "esc dismiss" }) // opencode 푸터
        {
            var idx = flat.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cut) cut = idx;
        }
        flat = flat[..cut].Trim();

        // 1,2,3… 연속 번호 마커를 순서대로 찾는다(앞에서부터, 직전 마커 뒤에서만 다음 번호 탐색).
        var marks = new List<(int num, int start, int contentStart)>();
        int searchFrom = 0, expected = 1;
        while (true)
        {
            Match? found = null;
            foreach (Match m in OptionMarker.Matches(flat))
            {
                if (m.Index < searchFrom) continue;
                if (int.Parse(m.Groups[1].Value) == expected) { found = m; break; }
            }
            if (found == null) break;
            marks.Add((expected, found.Index, found.Index + found.Length));
            searchFrom = found.Index + found.Length;
            expected++;
        }
        int maxOpt = marks.Count;
        if (maxOpt == 0) return null;
        if (!(hasCursor && maxOpt >= 1) && maxOpt < 2) return null; // 오탐 최소화: 커서+옵션 or 옵션 2개+

        // 질문: 첫 옵션 앞 텍스트에서, 마지막 '─' 구분선(=현재 메뉴 박스 상단) 이후만 사용한다.
        // 이러면 위쪽 입력 에코·스피너·이전 렌더 히스토리가 모두 잘려나간다.
        var pre = flat[..marks[0].start];
        int sep = pre.LastIndexOf('─');
        var question = (sep >= 0 ? pre[(sep + 1)..] : pre);
        question = question.Replace("─", " ").Replace("☐", " ").Replace("☑", " ");
        question = Regex.Replace(question, @"\s{2,}", " ").Trim();
        if (question.Length > 200) question = "…" + question[^200..];

        // 각 옵션: 이 마커 content 시작 ~ 다음 마커 start 까지. 본문의 '─'(구분선) 제거.
        var opts = new List<string>();
        int typeOpt = 0; // "Type something"(자유 입력) 옵션 번호 — 디스코드 모달로 처리.
        for (int k = 0; k < marks.Count; k++)
        {
            int from = marks[k].contentStart;
            int to = k + 1 < marks.Count ? marks[k + 1].start : flat.Length;
            var body = flat[from..to].Replace("─", " ");
            body = Regex.Replace(body, @"\s{2,}", " ").Trim();
            if (body.IndexOf("type something", StringComparison.OrdinalIgnoreCase) >= 0) typeOpt = marks[k].num;
            if (body.Length > 150) body = body[..150].Trim() + "…";
            opts.Add($"{marks[k].num}. {body}");
        }

        var sb = new StringBuilder();
        sb.Append("```\n");
        if (question.Length > 0) sb.Append(question).Append("\n\n");
        foreach (var o in opts) sb.Append(o).Append('\n');
        sb.Append("```");
        var text = sb.ToString();
        if (text.Length > 1800) text = text[..1800] + "\n```";
        return (text, maxOpt, typeOpt);
    }
}
