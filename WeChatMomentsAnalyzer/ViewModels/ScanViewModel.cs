using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WeChatMomentsAnalyzer.Models;
using WeChatMomentsAnalyzer.Services;

namespace WeChatMomentsAnalyzer.ViewModels;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _myNickname = string.Empty;
    [ObservableProperty] private int _maxScrollScreens = 30;
    [ObservableProperty] private int _scrollWaitMs = 1200;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "尚未开始";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private int _momentCount;
    [ObservableProperty] private int _likeCount;
    [ObservableProperty] private int _contactCount;
    [ObservableProperty] private bool _isScanningContacts;

    public ScanViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        MyNickname = AppServices.Analysis.LoadMyNickname();
        var (m, l) = AppServices.Analysis.GetStats();
        MomentCount = m;
        LikeCount = l;
        ContactCount = CountContactAvatars();
    }

    private static int CountContactAvatars()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatMomentsAnalyzer", "Contacts");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.png").Count() : 0;
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;
        if (string.IsNullOrWhiteSpace(MyNickname))
        {
            StatusText = "请先填写你的微信昵称（与微信客户端显示一致）";
            return;
        }
        AppServices.Analysis.SaveMyNickname(MyNickname);

        IsScanning = true;
        Progress = 0;
        LogText = string.Empty;
        _cts = new CancellationTokenSource();

        var config = new ScanConfig
        {
            MyNickname = MyNickname.Trim(),
            MaxScrollScreens = MaxScrollScreens,
            ScrollWaitMs = ScrollWaitMs
        };

        AppServices.Automation.ProgressChanged -= OnProgress;
        AppServices.Automation.Log -= OnLog;
        AppServices.Automation.ProgressChanged += OnProgress;
        AppServices.Automation.Log += OnLog;

        StatusText = "扫描中…";
        try
        {
            await Task.Run(() => AppServices.Automation.ScanAsync(config, AppServices.Repository, _cts.Token), _cts.Token);
            var (m, l) = AppServices.Analysis.GetStats();
            MomentCount = m;
            LikeCount = l;
            StatusText = $"扫描完成：朋友圈 {m} 条，点赞 {l} 条";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
            AppendLog("[错误] " + ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    [RelayCommand]
    private async Task ScanContactsAsync()
    {
        if (IsScanningContacts) return;
        IsScanningContacts = true;
        StatusText = "扫描联系人中…";
        _cts = new CancellationTokenSource();

        AppServices.Automation.Log -= OnLog;
        AppServices.Automation.Log += OnLog;

        try
        {
            var config = new ScanConfig { MyNickname = MyNickname.Trim() };
            int n = await Task.Run(() => AppServices.Automation.ScanContactsAsync(config, _cts.Token), _cts.Token);
            ContactCount = CountContactAvatars();
            StatusText = $"联系人扫描完成：共 {n} 个";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = "联系人扫描失败：" + ex.Message;
            AppendLog("[错误] " + ex);
        }
        finally
        {
            IsScanningContacts = false;
        }
    }

    private void OnProgress(ScanProgress p)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Progress = p.TotalScreens > 0 ? (double)p.ScreensScanned / p.TotalScreens * 100 : 0;
            StatusText = $"第 {p.ScreensScanned}/{p.TotalScreens} 屏，本屏 {p.MomentsThisScreen} 条，累计 {p.MomentsTotal} 条";
        });
    }

    private void OnLog(string msg) => _dispatcher.TryEnqueue(() => AppendLog(msg));

    private void AppendLog(string msg)
    {
        LogText = DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine + LogText;
    }
}
