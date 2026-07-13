using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevezCode.Models;
using DevezCode.Services;

namespace DevezCode.Views;

public partial class CommunityWindow : Window
{
    private readonly CommunityService _service = new();
    private readonly ObservableCollection<CommunityPostSummary> _posts = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CommunityPostSummary? _selectedPost;
    private CommunityPostDetail? _currentDetail;
    private string? _currentPassword;
    private bool _loaded;

    public CommunityWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        PostList.ItemsSource = _posts;
        CategoryFilter.ItemsSource = new[]
        {
            new CommunityCategoryOption("", "전체"),
        }.Concat(CommunityCategories.Options).ToArray();
        CategoryFilter.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            _loaded = true;
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _loadCancellation?.Cancel();
            _detailCancellation?.Cancel();
        };
    }

    private async Task RefreshAsync(Guid? selectedId = null)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        SetLoading(true);
        ListStatusText.Text = "불러오는 중...";
        try
        {
            var category = CategoryFilter.SelectedValue as string;
            var result = await _service.GetPostsAsync(category, SearchBox.Text, token);
            if (token.IsCancellationRequested) return;

            _posts.Clear();
            foreach (var post in result) _posts.Add(post);

            EmptyListText.Visibility = _posts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var total = result.Count > 0 ? result[0].TotalCount : 0;
            ListStatusText.Text = $"게시글 {total:N0}개";

            var target = selectedId.HasValue
                ? _posts.FirstOrDefault(post => post.Id == selectedId.Value)
                : _posts.FirstOrDefault();
            PostList.SelectedItem = target;
            if (target == null) ShowEmptyDetail();
        }
        catch (OperationCanceledException)
        {
        }
        catch (CommunityException ex)
        {
            ListStatusText.Text = ex.Message;
            ConfirmDialog.Alert("커뮤니티", ex.Message, iconKey: "IconMessageSquare");
        }
        finally
        {
            if (!token.IsCancellationRequested) SetLoading(false);
        }
    }

    private async void PostList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _detailCancellation?.Cancel();
        _currentDetail = null;
        _currentPassword = null;
        _selectedPost = PostList.SelectedItem as CommunityPostSummary;

        if (_selectedPost == null)
        {
            ShowEmptyDetail();
            return;
        }

        if (_selectedPost.IsPrivate)
        {
            ShowLockedDetail(_selectedPost);
            return;
        }

        await LoadDetailAsync(_selectedPost, null);
    }

    private async Task LoadDetailAsync(CommunityPostSummary summary, string? password)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        var token = _detailCancellation.Token;
        SetLoading(true);
        try
        {
            var detail = await _service.GetPostAsync(summary.Id, password, token);
            if (token.IsCancellationRequested || _selectedPost?.Id != summary.Id) return;

            _currentDetail = detail;
            _currentPassword = password;
            ShowDetail(detail);
        }
        catch (OperationCanceledException)
        {
        }
        catch (CommunityException ex)
        {
            if (_selectedPost?.Id == summary.Id) ShowLockedDetail(summary);
            ConfirmDialog.Alert("게시글 열기", ex.Message, iconKey: "IconLock");
        }
        finally
        {
            if (!token.IsCancellationRequested) SetLoading(false);
        }
    }

    private void ShowEmptyDetail()
    {
        EmptyDetailPane.Visibility = Visibility.Visible;
        LockedDetailPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
    }

    private void ShowLockedDetail(CommunityPostSummary summary)
    {
        LockedTitleText.Text = summary.Title;
        EmptyDetailPane.Visibility = Visibility.Collapsed;
        LockedDetailPane.Visibility = Visibility.Visible;
        DetailPane.Visibility = Visibility.Collapsed;
    }

    private void ShowDetail(CommunityPostDetail detail)
    {
        DetailCategoryText.Text = detail.CategoryLabel;
        DetailTitleText.Text = detail.Title;
        DetailAuthorText.Text = detail.AuthorName;
        DetailDateText.Text = detail.DateLabel;
        DetailBodyText.Text = detail.Body;
        DetailLockIcon.Visibility = detail.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
        EmptyDetailPane.Visibility = Visibility.Collapsed;
        LockedDetailPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Visible;
    }

    private void SetLoading(bool loading)
    {
        LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        NewPostBtn.IsEnabled = !loading;
    }

    private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPost == null) return;
        var password = CommunityPasswordDialog.Ask(
            this,
            "비공개 글 열기",
            "작성 시 설정한 비밀번호를 입력하세요.",
            "열기");
        if (password == null) return;
        await LoadDetailAsync(_selectedPost, password);
    }

    private async Task<string?> GetVerifiedPasswordAsync()
    {
        if (_selectedPost == null) return null;
        if (!string.IsNullOrEmpty(_currentPassword)) return _currentPassword;

        var password = CommunityPasswordDialog.Ask(
            this,
            "작성자 확인",
            "게시글을 작성할 때 설정한 비밀번호를 입력하세요.",
            "확인");
        if (password == null) return null;

        SetLoading(true);
        try
        {
            if (!await _service.VerifyPasswordAsync(_selectedPost.Id, password))
            {
                ConfirmDialog.Alert("작성자 확인", "비밀번호가 올바르지 않습니다.", iconKey: "IconLock");
                return null;
            }
            _currentPassword = password;
            return password;
        }
        catch (CommunityException ex)
        {
            ConfirmDialog.Alert("작성자 확인", ex.Message, iconKey: "IconLock");
            return null;
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void NewPostBtn_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CommunityEditorWindow(this, _service);
        if (editor.ShowDialog() == true && editor.SavedPostId is Guid id)
            await RefreshAsync(id);
    }

    private async void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPost == null) return;
        var password = await GetVerifiedPasswordAsync();
        if (password == null) return;

        if (_currentDetail == null)
        {
            try
            {
                _currentDetail = await _service.GetPostAsync(_selectedPost.Id, password);
            }
            catch (CommunityException ex)
            {
                ConfirmDialog.Alert("게시글 수정", ex.Message, iconKey: "IconPencil");
                return;
            }
        }

        var editor = new CommunityEditorWindow(this, _service, _currentDetail, password);
        if (editor.ShowDialog() == true)
            await RefreshAsync(_selectedPost.Id);
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPost == null) return;
        var password = await GetVerifiedPasswordAsync();
        if (password == null) return;

        if (!ConfirmDialog.Show(
                "게시글 삭제",
                "삭제한 게시글은 복구할 수 없습니다.",
                "삭제",
                "IconTrash2",
                danger: true))
            return;

        SetLoading(true);
        try
        {
            await _service.DeletePostAsync(_selectedPost.Id, password);
            await RefreshAsync();
        }
        catch (CommunityException ex)
        {
            ConfirmDialog.Alert("게시글 삭제", ex.Message, iconKey: "IconTrash2");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync(_selectedPost?.Id);
    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await RefreshAsync();
        e.Handled = true;
    }

    private async void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) await RefreshAsync();
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NewPostBtn_Click(NewPostBtn, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
