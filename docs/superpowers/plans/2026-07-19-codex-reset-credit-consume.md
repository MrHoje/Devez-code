# Codex 초기화권 소비 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 계정 사용량 패널의 codex 초기화권을 사용자가 안전하게 1회 소비하고, 결과를 메시지박스로 고지하며 사용량을 즉시 갱신한다.

**Architecture:** `CodexUsageService`에 소비 POST·1시간 쿨다운·dropGuard 우회를 추가하고, `MainWindow`가 "초기화 N회 가능" 헤더의 `[사용]` 버튼 클릭을 받아 만료 최빠름 크레딧을 명시 소비한다. 확인·결과 고지는 기존 `ConfirmDialog`를 재사용한다.

**Tech Stack:** .NET (WPF), C#, `System.Net.Http`, `System.Text.Json`. 테스트 프로젝트 없음 → 검증은 `dotnet build -c Release` 성공 + 실행 중 앱 수동 확인.

## Global Constraints

- **프로세스 강제 종료 금지**(CLAUDE.md 최우선). 빌드가 필요하면 사용자에게 정상 종료 요청 후 진행. 본 세션이 DevezCode 내부 실행일 수 있으니 임의 재시작 금지.
- 소비 엔드포인트: `POST https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume`
- 요청 body(JSON, snake_case): `{ "credit_id": "<id>", "redeem_request_id": "<UUID>" }`
- 헤더: 기존 credits GET과 동일 — `Authorization: Bearer <token>`, `User-Agent: OpenCode-Quota-Toast/1.0`, `OpenAI-Beta: codex-1`, `ChatGPT-Account-Id: <accountId>`.
- 응답 `code` 값: `reset` / `nothing_to_reset` / `no_credit` / `already_redeemed`.
- **자동 재시도 금지.** `redeem_request_id`는 논리 시도당 1회 생성·재사용.
- 쿨다운: **실제 소비 시(`reset`/`already_redeemed`)만** 1시간. 계정 지문 일치 시에만 적용. 파일 `%AppData%\DevezCode\codex-reset-last-used.json`.
- UI 문구/색/커서: `AppStyles.xaml` 전역 스타일 준수. 기본 `MessageBox` 대신 `ConfirmDialog` 사용.
- 토큰 원문/refresh/JWT는 로그·파일에 기록 금지(지문만).

---

### Task 1: 모델에 credit id 추가 + GET 파싱

**Files:**
- Modify: `Models/ProviderUsage.cs` (ResetCredit)
- Modify: `Services/CodexUsageService.cs:392-401` (FetchResetCreditsAsync 파싱 루프)

**Interfaces:**
- Produces: `ResetCredit.Id` (string?) — 각 초기화권의 opaque id.

- [ ] **Step 1: `ResetCredit`에 `Id` 추가**

`Models/ProviderUsage.cs`의 `ResetCredit`에 필드 추가:

```csharp
/// <summary>opaque 초기화권 id. 소비 시 credit_id 로 전송. GET 응답에 없으면 null.</summary>
public string? Id { get; init; }
```

- [ ] **Step 2: GET 파싱에서 `id` 추출 + raw 로깅으로 존재 검증**

`Services/CodexUsageService.cs` FetchResetCreditsAsync 의 항목 루프를 수정:

```csharp
foreach (var item in arr.EnumerateArray())
{
    if (item.ValueKind != JsonValueKind.Object) continue;
    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "초기화권" : "초기화권";
    var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
    DateTimeOffset? granted = TryParseTimestamp(item, "granted_at");
    DateTimeOffset? expires = TryParseTimestamp(item, "expires_at");
    list.Add(new ResetCredit { Id = id, Title = title, GrantedAt = granted, ExpiresAt = expires });
}
if (list.Count > 0 && list.All(c => c.Id == null))
    DiagLog.Write("CodexUsage: reset-credits 응답에 id 없음 — 소비 시 auto-select 폴백");
return list;
```

- [ ] **Step 3: 빌드 검증**

사용자에게 앱 정상 종료 요청 후:
Run: `dotnet build -c Release --nologo -v quiet`
Expected: 종료 코드 0.

- [ ] **Step 4: 커밋**

```bash
git add Models/ProviderUsage.cs Services/CodexUsageService.cs
git commit -m "feat(codex): 초기화권 모델에 id 추가 및 GET 파싱"
```

---

### Task 2: 소비 API + dropGuard 우회 (`ConsumeResetCreditAsync`)

**Files:**
- Modify: `Services/CodexUsageService.cs` (상수, 신규 메서드, enum)

**Interfaces:**
- Consumes: `ResetCredit.Id` (Task 1).
- Produces:
  - `enum ConsumeOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed, Unknown }`
  - `Task<ConsumeOutcome> ConsumeResetCreditAsync(string? creditId, string redeemRequestId)`
  - `static ResetCredit? PickEarliestExpiring(IEnumerable<ResetCredit> credits)`

- [ ] **Step 1: consume URL 상수 + enum 추가**

`CodexUsageService.cs` 상단 상수 옆:

```csharp
private const string ConsumeUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";
```

파일 하단(클래스 밖 또는 Models)에서 enum:

```csharp
public enum ConsumeOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed, Unknown }
```

- [ ] **Step 2: 만료 최빠름 선택 헬퍼**

```csharp
/// <summary>만료가 가장 빠른 초기화권을 고른다. ExpiresAt 오름차순, null(만료정보 없음)은 맨 뒤.
/// 목록이 비면 null.</summary>
public static ResetCredit? PickEarliestExpiring(IEnumerable<ResetCredit> credits)
    => credits
        .OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue)
        .FirstOrDefault();
```

- [ ] **Step 3: `ConsumeResetCreditAsync` 구현**

```csharp
/// <summary>초기화권 1개를 소비한다. 폴링과 직렬화되며 자동 재시도하지 않는다.
/// 성공(Reset/AlreadyRedeemed) 시 dropGuard 를 리셋하고 즉시 재폴링한다.</summary>
public async Task<ConsumeOutcome> ConsumeResetCreditAsync(string? creditId, string redeemRequestId)
{
    await _pollGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await EnsureFreshAsync().ConfigureAwait(false);
        var (token, accountId, expired, _) = ReadAuth();
        if (token == null || expired)
        {
            DiagLog.Write("CodexUsage consume 중단: 토큰 없음/만료");
            return ConsumeOutcome.Unknown;
        }

        var payload = new Dictionary<string, string> { ["redeem_request_id"] = redeemRequestId };
        if (!string.IsNullOrEmpty(creditId)) payload["credit_id"] = creditId!;

        using var req = new HttpRequestMessage(HttpMethod.Post, ConsumeUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenCode-Quota-Toast/1.0");
        req.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
        if (accountId != null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req).ConfigureAwait(false);
        var bodyText = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            DiagLog.Write($"CodexUsage consume HTTP {(int)res.StatusCode}");
            return ConsumeOutcome.Unknown;
        }

        var outcome = ParseConsumeOutcome(bodyText);
        DiagLog.Write($"CodexUsage consume outcome={outcome}");

        if (outcome is ConsumeOutcome.Reset or ConsumeOutcome.AlreadyRedeemed)
        {
            // 정당한 조기 초기화 — 다음 폴링의 급락이 dropGuard 에 보류되지 않도록 기준값을 비운다.
            _dropGuard.Reset();
            _guardSeeded = false;
            WriteResetCooldown(Fingerprint(accountId ?? token));
        }
        return outcome;
    }
    catch (Exception ex)
    {
        DiagLog.Write("CodexUsage consume 예외: " + ex.GetType().Name);
        return ConsumeOutcome.Unknown;
    }
    finally
    {
        _pollGate.Release();
        // 성공 여부와 무관하게 최신 상태를 다시 읽어온다(성공 시 초기화 반영, 실패 시 원복 확인).
        RefreshNow();
    }
}

private static ConsumeOutcome ParseConsumeOutcome(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        return code switch
        {
            "reset" => ConsumeOutcome.Reset,
            "nothing_to_reset" => ConsumeOutcome.NothingToReset,
            "no_credit" => ConsumeOutcome.NoCredit,
            "already_redeemed" => ConsumeOutcome.AlreadyRedeemed,
            _ => ConsumeOutcome.Unknown,
        };
    }
    catch { return ConsumeOutcome.Unknown; }
}
```

> 주: `WriteResetCooldown`/`Fingerprint`는 Task 3 에서 정의(같은 클래스). `RefreshNow`는 `_pollGate` 해제 후 호출하므로 교착 없음.

- [ ] **Step 4: 빌드 검증**

Run(앱 정상 종료 후): `dotnet build -c Release --nologo -v quiet`
Expected: 종료 코드 0. (Task 3 미완이면 `WriteResetCooldown` 미정의로 실패하므로 Task 3 과 함께 빌드.)

- [ ] **Step 5: 커밋 (Task 3 완료 후 함께)**

---

### Task 3: 1시간 쿨다운 영속화

**Files:**
- Modify: `Services/CodexUsageService.cs` (쿨다운 파일 경로·읽기/쓰기·정적 조회)

**Interfaces:**
- Consumes: `Fingerprint(string)` (기존 private static), account 지문.
- Produces:
  - `private void WriteResetCooldown(string accountFingerprint)`
  - `public static TimeSpan? ResetCooldownRemaining()` — 현재 계정이 쿨다운 중이면 남은 시간, 아니면 null.
  - `public static readonly TimeSpan ResetCooldownDuration` = 1시간.

- [ ] **Step 1: 경로·상수 + 쓰기**

```csharp
public static readonly TimeSpan ResetCooldownDuration = TimeSpan.FromHours(1);

private static string ResetCooldownPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "DevezCode", "codex-reset-last-used.json");

private void WriteResetCooldown(string accountFingerprint)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResetCooldownPath)!);
        var json = JsonSerializer.Serialize(new
        {
            used_at = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            fingerprint = accountFingerprint,
        });
        var tmp = ResetCooldownPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ResetCooldownPath, overwrite: true);
    }
    catch (Exception ex) { DiagLog.Write("CodexUsage 쿨다운 기록 실패: " + ex.GetType().Name); }
}
```

- [ ] **Step 2: 정적 조회**

```csharp
/// <summary>현재 codex 계정이 초기화권 쿨다운 중이면 남은 시간, 아니면 null.
/// 지문이 현재 계정과 다르면(다른 계정) 무시한다.</summary>
public static TimeSpan? ResetCooldownRemaining()
{
    try
    {
        if (!File.Exists(ResetCooldownPath)) return null;
        var (token, accountId, _, _) = ReadAuth();
        if (token == null) return null;
        var current = Fingerprint(accountId ?? token);

        using var doc = JsonDocument.Parse(File.ReadAllText(ResetCooldownPath));
        var root = doc.RootElement;
        var fp = root.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
        if (!string.Equals(fp, current, StringComparison.Ordinal)) return null;
        if (!root.TryGetProperty("used_at", out var u) || u.ValueKind != JsonValueKind.Number) return null;

        var usedAt = DateTimeOffset.FromUnixTimeMilliseconds(u.GetInt64());
        var remaining = ResetCooldownDuration - (DateTimeOffset.Now - usedAt);
        return remaining > TimeSpan.Zero ? remaining : null;
    }
    catch { return null; }
}
```

- [ ] **Step 3: 빌드 검증**

Run(앱 정상 종료 후): `dotnet build -c Release --nologo -v quiet`
Expected: 종료 코드 0.

- [ ] **Step 4: 커밋 (Task 2+3)**

```bash
git add Services/CodexUsageService.cs
git commit -m "feat(codex): 초기화권 소비 API·dropGuard 우회·1시간 쿨다운"
```

---

### Task 4: 카드 VM에 버튼 상태/툴팁 노출

**Files:**
- Modify: `Models/UsageCardVM.cs` (UsageCardVM)

**Interfaces:**
- Consumes: `CodexUsageService.ResetCooldownRemaining()` / `ResetCooldownDuration` (MainWindow가 계산해 주입).
- Produces (UsageCardVM init 프로퍼티):
  - `bool CanConsumeResetCredit`
  - `string ResetCreditTooltip`
  - `System.Windows.Visibility ResetUseButtonVisibility`

- [ ] **Step 1: UsageCardVM에 프로퍼티 추가**

```csharp
/// <summary>초기화권 [사용] 버튼 활성 여부(크레딧 있음 & 쿨다운 아님). MainWindow가 주입.</summary>
public bool CanConsumeResetCredit { get; init; }
/// <summary>[사용] 버튼 호버 툴팁(쿨다운 안내). 쿨다운 아니면 빈 문자열.</summary>
public string ResetCreditTooltip { get; init; } = "";
/// <summary>[사용] 버튼 노출 — 초기화권이 있을 때만.</summary>
public System.Windows.Visibility ResetUseButtonVisibility
    => ResetCredits.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
```

- [ ] **Step 2: 빌드 검증**

Run(앱 정상 종료 후): `dotnet build -c Release --nologo -v quiet`
Expected: 종료 코드 0.

- [ ] **Step 3: 커밋**

```bash
git add Models/UsageCardVM.cs
git commit -m "feat(codex): 초기화권 사용 버튼 상태·툴팁 VM 프로퍼티"
```

---

### Task 5: UI 버튼 + 클릭 소비 흐름 (XAML + MainWindow)

**Files:**
- Modify: `MainWindow.xaml` (헤더 682-686 영역에 `[사용]` 버튼)
- Modify: `MainWindow.xaml.cs` (마지막 codex 사용량 캐시, 카드 빌드 시 버튼 상태 주입, 클릭 핸들러)

**Interfaces:**
- Consumes: `ConsumeResetCreditAsync`, `ConsumeOutcome`, `PickEarliestExpiring`, `ResetCooldownRemaining`, `ResetCooldownDuration` (Task 2·3), `CanConsumeResetCredit`/`ResetCreditTooltip`/`ResetUseButtonVisibility` (Task 4), `ConfirmDialog.Show`/`Alert`.

- [ ] **Step 1: XAML — 헤더를 버튼과 한 줄로**

`MainWindow.xaml`의 `ResetHeaderText` TextBlock(684-686)을 Grid로 감싸 오른쪽에 버튼 추가:

```xml
<Grid Margin="0,0,0,6">
    <TextBlock Text="{Binding ResetHeaderText}"
               VerticalAlignment="Center"
               FontSize="{DynamicResource Fs11}" FontWeight="SemiBold"
               Foreground="{DynamicResource TextMutedBrush}"/>
    <Button Content="사용" HorizontalAlignment="Right"
            Style="{DynamicResource SmallButtonStyle}"
            Visibility="{Binding ResetUseButtonVisibility}"
            IsEnabled="{Binding CanConsumeResetCredit}"
            ToolTip="{Binding ResetCreditTooltip}"
            Click="ResetCreditUse_Click"/>
</Grid>
```

> `SmallButtonStyle`이 없으면 `AppStyles.xaml`에서 실제 존재하는 소형 버튼 스타일명으로 교체(예: 기존 팝오버 버튼 스타일). 커서 Arrow 규칙 준수. 빈 툴팁일 때 툴팁 안 뜨게 하려면 `ToolTipService.IsEnabled`를 `CanConsumeResetCredit`의 반대로 두거나, 툴팁 문자열이 비면 무시되는지 확인.

- [ ] **Step 2: MainWindow — 마지막 codex 사용량 캐시**

`ApplyProviderUsage`에서 codex일 때 원본을 보관. 필드 추가 + 대입:

```csharp
private Models.ProviderUsage? _lastCodexUsage;
```

`ApplyProviderUsage(Models.ProviderUsage u)` 안, codex 분기(또는 provider=="codex")에서:

```csharp
if (u.Provider == "codex") _lastCodexUsage = u;
```

- [ ] **Step 3: MainWindow — 카드 빌드 시 버튼 상태 주입**

`AddProviderCard`에서 codex 카드(name이 codex/OpenAI일 때)에만 쿨다운 계산해 주입:

```csharp
var cooldown = CodexUsageService.ResetCooldownRemaining();
bool canConsume = credits.Length > 0 && cooldown == null;
string tip = "";
if (cooldown is { } rem)
{
    var elapsed = CodexUsageService.ResetCooldownDuration - rem;
    tip = $"약 {Math.Max(1, (int)elapsed.TotalMinutes)}분 전 초기화권을 사용했습니다.\n"
        + $"중복 사용을 막기 위해 사용 후 1시간 동안 잠깁니다. (약 {Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes))}분 후 다시 사용 가능)\n"
        + "지금 더 필요하면 ChatGPT 웹사이트나 데스크톱 앱에서 사용할 수 있습니다.";
}
```

그리고 카드 생성부에 `CanConsumeResetCredit = canConsume, ResetCreditTooltip = tip,` 추가. (초기화권 없는 opencode-go 카드는 credits.Length==0 이라 자동으로 버튼 숨김.)

- [ ] **Step 4: MainWindow — 클릭 핸들러**

```csharp
private bool _resetConsumeInFlight;

private async void ResetCreditUse_Click(object sender, System.Windows.RoutedEventArgs e)
{
    if (_resetConsumeInFlight) return;
    var credits = _lastCodexUsage?.ResetCredits;
    if (credits == null || credits.Count == 0) return;

    var pick = CodexUsageService.PickEarliestExpiring(credits);
    var expiryText = pick?.ExpiresAt is { } ex
        ? ex.ToLocalTime().ToString("M월 d일 HH:mm")
        : "만료정보 없음";

    if (!Views.ConfirmDialog.Show(
            "초기화권 사용",
            $"만료가 가장 빠른 초기화권(~ {expiryText})을 사용합니다.\n사용량 한도가 즉시 초기화되며 되돌릴 수 없습니다.",
            okLabel: "사용", danger: true))
        return;

    _resetConsumeInFlight = true;
    if (sender is System.Windows.Controls.Button b) b.IsEnabled = false;
    try
    {
        var reqId = System.Guid.NewGuid().ToString();
        var outcome = await _codex.ConsumeResetCreditAsync(pick?.Id, reqId);

        var (title, msg) = outcome switch
        {
            ConsumeOutcome.Reset or ConsumeOutcome.AlreadyRedeemed
                => ("초기화 완료", "초기화권을 사용했습니다. 사용량 한도가 초기화되었습니다."),
            ConsumeOutcome.NothingToReset
                => ("사용 안 됨", "초기화할 사용량이 없어 초기화권이 소모되지 않았습니다."),
            ConsumeOutcome.NoCredit
                => ("사용 안 됨", "사용 가능한 초기화권이 없습니다."),
            _ => ("확인 필요", "결과를 확인하지 못했습니다. 사용량을 새로고침해 확인하세요."),
        };
        Views.ConfirmDialog.Alert(title, msg);
    }
    finally
    {
        _resetConsumeInFlight = false;
        if (_usageOpen) SetSidebarUsageCards(BuildUsageCards()); // 쿨다운/개수 반영해 버튼 상태 갱신
    }
}
```

> `ConsumeOutcome`은 `DevezCode.Services` 네임스페이스 — using 또는 `Services.ConsumeOutcome` 로 참조.

- [ ] **Step 5: 빌드 검증**

Run(앱 정상 종료 후): `dotnet build -c Release --nologo -v quiet`
Expected: 종료 코드 0.

- [ ] **Step 6: 수동 확인 (실행)**

앱 실행 → 계정 사용량 패널 열기 → codex 카드에 "초기화 N회 가능 [사용]" 확인.
- `[사용]` 클릭 → 확인 다이얼로그(만료일 표시) → "사용" → 결과 메시지박스.
- 성공 시: 사용량이 곧 초기화로 갱신, "N-1회 가능", 버튼 비활성 + 호버 툴팁(경과/잔여).
- 앱 재시작해도 1시간 내면 버튼 비활성 유지.

- [ ] **Step 7: 커밋**

```bash
git add MainWindow.xaml MainWindow.xaml.cs
git commit -m "feat(codex): 계정 사용량 패널에서 초기화권 사용 버튼·소비 흐름"
```

---

### Task 6: 변경 이력 + 지식베이스 갱신

**Files:**
- Modify: `Views/SettingsDialog.xaml.cs` (변경 이력 항목)
- Modify: `.knowledge/claude-codex-사용량-즉시초기화-인증-신선도.md` (소비/쿨다운 절 추가)

- [ ] **Step 1: 변경 이력 한 줄 추가**

`Views/SettingsDialog.xaml.cs`의 버전 이력 배열 최상단에 새 버전 항목 추가(형식은 기존 항목과 동일):

```csharp
"계정 사용량 패널에서 OpenAI Codex 초기화권을 직접 사용할 수 있습니다. (사용 후 1시간 잠금)",
```

- [ ] **Step 2: 지식베이스에 소비 절 추가**

`.knowledge/claude-codex-사용량-즉시초기화-인증-신선도.md`의 "관련 코드" 위에 절 추가:

```markdown
## 초기화권 소비(consume)

- `POST /wham/rate-limit-reset-credits/consume`, body `{credit_id, redeem_request_id}`, 헤더는 GET과 동일.
- 명시 `credit_id`(만료 최빠름) + `redeem_request_id`(UUID) 재사용 + 버튼 락 + 1시간 쿨다운으로 중복 소비 차단.
- 성공 시 `_dropGuard.Reset()` 후 `RefreshNow()` — 조기 초기화 급락을 보류 없이 즉시 반영.
- 쿨다운은 `codex-reset-last-used.json`(계정 지문+used_at)로 재시작을 넘어 유지, 다른 계정이면 무시.
- 응답 `code`: reset/nothing_to_reset/no_credit/already_redeemed. 모르는 값은 성공 처리하지 않음.
```

- [ ] **Step 3: 커밋 + 푸시**

```bash
git add Views/SettingsDialog.xaml.cs .knowledge/claude-codex-사용량-즉시초기화-인증-신선도.md
git commit -m "docs(codex): 초기화권 소비 변경이력·지식베이스"
git push -u origin feature/codex-reset-credit-consume
```

---

## Self-Review 결과

- **Spec 커버리지:** 모델 id(T1)·소비 API/outcome/dropGuard 우회(T2)·쿨다운 영속(T3)·버튼 상태/툴팁(T4)·UI/확인/결과/즉시갱신(T5)·이력/지식(T6) 모두 태스크 존재.
- **미확정 검증 항목(구현 중 확인):** ① GET 응답 `id` 실제 존재 여부(T1 로깅) — 없으면 `credit_id` 생략 폴백이 T2 payload 조건분기로 이미 처리됨. ② `SmallButtonStyle` 실제 스타일명(T5 Step1 주석) — 빌드 전 `AppStyles.xaml`에서 확인해 교체. ③ 빈 툴팁 시 툴팁 억제(T5 Step1).
- **타입 일관성:** `ConsumeResetCreditAsync(string?, string)`, `ConsumeOutcome`, `PickEarliestExpiring`, `ResetCooldownRemaining()`, `ResetCooldownDuration`, `CanConsumeResetCredit`/`ResetCreditTooltip`/`ResetUseButtonVisibility` — 정의부(T2~T4)와 사용부(T5) 명칭 일치 확인.
- **빌드 규칙:** 모든 빌드 스텝은 사용자 정상 종료 확인 후 실행(강제 종료 금지). 본 세션이 DevezCode 내부면 재시작은 사용자 요청 시에만.
