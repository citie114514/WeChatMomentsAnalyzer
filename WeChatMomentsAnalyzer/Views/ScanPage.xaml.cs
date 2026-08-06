using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeChatMomentsAnalyzer.ViewModels;

namespace WeChatMomentsAnalyzer.Views;

public sealed partial class ScanPage : Page
{
    public ScanViewModel ViewModel { get; }

    public ScanPage()
    {
        InitializeComponent();
        ViewModel = new ScanViewModel(DispatcherQueue);
    }
}

public sealed class BoolNegationConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, System.Type targetType, object parameter, string language)
        => value is bool b ? !b : value;
}
