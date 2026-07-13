using System.Windows;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class CommunityEditorWindow : Window
{
    private readonly CommunityService _service;
    private readonly Guid? _postId;
    private readonly string? _editPassword;
    private bool _saving;

    public Guid? SavedPostId { get; private set; }

    public CommunityEditorWindow(
        Window owner,
        CommunityService service,
        CommunityPostDetail? post = null,
        string? editPassword = null)
    {
        InitializeComponent();
        Owner = owner;
        _service = service;
        _postId = post?.Id;
        _editPassword = editPassword;
        CategoryBox.ItemsSource = CommunityCategories.Options;

        if (post == null)
        {
            CategoryBox.SelectedValue = CommunityCategories.Improvement;
            Loaded += (_, _) => TitleBox.Focus();
        }
        else
        {
            Title = "게시글 수정";
            HeaderTitleText.Text = "게시글 수정";
            SaveBtnText.Text = "수정";
            CategoryBox.SelectedValue = post.Category;
            TitleBox.Text = post.Title;
            BodyBox.Text = post.Body;
            AuthorBox.Text = post.AuthorName;
            PrivateCheckBox.IsChecked = post.IsPrivate;
            PasswordSection.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => TitleBox.Focus();
        }
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        var category = CategoryBox.SelectedValue as string ?? "";
        var title = TitleBox.Text.Trim();
        var body = BodyBox.Text.Trim();
        var author = AuthorBox.Text.Trim();
        var password = _postId.HasValue ? _editPassword ?? "" : PasswordBox.Password;

        var validation = Validate(category, title, body, author, password);
        if (validation != null)
        {
            ShowStatus(validation);
            return;
        }

        var draft = new CommunityPostDraft
        {
            Category = category,
            Title = title,
            Body = body,
            AuthorName = author,
            Password = password,
            IsPrivate = PrivateCheckBox.IsChecked == true,
        };

        _saving = true;
        SaveBtn.IsEnabled = false;
        ShowStatus(_postId.HasValue ? "수정하는 중..." : "등록하는 중...", isError: false);
        try
        {
            if (_postId is Guid id)
            {
                await _service.UpdatePostAsync(id, draft, password);
                SavedPostId = id;
            }
            else
            {
                SavedPostId = await _service.CreatePostAsync(draft);
            }
            DialogResult = true;
        }
        catch (CommunityException ex)
        {
            ShowStatus(ex.Message);
            SaveBtn.IsEnabled = true;
            _saving = false;
        }
    }

    private static string? Validate(
        string category,
        string title,
        string body,
        string author,
        string password)
    {
        if (!CommunityCategories.Options.Any(option => option.Value == category))
            return "분류를 선택하세요.";
        if (title.Length is < 2 or > 100)
            return "제목은 2~100자로 입력하세요.";
        if (body.Length is < 1 or > 5000)
            return "내용은 1~5,000자로 입력하세요.";
        if (author.Length is < 2 or > 20)
            return "이름은 2~20자로 입력하세요.";
        if (password.Length is < 4 or > 72)
            return "비밀번호는 4~72자로 입력하세요.";
        return null;
    }

    private void ShowStatus(string message, bool isError = true)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextMutedBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_saving)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            await SaveAsync();
            e.Handled = true;
        }
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) DialogResult = false;
    }
}
