using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeChatMomentsAnalyzer.Views;

namespace WeChatMomentsAnalyzer;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "微信朋友圈点赞分析器";
        ExtendsContentIntoTitleBar = true;
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Type? pageType = tag switch
            {
                "Scan" => typeof(ScanPage),
                "Friend" => typeof(FriendSearchPage),
                "Ranking" => typeof(RankingPage),
                _ => null
            };
            if (pageType != null)
            {
                ContentFrame.Navigate(pageType, null, args.RecommendedNavigationTransitionInfo);
            }
        }
    }
}
