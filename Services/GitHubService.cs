using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

/// <summary>GitHub CLI를 통해 현재 저장소의 pull request 상태를 읽는 로컬 전용 래퍼.</summary>
public static class GitHubService
{
    private readonly record struct GhResult(bool Ok, string Output, string Error, bool Missing);

    public static async Task<GitHubPullRequestResult> PullRequestsAsync(string repoDir, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 100);
        var result = await RunAsync(
            repoDir,
            "pr", "list",
            "--state", "all",
            "--limit", limit.ToString(),
            "--json",
            "number,title,state,isDraft,author,headRefName,baseRefName,updatedAt,url,reviewDecision,statusCheckRollup,additions,deletions,changedFiles");

        if (result.Missing)
        {
            return new GitHubPullRequestResult
            {
                State = GitHubLoadState.CliMissing,
                Message = "PR을 보려면 GitHub CLI가 필요합니다."
            };
        }

        if (!result.Ok)
        {
            var raw = (result.Output + "\n" + result.Error).Trim();
            DiagLog.Write($"GitHub PR load failed: {raw}");
            if (ContainsAny(raw, "gh auth login", "not logged into", "authentication", "http 401", "bad credentials"))
            {
                return new GitHubPullRequestResult
                {
                    State = GitHubLoadState.AuthenticationRequired,
                    Message = "GitHub 계정 로그인이 필요합니다."
                };
            }
            if (ContainsAny(raw, "none of the git remotes", "known GitHub host", "not a github repository", "no git remotes"))
            {
                return new GitHubPullRequestResult
                {
                    State = GitHubLoadState.UnsupportedRemote,
                    Message = "현재 저장소에 연결된 GitHub 원격을 찾지 못했습니다."
                };
            }
            return new GitHubPullRequestResult
            {
                State = GitHubLoadState.Failed,
                Message = "PR 정보를 불러오지 못했습니다. 잠시 후 다시 시도하세요."
            };
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var items = new List<GitPullRequestItem>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var passing = 0;
                var pending = 0;
                var failing = 0;
                if (element.TryGetProperty("statusCheckRollup", out var checks)
                    && checks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var check in checks.EnumerateArray())
                    {
                        var status = GetString(check, "status").ToUpperInvariant();
                        var conclusion = GetString(check, "conclusion").ToUpperInvariant();
                        var state = GetString(check, "state").ToUpperInvariant();
                        if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE"
                            || state is "ERROR" or "FAILURE")
                            failing++;
                        else if (status is "IN_PROGRESS" or "QUEUED" or "PENDING" or "EXPECTED" or "WAITING" or "REQUESTED"
                                 || state is "EXPECTED" or "PENDING"
                                 || (status.Length > 0 && status != "COMPLETED" && conclusion.Length == 0))
                            pending++;
                        else if (conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED" || state == "SUCCESS")
                            passing++;
                    }
                }

                var author = "";
                if (element.TryGetProperty("author", out var authorElement)
                    && authorElement.ValueKind == JsonValueKind.Object)
                    author = GetString(authorElement, "login");

                _ = DateTimeOffset.TryParse(GetString(element, "updatedAt"), out var updatedAt);
                items.Add(new GitPullRequestItem
                {
                    Number = GetInt(element, "number"),
                    Title = GetString(element, "title"),
                    State = GetString(element, "state"),
                    IsDraft = GetBool(element, "isDraft"),
                    AuthorLogin = author,
                    HeadRefName = GetString(element, "headRefName"),
                    BaseRefName = GetString(element, "baseRefName"),
                    UpdatedAt = updatedAt,
                    Url = GetString(element, "url"),
                    ReviewDecision = GetString(element, "reviewDecision"),
                    Additions = GetInt(element, "additions"),
                    Deletions = GetInt(element, "deletions"),
                    ChangedFiles = GetInt(element, "changedFiles"),
                    PassingChecks = passing,
                    PendingChecks = pending,
                    FailingChecks = failing,
                });
            }

            return new GitHubPullRequestResult
            {
                State = GitHubLoadState.Ready,
                PullRequests = items
            };
        }
        catch (JsonException ex)
        {
            DiagLog.Write($"GitHub PR JSON parse failed: {ex.Message}");
            return new GitHubPullRequestResult
            {
                State = GitHubLoadState.Failed,
                Message = "PR 응답을 읽지 못했습니다. GitHub CLI를 업데이트한 뒤 다시 시도하세요."
            };
        }
    }

    public static async Task<(bool Ok, string Body)> PullRequestBodyAsync(string repoDir, int number)
    {
        var result = await RunAsync(repoDir, "pr", "view", number.ToString(), "--json", "body", "--jq", ".body");
        return result.Ok ? (true, result.Output.Trim()) : (false, "");
    }

    private static async Task<GhResult> RunAsync(string repoDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = repoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["NO_COLOR"] = "1";
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return new GhResult(false, "", "gh 프로세스를 시작하지 못했습니다.", false);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new GhResult(process.ExitCode == 0, await outputTask, await errorTask, false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GhResult(false, "", "", true);
        }
        catch (Exception ex)
        {
            return new GhResult(false, "", ex.Message, false);
        }
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
