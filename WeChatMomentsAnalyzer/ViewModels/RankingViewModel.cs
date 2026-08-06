using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WeChatMomentsAnalyzer.Models;

namespace WeChatMomentsAnalyzer.ViewModels;

public sealed partial class RankingViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty] private string _myNickname = string.Empty;
    [ObservableProperty] private ObservableCollection<FriendStats> _ranking = new();
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private FriendStats? _selectedFriend;

    public RankingViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        MyNickname = AppServices.Analysis.LoadMyNickname();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(MyNickname))
        {
            Summary = "请先在「扫描朋友圈」页填写你的微信昵称。";
            return;
        }
        AppServices.Analysis.SaveMyNickname(MyNickname);

        HasLoaded = true;
        Ranking.Clear();
        Summary = "加载中…";
        var list = await Task.Run(() => AppServices.Analysis.GetRanking());
        Ranking = new ObservableCollection<FriendStats>(list);
        Summary = list.Count > 0
            ? $"共 {list.Count} 位好友给你点过赞，最高赞 {list[0].LikeCount} 次"
            : "暂无点赞数据。可先去「扫描朋友圈」页扫描。";
    }

    [RelayCommand]
    private void Refresh()
    {
        if (HasLoaded) _ = LoadAsync();
    }
}
