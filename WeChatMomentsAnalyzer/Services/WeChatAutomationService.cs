using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using OpenCvSharp;
using WeChatMomentsAnalyzer.Data;
using WeChatMomentsAnalyzer.Models;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace WeChatMomentsAnalyzer.Services;

/// <summary>
/// 微信 PC 客户端 UI 自动化服务。
/// 参考 March7thAssistant：SendInput 真实硬件模拟 + CopyFromScreen 前台截图 + OpenCV 模板匹配。
/// </summary>
public sealed class WeChatAutomationService
{
    private static readonly string[] WeChatMainClasses = new[] { "WeChatMainWndForPC", "WeChatMainWndForXX" };
    private static readonly string[] WeChatProcessNames = new[] { "WeChat", "Weixin" };
    private const string MomentsTitleHint = "朋友圈";
    private const byte VK_ESCAPE = 0x1B;

    // 微信 4.x 主窗口布局校准（物理像素，相对窗口左上角）
    private const int AvatarCenterX = 55;
    private const int AvatarCenterY = 92;
    private const int SidebarMomentsX = 55;
    private const int SidebarMomentsY = 459;
    // 个人面板内"朋友圈"行校准（相对面板窗口左上角，UIA 定位失败时兜底）
    private const int PanelMomentsRelX = 245;
    private const int PanelMomentsRelY = 233;

    public event Action<ScanProgress>? ProgressChanged;
    public event Action<string>? Log;

    // OCR 服务（Windows.Media.Ocr），识别详情页点赞/评论区昵称文本
    private readonly OcrService _ocr = new();

    // 扫描日志同步落盘，便于离线诊断
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatMomentsAnalyzer", "scan_log.txt");

    private void LogMsg(string msg)
    {
        Log?.Invoke(msg);
        try { File.AppendAllText(LogFilePath, DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine); }
        catch { /* 日志落盘失败不影响扫描 */ }
    }

    private static UIA3Automation CreateAutomation() => new();

    public async Task ScanAsync(ScanConfig config, MomentsRepository repo, CancellationToken ct = default)
    {
        LogMsg("正在查找微信主窗口…");
        var mainWnd = FindWeChatMainWindow();
        if (mainWnd == null)
            throw new InvalidOperationException("未找到微信主窗口，请先启动并登录微信 PC 客户端。");
        LogMsg($"找到微信窗口：0x{mainWnd.Value.ToInt64():X}");

        // 激活微信窗口
        BringToFront(mainWnd.Value);
        await Task.Delay(500, ct);

        LogMsg("正在打开朋友圈…");
        var momentsWnd = await OpenMomentsAsync(mainWnd.Value, config, ct);
        if (momentsWnd == null)
            throw new InvalidOperationException("未能自动打开朋友圈，请手动打开朋友圈后重试。");

        // 扫描期间最小化本程序，避免遮挡微信
        MinimizeOwnWindow();
        BringToFront(momentsWnd.Value);
        await Task.Delay(300, ct);

        var targetWnd = momentsWnd.Value;
        using var automation = CreateAutomation();
        var momentsEl = automation.FromHandle(targetWnd);

        try
        {
            // 相册页可能停留在上次会话的中间/底部：先滚回顶部再开始扫描
            if (!IsDetailView(momentsEl) && !IsFeedView(momentsEl))
            {
                LogMsg("相册滚回顶部…");
                BringToFront(targetWnd);
                GetClientRect(targetWnd, out RECT topRc);
                // 单次大滚轮会被微信限幅，分多次小步滚到顶
                for (int i = 0; i < 10; i++)
                {
                    ImageAutomationHelper.ScrollClient(targetWnd, WHEEL_DELTA * 6, topRc.Right / 2, topRc.Bottom / 2);
                    await Task.Delay(200, ct);
                }
                await Task.Delay(800, ct);
            }

            var seenHashes = new HashSet<string>(StringComparer.Ordinal);
            int totalScreens = 0;
            int totalMoments = 0;
            int totalAvatars = 0;
            int emptyStreak = 0;
            string prevScreenFingerprint = string.Empty;

            while (totalScreens < config.MaxScrollScreens)
            {
                ct.ThrowIfCancellationRequested();
                totalScreens++;

                // 若停留在详情页，先返回列表
                if (IsDetailView(momentsEl))
                    await GoBackToListAsync(momentsEl, targetWnd, ct);

                // 若停在朋友圈信息流页：通过主窗口面板入口把朋友圈窗口导航到个人相册页
                if (IsFeedView(momentsEl))
                {
                    LogMsg("当前在朋友圈信息流页，通过面板入口切换到个人相册…");
                    await TryOpenMomentsViaPanelAsync(mainWnd.Value, ct);
                    BringToFront(targetWnd);
                    await WaitViewAsync(momentsEl, detail: false, 3000, ct);
                }

                var items = GetListPageItems(momentsEl, targetWnd);
                if (items.Count == 0)
                {
                    emptyStreak++;
                    if (emptyStreak >= 3)
                    {
                        LogMsg("连续多屏未发现带日期的相册条目，结束扫描。");
                        break;
                    }
                    LogMsg($"第 {totalScreens} 屏：未发现新的相册条目，继续滚动…");
                }
                else
                {
                    emptyStreak = 0;
                }

                string fingerprint = string.Join("|", items.Select(i => i.Name));
                int screenCount = 0;

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = ComputeHash(item.Name);
                    if (!seenHashes.Add(key)) continue;

                    var saved = await ScanOneMomentAsync(targetWnd, momentsEl, item, config, repo, ct);
                    if (saved != null)
                    {
                        totalMoments++;
                        totalAvatars += saved.Value;
                        screenCount++;
                    }
                }

                ProgressChanged?.Invoke(new ScanProgress
                {
                    ScreensScanned = totalScreens,
                    TotalScreens = config.MaxScrollScreens,
                    MomentsThisScreen = screenCount,
                    MomentsTotal = totalMoments
                });
                LogMsg($"第 {totalScreens} 屏：新入库 {screenCount} 条，累计 {totalMoments} 条，头像 {totalAvatars} 个");

                if (fingerprint == prevScreenFingerprint && totalScreens > 1)
                {
                    LogMsg("滚动后内容未变化，已到达底部，结束扫描。");
                    break;
                }
                prevScreenFingerprint = fingerprint;

                await ScrollDownOneScreenAsync(targetWnd, config.ScrollWaitMs, ct);
            }

            LogMsg($"扫描完成：共 {totalScreens} 屏，{totalMoments} 条朋友圈入库，记录头像 {totalAvatars} 个。");
        }
        finally
        {
            RestoreOwnWindow();
        }
    }

    // ====== 联系人爬取 ======

    private record struct ContactItemInfo(string Name, Rectangle Rect);

    // 通讯录中的系统/功能条目，不作为联系人保存
    private static readonly string[] ContactSystemNames =
        { "新的朋友", "群聊", "公众号", "标签", "企业微信联系人", "设备", "聊天信息", "小程序", "视频号" };

    /// <summary>
    /// 扫描微信通讯录，读取每个联系人昵称并截取头像，保存到 Contacts/&lt;昵称&gt;.png。
    /// 建立联系人头像库后，扫描朋友圈详情时可按头像模板匹配识别点赞人昵称。
    /// </summary>
    public async Task<int> ScanContactsAsync(ScanConfig config, CancellationToken ct = default)
    {
        LogMsg("正在查找微信主窗口…");
        var mainWnd = FindWeChatMainWindow();
        if (mainWnd == null)
            throw new InvalidOperationException("未找到微信主窗口，请先启动并登录微信 PC 客户端。");

        MinimizeOwnWindow();
        try
        {
            BringToFront(mainWnd.Value);
            await Task.Delay(500, ct);

            if (!await OpenContactsAsync(mainWnd.Value, ct))
                throw new InvalidOperationException("未能自动打开通讯录，请手动切换到通讯录页后重试。");

            Directory.CreateDirectory(config.ContactAvatarsDirectory);

            using var automation = CreateAutomation();
            var root = automation.FromHandle(mainWnd.Value);
            GetWindowRect(mainWnd.Value, out RECT wrc);
            var wndRect = new Rectangle(wrc.Left, wrc.Top, wrc.Right - wrc.Left, wrc.Bottom - wrc.Top);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            string prevFp = string.Empty;
            int emptyStreak = 0;
            int scrollLevel = 0;

            for (int screen = 0; screen < 80; screen++)
            {
                ct.ThrowIfCancellationRequested();
                var items = GetContactItems(root, mainWnd.Value);
                string fp = string.Join("|", items.Select(i => i.Name));

                foreach (var item in items)
                {
                    if (!seen.Add(item.Name)) continue;
                    // 截取头像：ListItem 左侧约 40x40 区域（头像通常在条目左侧）；底部截断条目夹紧避免截到窗口外
                    int ax = item.Rect.Left + 6;
                    int ay = item.Rect.Top + Math.Max(2, (item.Rect.Height - 40) / 2);
                    ay = Math.Min(ay, wndRect.Bottom - 46);
                    var avRect = new Rectangle(ax, ay, 40, 40);
                    using var av = ImageAutomationHelper.CaptureRegion(avRect);
                    string path = Path.Combine(config.ContactAvatarsDirectory, SanitizeFileName(item.Name) + ".png");
                    ImageAutomationHelper.SaveDebug(av, path);
                    count++;
                }

                LogMsg($"联系人第 {screen + 1} 屏：本屏 {items.Count} 项，累计 {count} 个");

                if (fp == prevFp)
                {
                    if (scrollLevel < 2)
                    {
                        scrollLevel++;
                        LogMsg($"联系人滚动无效（内容未变），切换滚动策略 {scrollLevel + 1}");
                    }
                    else if (++emptyStreak >= 2) { LogMsg("通讯录已滚动到底，结束。"); break; }
                }
                else
                {
                    emptyStreak = 0;
                    scrollLevel = 0;
                }
                prevFp = fp;

                await ScrollContactsAsync(mainWnd.Value, root, wndRect, items, scrollLevel, ct);
            }

            LogMsg($"联系人扫描完成：共保存 {count} 个联系人头像 → {config.ContactAvatarsDirectory}");
            return count;
        }
        finally
        {
            RestoreOwnWindow();
        }
    }

    /// <summary>
    /// 定向滚动联系人列表。微信对单次大滚轮 delta 限幅、且光标悬停列表项时滚动可能失效，
    /// 故用小步多次滚轮，无效时按策略升级：①列表水平中心 ②列表滚动条带 ③UIA ScrollPattern。
    /// 滚完把鼠标移回标题栏，清除列表项悬停态。
    /// </summary>
    private async Task ScrollContactsAsync(IntPtr wnd, AutomationElement root, Rectangle wndRect,
        List<ContactItemInfo> items, int level, CancellationToken ct)
    {
        int listLeft = items.Count > 0 ? items.Min(i => i.Rect.Left) : wndRect.Left + 90;
        int listRight = items.Count > 0 ? items.Max(i => i.Rect.Right) : wndRect.Left + 450;
        int sy = wndRect.Top + wndRect.Height / 2;

        BringToFront(wnd);
        if (level == 0)
        {
            ImageAutomationHelper.ScrollScreenBursts(wnd, -WHEEL_DELTA, 6, 120, (listLeft + listRight) / 2, sy);
        }
        else if (level == 1)
        {
            ImageAutomationHelper.ScrollScreenBursts(wnd, -WHEEL_DELTA, 6, 120, listRight - 8, sy);
        }
        else
        {
            var pt = new Point((listLeft + listRight) / 2, sy);
            if (!TryScrollPattern(root, pt))
                ImageAutomationHelper.ScrollScreenBursts(wnd, -WHEEL_DELTA, 10, 100, pt.X, pt.Y);
        }
        await Task.Delay(700, ct);

        // 移开鼠标，避免悬停锚定阻碍下一次滚动
        ImageAutomationHelper.MoveCursor(wndRect.Left + wndRect.Width / 2, wndRect.Top + 16);
    }

    /// <summary>尝试用 UIA ScrollPattern 滚动包含指定屏幕点的容器（不依赖鼠标滚轮，免疫悬停/光标问题）。</summary>
    private static bool TryScrollPattern(AutomationElement root, Point screenPt)
    {
        try
        {
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    var r = el.BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    if (screenPt.X < r.Left || screenPt.X > r.Right || screenPt.Y < r.Top || screenPt.Y > r.Bottom) continue;
                    var sp = el.Patterns.Scroll.PatternOrDefault;
                    if (sp == null) continue;
                    sp.Scroll(FlaUI.Core.Definitions.ScrollAmount.NoAmount, FlaUI.Core.Definitions.ScrollAmount.LargeIncrement);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>打开通讯录页：优先 UIA 查找名为"通讯录"的入口并点击，兜底侧边栏固定坐标。</summary>
    private async Task<bool> OpenContactsAsync(IntPtr mainWnd, CancellationToken ct)
    {
        using var automation = CreateAutomation();
        var root = automation.FromHandle(mainWnd);

        foreach (var el in root.FindAllDescendants())
        {
            string name;
            try { name = el.Name; } catch { continue; }
            if (string.IsNullOrWhiteSpace(name) || !name.Contains("\u901a\u8baf\u5f55", StringComparison.Ordinal)) continue;
            var r = el.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) continue;
            LogMsg($"点击通讯录入口 ({r.Left + r.Width / 2}, {r.Top + r.Height / 2})");
            ImageAutomationHelper.ClickScreen(mainWnd, r.Left + r.Width / 2, r.Top + r.Height / 2);
            await Task.Delay(1000, ct);
            return true;
        }

        // 兜底：侧边栏固定坐标（通讯录入口在朋友圈上方，间距约 64）
        var rect = await WaitForStableRectAsync(mainWnd, ct);
        int cx = rect.Left + SidebarMomentsX;
        int cy = rect.Top + SidebarMomentsY - 64;
        LogMsg($"UIA 未找到通讯录入口，兜底点击侧边栏 ({cx}, {cy})");
        ImageAutomationHelper.ClickScreen(mainWnd, cx, cy);
        await Task.Delay(1000, ct);
        return true;
    }

    private List<ContactItemInfo> GetContactItems(AutomationElement root, IntPtr wnd)
    {
        var result = new List<ContactItemInfo>();
        try
        {
            GetWindowRect(wnd, out RECT rc);
            var wndRect = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    if (el.ControlType != FlaUI.Core.Definitions.ControlType.ListItem) continue;
                    var r = el.BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    if (r.Top < wndRect.Top + 60 || r.Top > wndRect.Bottom - 48) continue;
                    string name;
                    try { name = el.Name?.Trim() ?? string.Empty; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (ContactSystemNames.Any(s => name.Contains(s, StringComparison.Ordinal))) continue;
                    // 过滤字母索引标题（单个 ASCII 字母）
                    if (name.Length == 1 && char.IsLetter(name[0]) && name[0] < 128) continue;
                    if (!seen.Add(name)) continue;
                    result.Add(new ContactItemInfo(name, r));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogMsg("读取联系人列表失败: " + ex.Message);
        }
        return result.OrderBy(i => i.Rect.Top).ToList();
    }

    // ====== 窗口查找 ======

    private static IntPtr? FindWeChatMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;

            var classSb = new StringBuilder(256);
            GetClassName(h, classSb, classSb.Capacity);
            string className = classSb.ToString();

            if (WeChatMainClasses.Contains(className))
            {
                found = h;
                return false;
            }

            // 微信 4.x：Qt 主窗口，标题恰为"微信"（朋友圈窗口标题为"朋友圈"，不会误认）
            if (className.StartsWith("Qt", StringComparison.Ordinal) && className.EndsWith("QWindowIcon", StringComparison.Ordinal))
            {
                var tSb = new StringBuilder(256);
                GetWindowText(h, tSb, tSb.Capacity);
                string t = tSb.ToString();
                if ((t == "\u5fae\u4fe1" || t == "Weixin" || t == "WeChat")
                    && GetWindowProcessName(h) is string pn && WeChatProcessNames.Contains(pn))
                {
                    found = h;
                    return false;
                }
                return true;
            }

            var titleSb = new StringBuilder(256);
            GetWindowText(h, titleSb, titleSb.Capacity);
            string title = titleSb.ToString();
            if (string.IsNullOrEmpty(title)) return true;

            bool titleMatches = title.Contains("微信", StringComparison.OrdinalIgnoreCase)
                             || title.Equals("Weixin", StringComparison.OrdinalIgnoreCase)
                             || title.Contains("WeChat", StringComparison.OrdinalIgnoreCase);
            if (titleMatches && GetWindowProcessName(h) is string procName && WeChatProcessNames.Contains(procName))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    private static string? GetWindowProcessName(IntPtr hWnd)
    {
        if (GetWindowThreadProcessId(hWnd, out uint pid) == 0) return null;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    // ====== 打开朋友圈 ======

    /// <summary>
    /// 打开朋友圈。微信 4.x 主路径：点击左上角头像 → 在弹出的个人面板中点击"朋友圈"；
    /// 回退路径：侧边栏朋友圈图标真实点击 → UIA Invoke → 模板匹配（旧版微信）。
    /// 每一步都先验证朋友圈窗口确实出现，避免误触其他入口。
    /// </summary>
    private async Task<IntPtr?> OpenMomentsAsync(IntPtr mainWnd, ScanConfig config, CancellationToken ct)
    {
        if (FindMomentsWindow() is IntPtr already) return already;

        MinimizeOwnWindow();
        BringToFront(mainWnd);
        var rect = await WaitForStableRectAsync(mainWnd, ct);

        SaveDebugScreenshot(mainWnd, "main_win");

        // 1) 微信 4.x：左上角头像 → 个人面板"朋友圈"（打开/导航到个人相册页）
        if (await TryOpenMomentsViaPanelAsync(mainWnd, ct))
        {
            if (VerifyMomentsOpened(mainWnd, out var wnd))
            {
                RestoreOwnWindow();
                return wnd;
            }
        }

        // 关闭可能残留的面板，避免遮挡后续点击
        ImageAutomationHelper.PostKey(mainWnd, VK_ESCAPE);
        await Task.Delay(400, ct);

        // 2) 侧边栏朋友圈图标真实点击（微信 4.x 侧边栏固定位置）
        LogMsg("尝试点击侧边栏朋友圈图标…");
        ImageAutomationHelper.ClickScreen(mainWnd, rect.Left + SidebarMomentsX, rect.Top + SidebarMomentsY);
        await Task.Delay(1800, ct);
        if (VerifyMomentsOpened(mainWnd, out var wnd2))
        {
            RestoreOwnWindow();
            return wnd2;
        }

        // 3) UIA Invoke（旧版微信）
        if (TryInvokeMomentsControl(mainWnd))
        {
            await Task.Delay(2000, ct);
            if (VerifyMomentsOpened(mainWnd, out var wnd3))
            {
                RestoreOwnWindow();
                return wnd3;
            }
        }

        // 4) 侧边栏模板匹配（旧版微信），仅在侧边栏区域且高置信度时使用
        int sidebarWidth = Math.Min(70, rect.Right - rect.Left);
        var sidebarRoi = new Rectangle(rect.Left, rect.Top, sidebarWidth, rect.Bottom - rect.Top);
        const double safeTemplateThreshold = 0.80;
        if (TryClickTemplate(mainWnd, config.MomentsIconTemplatePath, sidebarRoi,
            Math.Max(config.MatchThreshold, safeTemplateThreshold), "Moments sidebar icon"))
        {
            await Task.Delay(2000, ct);
            if (VerifyMomentsOpened(mainWnd, out var wnd4))
            {
                RestoreOwnWindow();
                return wnd4;
            }
        }

        LogMsg("Moments entry was not verified. Automatic clicks stopped safely.");
        RestoreOwnWindow();
        return null;
    }

    /// <summary>
    /// 微信 4.x：点击主窗口左上角头像弹出个人面板，再点击面板内"朋友圈"。
    /// 朋友圈窗口已存在时，该入口会把它导航到个人相册页（扫描目标页）。
    /// </summary>
    private async Task<bool> TryOpenMomentsViaPanelAsync(IntPtr mainWnd, CancellationToken ct)
    {
        MinimizeOwnWindow();
        BringToFront(mainWnd);
        var rect = await WaitForStableRectAsync(mainWnd, ct);

        LogMsg("点击左上角头像，打开个人面板…");
        ImageAutomationHelper.ClickScreen(mainWnd, rect.Left + AvatarCenterX, rect.Top + AvatarCenterY);
        await Task.Delay(1000, ct);

        var panelWnd = FindPanelWindow();
        if (panelWnd == null)
        {
            LogMsg("个人面板未弹出。");
            return false;
        }

        GetWindowRect(panelWnd.Value, out RECT panelRect);
        var pt = FindMomentsClickPointInPanel(panelWnd.Value)
                 ?? new Point(panelRect.Left + PanelMomentsRelX, panelRect.Top + PanelMomentsRelY);
        LogMsg($"点击面板内朋友圈入口 ({pt.X}, {pt.Y})");
        ImageAutomationHelper.ClickScreen(mainWnd, pt.X, pt.Y);
        await Task.Delay(1800, ct);
        return true;
    }

    /// <summary>等待窗口恢复动画结束，返回稳定的窗口矩形。</summary>
    private static async Task<RECT> WaitForStableRectAsync(IntPtr hWnd, CancellationToken ct)
    {
        GetWindowRect(hWnd, out RECT prev);
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(250, ct);
            GetWindowRect(hWnd, out RECT cur);
            if (cur.Left == prev.Left && cur.Top == prev.Top && cur.Right == prev.Right && cur.Bottom == prev.Bottom)
                return cur;
            prev = cur;
        }
        return prev;
    }

    /// <summary>微信 4.x 点击头像后弹出的个人面板（独立顶层窗口，类名以 QWindowToolSaveBits 结尾）。</summary>
    private static IntPtr? FindPanelWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().EndsWith("QWindowToolSaveBits", StringComparison.Ordinal)
                && GetWindowProcessName(h) is string p && WeChatProcessNames.Contains(p))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    /// <summary>
    /// 在个人面板内定位"朋友圈"入口点击点：优先右侧缩略图宽 Button，
    /// 其次"朋友圈"文本右侧（整行均可点击）。
    /// </summary>
    private Point? FindMomentsClickPointInPanel(IntPtr panelWnd)
    {
        try
        {
            using var automation = CreateAutomation();
            var root = automation.FromHandle(panelWnd);
            Point? label = null;
            foreach (var el in root.FindAllDescendants())
            {
                string name;
                try { name = el.Name; }
                catch { continue; }

                var r = el.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;

                if (string.Equals(name, "\u670b\u53cb\u5708", StringComparison.Ordinal))
                    label ??= new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);

                // 缩略图行是一个宽 Button，点击必然打开朋友圈
                if (el.ControlType == FlaUI.Core.Definitions.ControlType.Button && r.Width >= 150 && r.Height >= 40)
                    return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            }
            if (label.HasValue) return new Point(label.Value.X + 180, label.Value.Y + 15);
        }
        catch (Exception ex)
        {
            LogMsg("UIA 定位面板朋友圈入口失败: " + ex.Message);
        }
        return null;
    }

    /// <summary>验证朋友圈是否打开：独立窗口或主窗口内嵌页面。</summary>
    private bool VerifyMomentsOpened(IntPtr mainWnd, out IntPtr momentsWnd)
    {
        if (FindMomentsWindow() is IntPtr m)
        {
            LogMsg($"朋友圈窗口已打开：0x{m.ToInt64():X}");
            momentsWnd = m;
            return true;
        }
        if (IsMomentsPageVisibleInMainWindow(mainWnd))
        {
            momentsWnd = mainWnd;
            return true;
        }
        momentsWnd = IntPtr.Zero;
        return false;
    }

    private bool TryInvokeMomentsControl(IntPtr mainWnd)
    {
        try
        {
            using var automation = CreateAutomation();
            var root = automation.FromHandle(mainWnd);
            foreach (var element in root.FindAllDescendants())
            {
                string name;
                try { name = element.Name; }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(name) ||
                    !name.Contains("\u670b\u53cb\u5708", StringComparison.Ordinal))
                    continue;

                var invokePattern = element.Patterns.Invoke.PatternOrDefault;
                if (invokePattern == null) continue;

                LogMsg($"UI Automation found Moments control: {name}");
                invokePattern.Invoke();
                return true;
            }

            LogMsg("UI Automation did not expose a callable Moments control.");
            return false;
        }
        catch (Exception ex)
        {
            LogMsg($"UI Automation could not invoke Moments: {ex.Message}");
            return false;
        }
    }

    private bool IsMomentsPageVisibleInMainWindow(IntPtr mainWnd)
    {
        try
        {
            using var automation = CreateAutomation();
            var root = automation.FromHandle(mainWnd);
            var rootRect = root.BoundingRectangle;
            foreach (var element in root.FindAllDescendants())
            {
                string name;
                try { name = element.Name; }
                catch { continue; }

                if (!string.Equals(name?.Trim(), "\u670b\u53cb\u5708", StringComparison.Ordinal))
                    continue;

                var rect = element.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                // The sidebar entry is on the far left; the page title/content appears in the main area.
                if (rect.Left > rootRect.Left + 120 && rect.Top > rootRect.Top + 20)
                {
                    LogMsg("Moments page is visible in the main WeChat window.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogMsg($"Could not verify embedded Moments page: {ex.Message}");
        }

        return false;
    }

    private void SaveDebugScreenshot(IntPtr wnd, string suffix)
    {
        try
        {
            GetWindowRect(wnd, out RECT rc);
            var rect = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            using var mat = ImageAutomationHelper.CaptureRegion(rect);
            ImageAutomationHelper.SaveDebug(mat, Path.Combine(ImageAutomationHelper.DebugDirectory, $"screenshot_{suffix}_{DateTime.Now:HHmmssfff}.png"));
        }
        catch { }
    }

    private bool TryClickTemplate(IntPtr wnd, string templatePath, Rectangle searchRoi, double threshold, string label)
    {
        if (!File.Exists(templatePath))
        {
            LogMsg($"{label}: 模板不存在 {templatePath}");
            return false;
        }

        var template = ImageAutomationHelper.LoadTemplate(templatePath);
        if (template == null)
        {
            LogMsg($"{label}: 模板加载失败");
            return false;
        }

        using (template)
        {
            // 前台截图（微信需在前台）
            var roiMat = ImageAutomationHelper.CaptureRegion(searchRoi);
            using (roiMat)
            {
                // 保存调试图
                string debugName = $"{label.Replace("/", "_").Replace("\\", "_")}_{DateTime.Now:HHmmssfff}";
                ImageAutomationHelper.SaveDebug(roiMat, Path.Combine(ImageAutomationHelper.DebugDirectory, $"{debugName}_roi.png"));
                ImageAutomationHelper.SaveDebug(template, Path.Combine(ImageAutomationHelper.DebugDirectory, $"{debugName}_template.png"));

                // 多尺度模板匹配，适配用户截图尺寸不一
                var pt = ImageAutomationHelper.FindTemplate(roiMat, template, threshold);
                if (!pt.HasValue)
                {
                    LogMsg($"{label}: 未匹配到");
                    return false;
                }

                int sx = searchRoi.X + pt.Value.X;
                int sy = searchRoi.Y + pt.Value.Y;
                LogMsg($"{label}: 匹配成功，真实点击 ({sx}, {sy})");
                ImageAutomationHelper.ClickScreen(wnd, sx, sy);
                return true;
            }
        }
    }

    private static IntPtr? FindMomentsWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;

            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            if (!title.ToString().Contains(MomentsTitleHint)) return true;

            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            if (WeChatMainClasses.Contains(cls))
            {
                found = h;
                return false;
            }

            // 微信 4.x：Qt 窗口 + 微信进程（按进程名排除本程序等其他窗口）
            if (cls.StartsWith("Qt", StringComparison.Ordinal)
                && GetWindowProcessName(h) is string p && WeChatProcessNames.Contains(p))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    // ====== 朋友圈内容提取（微信 4.x：列表 ListItem → 点入详情） ======

    private record struct ListItemInfo(string Name, Rectangle Rect);

    private static readonly Regex DetailTimeRegex = new(@"\d{4}年\d{1,2}月\d{1,2}日\s*\d{1,2}:\d{2}");
    private static readonly Regex MediaPhraseRegex = new(@"包含\d+(张图片|段视频|个视频|张图文)");
    // 个人相册页（扫描目标页）条目以左侧日期开头："8月04 04 内容…"/"今天 …"/"昨天 …"；
    // "置顶"与个性签名等条目不以日期开头，用此前缀正则排除
    private static readonly Regex AlbumItemDatePrefixRegex = new(@"^(\d{1,2}月\d{1,2}|\d{4}年\d{1,2}月\d{1,2}|\d+\s*(分钟|小时|天)前|昨天|今天)");

    /// <summary>读取朋友圈列表页中完整可见的 ListItem（每条朋友圈一个，名称含日期和内容摘要）。</summary>
    private List<ListItemInfo> GetListPageItems(AutomationElement root, IntPtr wnd)
    {
        var result = new List<ListItemInfo>();
        try
        {
            GetWindowRect(wnd, out RECT rc);
            var wndRect = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    if (el.ControlType != FlaUI.Core.Definitions.ControlType.ListItem) continue;
                    var r = el.BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0) continue;

                    // 只保留完整落在窗口内的条目（工具栏高约 72px）
                    if (r.Top < wndRect.Top + 72 || r.Bottom > wndRect.Bottom - 10) continue;

                    string name;
                    try { name = el.Name?.Trim() ?? string.Empty; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    // 真正的条目名称含日期+内容摘要；过滤"置顶"等徽章小元素
                    if (name.Length < 8) continue;
                    // 跳过"置顶"条目
                    if (name.StartsWith("\u7f6e\u9876", StringComparison.Ordinal)) continue;
                    // 必须以左侧日期开头（今天/昨天/M月D日…），排除签名等非朋友圈条目
                    if (!AlbumItemDatePrefixRegex.IsMatch(name)) continue;
                    if (!seen.Add(name)) continue; // UIA 偶尔重复暴露同一 ListItem

                    result.Add(new ListItemInfo(name, r));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogMsg("读取朋友圈列表失败: " + ex.Message);
        }
        return result.OrderBy(i => i.Rect.Top).ToList();
    }

    /// <summary>是否处于详情页（标题区出现"详情"二字）。</summary>
    private static bool IsDetailView(AutomationElement root)
        => FindElementByName(root, "\u8be6\u60c5") != null;

    /// <summary>是否处于个人相册列表页（标题区出现"相册"二字，即扫描目标页）。</summary>
    private static bool IsListView(AutomationElement root)
        => FindElementByName(root, "\u76f8\u518c") != null;

    /// <summary>是否处于朋友圈信息流页（存在名为"朋友圈"的 List 控件）。</summary>
    private static bool IsFeedView(AutomationElement root)
    {
        try
        {
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    if (el.ControlType != FlaUI.Core.Definitions.ControlType.List) continue;
                    if (string.Equals(el.Name?.Trim(), "\u670b\u53cb\u5708", StringComparison.Ordinal)) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static FlaUI.Core.AutomationElements.AutomationElement? FindElementByName(AutomationElement root, string name)
    {
        try
        {
            foreach (var el in root.FindAllDescendants())
            {
                string n;
                try { n = el.Name; } catch { continue; }
                if (string.Equals(n?.Trim(), name, StringComparison.Ordinal)) return el;
            }
        }
        catch { }
        return null;
    }

    private static Point? FindButtonCenterByName(AutomationElement root, string name)
    {
        try
        {
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    if (el.ControlType != FlaUI.Core.Definitions.ControlType.Button) continue;
                    if (!string.Equals(el.Name?.Trim(), name, StringComparison.Ordinal)) continue;
                    var r = el.BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 扫描一条朋友圈：点击列表条目进入详情 → 解析发布者/内容/日期 →
    /// 记录日期下方（点赞/评论区）的头像 → 点击左上角"返回"回到列表。
    /// 返回值为本条记录的头像数；失败返回 null。
    /// </summary>
    private async Task<int?> ScanOneMomentAsync(
        IntPtr wnd, AutomationElement root, ListItemInfo item,
        ScanConfig config, MomentsRepository repo, CancellationToken ct)
    {
        try
        {
            // 1) 点入详情。相册条目布局：左侧日期、中间图片九宫格、右侧文字。
            //    优先点击图片区进入详情；无图条目该位置落在文字上同样可进入。
            int imgX = item.Rect.Left + 245;
            int imgY = item.Rect.Top + Math.Max(60, item.Rect.Height / 2);
            if (!await TryEnterDetailAsync(wnd, root, imgX, imgY, config, ct))
            {
                // 回退：点击右侧文字区
                int txtX = Math.Min(item.Rect.Left + 520, item.Rect.Right - 40);
                int txtY = item.Rect.Top + Math.Min(56, item.Rect.Height / 2);
                if (!await TryEnterDetailAsync(wnd, root, txtX, txtY, config, ct))
                {
                    // 可能误触跳到了其他窗口（资料页等），按 Esc 收起浮层后再试一次图片区
                    DismissOverlays(wnd);
                    BringToFront(wnd);
                    await Task.Delay(600, ct);
                    if (!await TryEnterDetailAsync(wnd, root, imgX, imgY, config, ct))
                    {
                        LogMsg($"未能进入详情：{Truncate(item.Name, 24)}");
                        return null;
                    }
                }
            }
            await Task.Delay(800, ct);

            // 2) 详情内长文本 ListItem 即帖子本体：发布者 内容 包含N张图片 时间
            string detailName = GetDetailPostName(root) ?? item.Name;
            var post = ParseDetailName(detailName);
            LogMsg($"进入详情：{Truncate(post.Publisher, 12)} | {Truncate(post.Content, 24)}");

            // 3) 记录日期下方（点赞/评论区）的头像与点赞/评论昵称
            var (avatarCount, likerNames) = await RecordDetailAvatarsAsync(wnd, root, post, config, ct);
            post.Likers = likerNames;

            repo.UpsertMoment(post);

            // 4) 点击左上角"返回"回到列表
            await GoBackToListAsync(root, wnd, ct);
            return avatarCount;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogMsg("扫描该条失败: " + ex.Message);
            // 尽力回到列表，避免卡死在详情页
            try { await GoBackToListAsync(root, wnd, ct); } catch { }
            return null;
        }
    }

    /// <summary>详情内帖子本体的 ListItem 名称最长（含发布者、内容、时间）。</summary>
    private static string? GetDetailPostName(AutomationElement root)
    {
        string? best = null;
        try
        {
            foreach (var el in root.FindAllDescendants())
            {
                try
                {
                    if (el.ControlType != FlaUI.Core.Definitions.ControlType.ListItem) continue;
                    var name = el.Name?.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (best == null || name.Length > best.Length) best = name;
                }
                catch { }
            }
        }
        catch { }
        return best;
    }

    /// <summary>解析详情 ListItem 名称："发布者 内容 包含1张图片 2026年8月2日 18:27"。</summary>
    private MomentPost ParseDetailName(string detailName)
    {
        var post = new MomentPost { ScanTime = DateTime.Now };
        string s = detailName.Trim();

        var tm = DetailTimeRegex.Match(s);
        string? timeText = null;
        if (tm.Success)
        {
            timeText = tm.Value;
            s = s[..tm.Index];
        }
        s = MediaPhraseRegex.Replace(s, " ").Trim();

        // 第一个空白前为发布者昵称，其余为正文
        int sep = s.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
        if (sep > 0)
        {
            post.Publisher = s[..sep].Trim();
            post.Content = s[(sep + 1)..].Trim();
        }
        else
        {
            post.Content = s;
        }

        if (timeText != null) post.PostTime = TryParseTime(timeText);
        post.ContentHash = ComputeHash($"{post.Publisher}|{post.Content}|{timeText ?? ""}");
        return post;
    }

    /// <summary>
    /// 在详情页截取帖子（日期行）下方的点赞/评论区，检测方形头像并保存，
    /// 同时通过联系人头像库匹配 + OCR 文字行识别得到点赞/评论昵称列表。
    /// 返回 (头像数, 昵称列表)；昵称列表用于填充 MomentPost.Likers 写入 likes 表。
    /// </summary>
    private async Task<(int avatarCount, List<string> names)> RecordDetailAvatarsAsync(
        IntPtr wnd, AutomationElement root, MomentPost post, ScanConfig config, CancellationToken ct)
    {
        var empty = (0, new List<string>());
        try
        {
            GetWindowRect(wnd, out RECT rc);
            var wndRect = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);

            // 详情页有进入动画、点赞区渲染滞后：裁剪区太矮时等待重试，直到布局稳定
            int roiY = 0, roiH = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                // 帖子 ListItem 的底部即日期行下沿
                int postBottom = -1;
                int blockBottom = -1;
                foreach (var el in root.FindAllDescendants())
                {
                    try
                    {
                        if (el.ControlType != FlaUI.Core.Definitions.ControlType.ListItem) continue;
                        var r = el.BoundingRectangle;
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        var name = el.Name?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(name))
                            postBottom = Math.Max(postBottom, r.Bottom);
                        else
                            blockBottom = Math.Max(blockBottom, r.Bottom);
                    }
                    catch { }
                }

                if (postBottom <= 0) return empty;
                if (blockBottom <= postBottom) blockBottom = wndRect.Bottom - 10;
                roiY = postBottom + 4;
                // 区域限高一屏，避免长文/大图把点赞区挤出视口时截到无关内容
                int roiBottom = Math.Min(Math.Min(blockBottom, wndRect.Bottom - 10), roiY + 600);
                roiH = roiBottom - roiY;
                if (roiH >= 60) break;
                await Task.Delay(700, ct);
            }
            if (roiH < 20) return empty;

            var roi = new Rectangle(wndRect.X + 1, roiY, wndRect.Width - 2, roiH);
            using var block = ImageAutomationHelper.CaptureRegion(roi);

            string dir = Path.Combine(config.MomentsAvatarsDirectory, post.ContentHash);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("HHmmssfff");
            ImageAutomationHelper.SaveDebug(block, Path.Combine(dir, $"detail_below_{stamp}.png"));

            var avatars = ImageAutomationHelper.ExtractSquareAvatarsWithBounds(block);
            List<string> nameList;
            try
            {
                for (int i = 0; i < avatars.Count; i++)
                    ImageAutomationHelper.SaveDebug(avatars[i].Image, Path.Combine(dir, $"avatar_{stamp}_{i:00}.png"));

                var names = new HashSet<string>(StringComparer.Ordinal);

                // 路径1：联系人头像库模板匹配 —— 在点赞区截图中查找出现在联系人库里的头像
                var contacts = ImageAutomationHelper.LoadContactTemplates(config.ContactAvatarsDirectory);
                try
                {
                    if (contacts.Count > 0)
                    {
                        var matched = ImageAutomationHelper.MatchContacts(block, contacts, config.ContactMatchThreshold);
                        foreach (var (nm, _) in matched) names.Add(nm);
                    }
                }
                finally
                {
                    foreach (var kv in contacts) kv.Value.Dispose();
                }

                // 路径2：OCR 文字行识别 —— 补充联系人库未覆盖的好友昵称
                if (_ocr.IsAvailable)
                {
                    var lines = await _ocr.RecognizeAsync(block);
                    foreach (var n in OcrService.ExtractNames(lines)) names.Add(n);
                    try
                    {
                        File.WriteAllText(Path.Combine(dir, $"detail_ocr_{stamp}.txt"),
                            string.Join("\n", lines.Select(l => l.Text)));
                    }
                    catch { }
                }

                // 补充联系人库：仅在头像与昵称均唯一时存入（避免错误关联污染库）
                if (avatars.Count == 1 && names.Count == 1)
                    SaveAvatarToContacts(config, avatars[0].Image, names.First());

                if (avatars.Count > 0 || names.Count > 0)
                    LogMsg($"  日期下方 {avatars.Count} 头像，识别昵称 {names.Count} 个 → {dir}");
                nameList = names.ToList();
            }
            finally
            {
                foreach (var (img, _) in avatars) img.Dispose();
            }
            return (avatars.Count, nameList);
        }
        catch (Exception ex)
        {
            LogMsg("记录详情头像失败: " + ex.Message);
            return empty;
        }
    }

    /// <summary>把头像保存到联系人库（仅当该昵称尚无头像时），用于不断完备联系人库。</summary>
    private static void SaveAvatarToContacts(ScanConfig config, Mat avatar, string name)
    {
        try
        {
            Directory.CreateDirectory(config.ContactAvatarsDirectory);
            var path = Path.Combine(config.ContactAvatarsDirectory, SanitizeFileName(name) + ".png");
            if (File.Exists(path)) return;
            avatar.SaveImage(path);
        }
        catch { }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 点击并等待进入详情页。若点击后焦点跑到其他窗口（误触头像/图片），
    /// 先把朋友圈窗口重新拉到前台再确认。
    /// </summary>
    private async Task<bool> TryEnterDetailAsync(
        IntPtr wnd, AutomationElement root, int clickX, int clickY, ScanConfig config, CancellationToken ct)
    {
        BringToFront(wnd);
        await Task.Delay(200, ct);
        ImageAutomationHelper.ClickScreen(wnd, clickX, clickY);

        int timeoutMs = config.DetailOpenWaitMs * 3;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool broughtBack = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            if (IsDetailView(root)) return true;

            // 点击把微信带到了别的窗口（如资料页）：拉回朋友圈窗口再观察
            if (!broughtBack && GetForegroundWindow() != wnd)
            {
                BringToFront(wnd);
                broughtBack = true;
            }
        }
        return IsDetailView(root);
    }

    /// <summary>按 Esc 收起可能误开的资料弹层/浮层。</summary>
    private static void DismissOverlays(IntPtr wnd)
    {
        ImageAutomationHelper.PostKey(wnd, VK_ESCAPE);
        Thread.Sleep(400);
        ImageAutomationHelper.PostKey(wnd, VK_ESCAPE);
    }

    /// <summary>点击朋友圈窗口左上角"返回"按钮回到列表页。
    /// 最多点 3 次，应对 详情/个人主页 等多级页面堆叠；失败时按 Esc 兜底。</summary>
    private async Task GoBackToListAsync(AutomationElement root, IntPtr wnd, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            if (IsListView(root)) return;

            var back = FindButtonCenterByName(root, "\u8fd4\u56de");
            if (back.HasValue)
            {
                BringToFront(wnd);
                ImageAutomationHelper.ClickScreen(wnd, back.Value.X, back.Value.Y);
                if (await WaitViewAsync(root, detail: false, 3000, ct)) return;
            }
            ImageAutomationHelper.PostKey(wnd, VK_ESCAPE);
            if (await WaitViewAsync(root, detail: false, 2000, ct)) return;
        }
    }

    /// <summary>轮询等待视图切换完成：detail=true 等待详情页出现，否则等待列表页出现。</summary>
    private static async Task<bool> WaitViewAsync(AutomationElement root, bool detail, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            bool inDetail = IsDetailView(root);
            bool inList = IsListView(root);
            if (detail ? inDetail : inList) return true;
            await Task.Delay(300, ct);
        }
        return detail ? IsDetailView(root) : IsListView(root);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

    // ====== 时间解析 ======

    private static DateTime? TryParseTime(string s)
    {
        var m = Regex.Match(s, @"(\d{4})年(\d{1,2})月(\d{1,2})日\s*(\d{1,2}):(\d{2})");
        if (m.Success)
            return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0);
        m = Regex.Match(s, @"(\d+)\s*分钟前");
        if (m.Success) return DateTime.Now.AddMinutes(-int.Parse(m.Groups[1].Value));
        m = Regex.Match(s, @"(\d+)\s*小时前");
        if (m.Success) return DateTime.Now.AddHours(-int.Parse(m.Groups[1].Value));
        m = Regex.Match(s, @"(\d+)\s*天前");
        if (m.Success) return DateTime.Now.AddDays(-int.Parse(m.Groups[1].Value));
        m = Regex.Match(s, @"(\d{1,2})月(\d{1,2})日");
        if (m.Success)
        {
            int mo = int.Parse(m.Groups[1].Value), d = int.Parse(m.Groups[2].Value);
            int year = DateTime.Now.Year;
            if (mo > DateTime.Now.Month) year--;
            return new DateTime(year, mo, d);
        }
        m = Regex.Match(s, @"(\d{4})年(\d{1,2})月(\d{1,2})日");
        if (m.Success)
            return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        if (s.Contains("昨天")) return DateTime.Now.Date.AddDays(-1);
        if (s.Contains("前天")) return DateTime.Now.Date.AddDays(-2);
        m = Regex.Match(s, @"(\d{1,2}):(\d{2})");
        if (m.Success) return DateTime.Now.Date.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
        return null;
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes, 0, 8);
    }

    // ====== 滚动 ======

    private static async Task<bool> ScrollDownOneScreenAsync(IntPtr wnd, int waitMs, CancellationToken ct)
    {
        BringToFront(wnd);
        GetClientRect(wnd, out RECT rc);
        int cx = rc.Right / 2;
        int cy = rc.Bottom / 2;
        ImageAutomationHelper.ScrollClient(wnd, -WHEEL_DELTA * 5, cx, cy);
        await Task.Delay(waitMs, ct);
        return true;
    }

    // ====== 窗口管理 ======

    private static void MinimizeOwnWindow()
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            if (proc.MainWindowHandle != IntPtr.Zero)
                ShowWindow(proc.MainWindowHandle, 6); // SW_MINIMIZE
        }
        catch { }
    }

    private static void RestoreOwnWindow()
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            if (proc.MainWindowHandle != IntPtr.Zero)
                ShowWindow(proc.MainWindowHandle, SW_RESTORE);
        }
        catch { }
    }

    private static void BringToFront(IntPtr hWnd)
    {
        ShowWindow(hWnd, SW_RESTORE);

        // 通过 AttachThreadInput 绕过 Windows 前台窗口限制，强制激活目标窗口
        IntPtr fgWnd = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fgWnd, out _);
        uint curThread = GetCurrentThreadId();
        if (fgThread != curThread)
        {
            AttachThreadInput(curThread, fgThread, true);
        }
        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);
        if (fgThread != curThread)
        {
            AttachThreadInput(curThread, fgThread, false);
        }
    }

    // ====== Win32 P/Invoke ======

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int WHEEL_DELTA = 120;
}

public sealed class ScanProgress
{
    public int ScreensScanned { get; set; }
    public int TotalScreens { get; set; }
    public int MomentsThisScreen { get; set; }
    public int MomentsTotal { get; set; }
}
