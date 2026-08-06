using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WeChatMomentsAnalyzer.Models;

namespace WeChatMomentsAnalyzer.ViewModels;

public sealed partial class FriendSearchViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty] private string _myNickname = string.Empty;
    [ObservableProperty] private string _friendName = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _suggestions = new();
    [ObservableProperty] private ObservableCollection<MomentPost> _results = new();
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private string _summary = string.Empty;

    public Visibility IsEmptyVisible => HasSearched && Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(IsEmptyVisible));
    partial void OnResultsChanged(ObservableCollection<MomentPost> value) => OnPropertyChanged(nameof(IsEmptyVisible));

    public FriendSearchViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        MyNickname = AppServices.Analysis.LoadMyNickname();
    }

    partial void OnFriendNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 1)
        {
            Suggestions.Clear();
            return;
        }
        var all = AppServices.Analysis.GetAllLikerNames();
        Suggestions = new ObservableCollection<string>(
            all.Where(n => n.Contains(value)).Take(10));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(FriendName)) return;
        if (string.IsNullOrWhiteSpace(MyNickname))
        {
            Summary = "请先在「扫描朋友圈」页填写你的微信昵称。";
            return;
        }
        AppServices.Analysis.SaveMyNickname(MyNickname);

        HasSearched = true;
        Results.Clear();
        Summary = "查询中…";

        var list = await Task.Run(() => AppServices.Analysis.GetMomentsLikedByFriend(FriendName.Trim()));
        Results = new ObservableCollection<MomentPost>(list);
        Summary = list.Count > 0
            ? $"「{FriendName}」共给你点过赞 {list.Count} 条朋友圈"
            : $"没有找到「{FriendName}」给你点赞的记录。可先去「扫描朋友圈」页扫描。";
    }
}
