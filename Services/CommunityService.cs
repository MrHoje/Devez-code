using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevezCode.Models;

namespace DevezCode.Services;

public sealed class CommunityService
{
    private const string ProjectUrl = "https://juaikaqmbulgxpleasoh.supabase.co";
    // Supabase publishable key: safe for desktop clients. Database permissions
    // remain enforced by RLS and the security-definer RPC functions.
    private const string PublishableKey = "sb_publishable_f7vbNo8yq3d39Mcn3dQPGg_dKPYgBt2";

    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(ProjectUrl),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CommunityPostSummary>> GetPostsAsync(
        string? category,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_list_posts", new
        {
            p_category = string.IsNullOrWhiteSpace(category) ? null : category,
            p_search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            p_limit = 100,
            p_offset = 0,
        }, cancellationToken);

        return json.Deserialize<List<CommunityPostSummary>>(JsonOptions) ?? [];
    }

    public async Task<CommunityPostDetail> GetPostAsync(
        Guid id,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_get_post", new
        {
            p_id = id,
            p_password = password,
        }, cancellationToken);

        var posts = json.Deserialize<List<CommunityPostDetail>>(JsonOptions);
        return posts is { Count: > 0 }
            ? posts[0]
            : throw new CommunityException("게시글을 찾을 수 없습니다.");
    }

    public async Task<bool> VerifyPasswordAsync(
        Guid id,
        string password,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_verify_password", new
        {
            p_id = id,
            p_password = password,
        }, cancellationToken);
        return json.ValueKind == JsonValueKind.True;
    }

    public async Task<Guid> CreatePostAsync(
        CommunityPostDraft draft,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_create_post", new
        {
            p_category = draft.Category,
            p_title = draft.Title,
            p_body = draft.Body,
            p_author_name = draft.AuthorName,
            p_password = draft.Password,
            p_is_private = draft.IsPrivate,
        }, cancellationToken);

        return ReadGuid(json);
    }

    public async Task UpdatePostAsync(
        Guid id,
        CommunityPostDraft draft,
        string password,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_update_post", new
        {
            p_id = id,
            p_category = draft.Category,
            p_title = draft.Title,
            p_body = draft.Body,
            p_author_name = draft.AuthorName,
            p_password = password,
            p_is_private = draft.IsPrivate,
        }, cancellationToken);

        if (json.ValueKind != JsonValueKind.True)
            throw new CommunityException("게시글을 수정하지 못했습니다.");
    }

    public async Task DeletePostAsync(
        Guid id,
        string password,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("community_delete_post", new
        {
            p_id = id,
            p_password = password,
        }, cancellationToken);

        if (json.ValueKind != JsonValueKind.True)
            throw new CommunityException("게시글을 삭제하지 못했습니다.");
    }

    private static async Task<JsonElement> InvokeAsync(
        string functionName,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/rest/v1/rpc/{functionName}");
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PublishableKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommunityException("서버 응답 시간이 초과되었습니다.");
        }
        catch (HttpRequestException)
        {
            throw new CommunityException("커뮤니티 서버에 연결할 수 없습니다.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw CreateException(text);

            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new CommunityException("서버 응답을 읽지 못했습니다.");
            }
        }
    }

    private static Guid ReadGuid(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.String &&
            Guid.TryParse(json.GetString(), out var id))
            return id;

        throw new CommunityException("게시글 번호를 확인하지 못했습니다.");
    }

    private static CommunityException CreateException(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var message = document.RootElement.TryGetProperty("message", out var value)
                ? value.GetString()
                : null;

            return message switch
            {
                "COMMUNITY_INVALID_PASSWORD" => new("비밀번호가 올바르지 않습니다."),
                "COMMUNITY_NOT_FOUND" => new("게시글을 찾을 수 없습니다."),
                "COMMUNITY_INVALID_PASSWORD_LENGTH" => new("비밀번호는 4~72자로 입력하세요."),
                "COMMUNITY_INVALID_TITLE" => new("제목은 2~100자로 입력하세요."),
                "COMMUNITY_INVALID_BODY" => new("내용은 1~5,000자로 입력하세요."),
                "COMMUNITY_INVALID_AUTHOR" => new("이름은 2~20자로 입력하세요."),
                _ => new CommunityException("요청을 처리하지 못했습니다. 잠시 후 다시 시도해주세요."),
            };
        }
        catch (JsonException)
        {
            return new CommunityException("요청을 처리하지 못했습니다. 잠시 후 다시 시도해주세요.");
        }
    }
}

public sealed class CommunityException(string message) : Exception(message);
