using Microsoft.UI.Xaml.Controls;
using WeChatMomentsAnalyzer.ViewModels;

namespace WeChatMomentsAnalyzer.Views;

public sealed partial class RankingPage : Page
{
    public RankingViewModel ViewModel { get; }

    public RankingPage()
    {
        InitializeComponent();
        ViewModel = new RankingViewModel(DispatcherQueue);
        Loaded += (s, e) => ViewModel.LoadCommand.Execute(null);
    }
}
