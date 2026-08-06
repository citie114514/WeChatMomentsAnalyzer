using Microsoft.UI.Xaml.Controls;
using WeChatMomentsAnalyzer.ViewModels;

namespace WeChatMomentsAnalyzer.Views;

public sealed partial class FriendSearchPage : Page
{
    public FriendSearchViewModel ViewModel { get; }

    public FriendSearchPage()
    {
        InitializeComponent();
        ViewModel = new FriendSearchViewModel(DispatcherQueue);
    }

    private void FriendBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string s)
        {
            ViewModel.FriendName = s;
        }
        ViewModel.SearchCommand.Execute(null);
    }

    private void FriendBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s)
        {
            ViewModel.FriendName = s;
        }
    }
}
