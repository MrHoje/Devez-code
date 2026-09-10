using System.Windows;
using System.Windows.Controls;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class AccountSettingsView : UserControl
{
    private bool _loading;
    public bool IsBusy { get; private set; }
    public AccountSettingsView() => InitializeComponent();

    public void RefreshAccounts()
    {
        if (IsBusy) return;
        _loading = true;
        try
        {
            var data = CliAccountStore.Instance.RefreshCurrentAccounts(out var failedProviders);
            Providers.ItemsSource = new[] { "claude", "codex" }.Select(provider => new ProviderRow(provider, data)).ToArray();
            StatusText.Text = failedProviders.Count > 0
                ? string.Join(", ", failedProviders.Select(p => p == "claude" ? "Claude" : "Codex"))
                    + " 현재 계정을 읽지 못했습니다. 기존 등록 목록은 유지됩니다."
                : data.Accounts.Count == 0 ? "자동으로 가져올 계정 정보가 없습니다. 계정을 추가해 주세요." : "";
        }
        catch (Exception)
        {
            Providers.ItemsSource = null;
            StatusText.Text = "저장된 계정을 읽지 못했습니다. 계정 파일의 접근 권한과 Windows 사용자 계정을 확인하세요.";
        }
        finally { _loading = false; }
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || sender is not FrameworkElement { DataContext: ProviderRow provider }) return;
        var name = PromptDialog.Show("계정 추가", "구분하기 쉬운 계정 이름을 입력하세요.", defaultValue: provider.Name + " 계정", maxLength: 80);
        if (string.IsNullOrWhiteSpace(name)) return;
        SetBusy(true, "새 계정으로 로그인하는 중입니다. 현재 사용 중인 계정은 유지됩니다.");
        string message;
        try
        {
            UsageLoginWindowBase login = provider.Id == "claude"
                ? new ClaudeLoginWindow(Window.GetWindow(this), captureOnly: true)
                : new CodexLoginWindow(Window.GetWindow(this), captureOnly: true);
            login.ShowDialog();
            if (login.Captured && login.TokenResponse is { } response)
            {
                CliAccountStore.Instance.Add(CliAccountStore.FromLogin(provider.Id, response, name.Trim()));
                message = "계정을 추가했습니다. 사용할 계정에서 선택하면 적용됩니다.";
            }
            else message = "계정 추가를 취소했거나 로그인하지 못했습니다. 기존 계정은 유지됩니다.";
        }
        catch (Exception ex) { message = AccountError(ex); }
        SetBusy(false);
        RefreshAccounts();
        StatusText.Text = message;
    }

    private void ImportAccount_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || sender is not FrameworkElement { DataContext: ProviderRow provider }) return;
        try
        {
            if (CliAccountStore.Instance.ReadCurrent(provider.Id) == null)
                throw new InvalidOperationException("가져올 CLI 로그인 정보가 없습니다. 계정 추가로 로그인하세요.");
            CliAccountStore.Instance.CaptureCurrent(provider.Id);
            RefreshAccounts();
            StatusText.Text = "현재 CLI 계정을 가져왔습니다.";
        }
        catch (Exception ex) { StatusText.Text = AccountError(ex); }
    }

    private async void Account_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IsBusy || sender is not ComboBox { DataContext: ProviderRow provider, SelectedItem: AccountRow account }
            || account.Id.Length == 0 || account.Id == provider.ActiveId) return;
        if (Application.Current.MainWindow is not MainWindow main) return;
        SetBusy(true, "계정을 변경하는 중입니다. 세션의 대화 기록을 저장한 뒤 다시 엽니다.");
        string message;
        try
        {
            await main.SwitchCliAccountAsync(provider.Id, account.Id);
            message = $"{account.Name} 계정으로 변경했습니다. 열린 세션을 다시 시작했습니다.";
        }
        catch (Exception ex) { message = AccountError(ex); }
        SetBusy(false);
        RefreshAccounts();
        StatusText.Text = message;
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || sender is not FrameworkElement { DataContext: AccountRow account } || !account.CanDelete) return;
        if (!ConfirmDialog.Show("계정 삭제", $"'{account.Name}' 계정을 등록 목록에서 삭제할까요?\n공통 설정과 대화 기록은 유지됩니다.", okLabel: "삭제", danger: true)) return;
        try
        {
            CliAccountStore.Instance.Delete(account.Id);
            RefreshAccounts();
            StatusText.Text = "등록된 계정을 삭제했습니다.";
        }
        catch (Exception ex) { StatusText.Text = AccountError(ex); }
    }

    private void SetBusy(bool busy, string message = "")
    {
        IsBusy = busy;
        Providers.IsEnabled = !busy;
        StatusText.Text = message;
    }

    private static string AccountError(Exception ex) => ex is InvalidOperationException or System.IO.InvalidDataException or AccountRestoreException
        ? ex.Message : "계정 작업을 완료하지 못했습니다. 저장 공간과 파일 접근 권한을 확인하고 다시 시도하세요.";

    public sealed class ProviderRow
    {
        public string Id { get; }
        public string Name => Id == "claude" ? "Claude" : "Codex";
        public string ActiveId { get; }
        public string SelectionLabel => Name + " 사용 계정";
        public string AddLabel => Name + " 계정 추가";
        public List<AccountRow> Accounts { get; }
        public List<AccountRow> Options { get; }
        public bool CanSwitch => Accounts.Count > 0;
        public ProviderRow(string id, CliAccountStore.AccountData data)
        {
            Id = id;
            ActiveId = data.Active.GetValueOrDefault(id, "");
            Accounts = data.Accounts.Where(a => a.Provider == id).Select(a => new AccountRow(a.Id, a.DisplayName, a.Id == ActiveId)).ToList();
            Options = Accounts.ToList();
            if (Accounts.All(a => a.Id != ActiveId))
            {
                ActiveId = "";
                Options.Insert(0, new AccountRow("", Accounts.Count == 0 ? "계정을 추가하거나 가져오세요" : "현재 CLI 계정 · 미등록", true));
            }
        }
    }

    public sealed record AccountRow(string Id, string Name, bool IsActive)
    {
        public bool CanDelete => !IsActive;
        public string StateLabel => IsActive ? "현재 사용 중" : "등록된 계정";
        public string DeleteLabel => Name + " 삭제";
        public string DeleteHint => IsActive ? "다른 계정으로 변경한 뒤 삭제할 수 있습니다." : "등록 목록에서 삭제";
        public override string ToString() => Name;
    }
}
