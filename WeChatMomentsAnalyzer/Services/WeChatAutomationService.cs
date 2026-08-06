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

    public event Action<ScanProgress>? ProgressChanged;
    public event Action<string>? Log;

    private void LogMsg(string msg) => Log?.Invoke(msg);

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
        BringToFront(mainWnd.Value);
        await Task.Delay(300, ct);

        var targetWnd = mainWnd.Value;
        using var automation = CreateAutomation();
        var momentsEl = automation.FromHandle(targetWnd);

        var contacts = ImageAutomationHelper.LoadContactTemplates(config.ContactAvatarsDirectory);
        try
        {
            var seenHashes = new HashSet<string>();
            int totalScreens = 0;
            int totalMoments = 0;

            while (totalScreens < config.MaxScrollScreens)
            {
                ct.ThrowIfCancellationRequested();
                totalScreens++;
                var candidates = ExtractVisibleMoments(momentsEl, config, targetWnd);
                int screenCount = 0;

                foreach (var cand in candidates)
                {
                    var m = cand.Post;
                    if (string.IsNullOrEmpty(m.ContentHash)) continue;
                    if (!seenHashes.Add(m.ContentHash)) continue;

                    if (config.OnlyMine && !string.IsNullOrEmpty(config.MyNickname)
                        && !m.Publisher.Equals(config.MyNickname, StringComparison.Ordinal))
                        continue;

                    // 点入详情，通过头像匹配识别点赞人
                    var avatarLikers = await ScanLikersInDetailAsync(targetWnd, cand, config, contacts, ct);
                    foreach (var name in avatarLikers)
                        if (!m.Likers.Contains(name, StringComparer.Ordinal))
                            m.Likers.Add(name);

                    repo.UpsertMoment(m);
                    totalMoments++;
                    screenCount++;
                }

                ProgressChanged?.Invoke(new ScanProgress
                {
                    ScreensScanned = totalScreens,
                    TotalScreens = config.MaxScrollScreens,
                    MomentsThisScreen = screenCount,
                    MomentsTotal = totalMoments
                });
                LogMsg($"第 {totalScreens} 屏：抓到 {screenCount} 条，累计 {totalMoments} 条");

                bool advanced = await ScrollDownOneScreenAsync(targetWnd, config.ScrollWaitMs, ct);
                if (!advanced)
                {
                    LogMsg("已到达朋友圈顶部或滚动失败，结束扫描。");
                    break;
                }
            }

            LogMsg($"扫描完成：共 {totalScreens} 屏，{totalMoments} 条朋友圈入库。");
        }
        finally
        {
            foreach (var mat in contacts.Values)
            {
                try { mat.Dispose(); } catch { }
            }
            RestoreOwnWindow();
        }
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
    /// 打开朋友圈：跳过 UIA，直接图像识别 + 兜底坐标。
    /// </summary>
    private async Task<IntPtr?> OpenMomentsAsync(IntPtr mainWnd, ScanConfig config, CancellationToken ct)
    {
        // 如果已经打开了朋友圈，直接返回
        IntPtr? existing = FindMomentsWindow();
        if (existing != null) return existing;

        MinimizeOwnWindow();
        BringToFront(mainWnd);
        await Task.Delay(800, ct);

        GetWindowRect(mainWnd, out RECT mainRect);
        int winW = mainRect.Right - mainRect.Left;
        int winH = mainRect.Bottom - mainRect.Top;

        // 保存整屏截图，便于诊断
        SaveDebugScreenshot(mainWnd, "main_win");

        // 左侧栏宽度约 50~60 像素
        int sidebarWidth = Math.Min(60, winW);
        var sidebarRoi = new Rectangle(mainRect.Left, mainRect.Top, sidebarWidth, winH);

        LogMsg($"步骤1: 图像识别匹配左侧栏朋友圈图标 (ROI: {sidebarRoi})");
        if (TryClickTemplate(mainWnd, config.MomentsIconTemplatePath, sidebarRoi, config.MatchThreshold, "左侧栏朋友圈图标"))
        {
            LogMsg("步骤1: 图像识别匹配成功");
            SaveDebugScreenshot(mainWnd, "after_click_moments_icon");
            await Task.Delay(2000, ct);
            RestoreOwnWindow();
            IntPtr? mw = FindMomentsWindow();
            if (mw != null)
            {
                LogMsg("步骤1: 朋友圈窗口已打开");
                return mw;
            }
            LogMsg("步骤1: 点击了图标但未检测到朋友圈窗口");
        }
        else
        {
            LogMsg("步骤1: 图像识别未匹配到");
        }

        // 点击右上角头像
        int avatarRoiW = (int)(winW * 0.25);
        int avatarRoiH = Math.Min(160, winH);
        var avatarRoi = new Rectangle(mainRect.Right - avatarRoiW, mainRect.Top, avatarRoiW, avatarRoiH);
        LogMsg($"步骤2: 点击右上角头像 (ROI: {avatarRoi})");
        if (TryClickTemplate(mainWnd, config.MyAvatarTemplatePath, avatarRoi, config.MatchThreshold, "右上角头像"))
        {
            LogMsg("步骤2: 头像匹配成功");
            SaveDebugScreenshot(mainWnd, "after_click_avatar");
            await Task.Delay(1500, ct);

            // 在弹出的卡片中找 "朋友圈" / "Moments" 点击
            LogMsg("步骤2a: 在弹出卡片中找朋友圈入口…");
            TryClickInCard(mainWnd, winW, winH);
            await Task.Delay(1500, ct);
            RestoreOwnWindow();
            IntPtr? mw = FindMomentsWindow();
            if (mw != null)
            {
                LogMsg("步骤2: 通过头像→卡片→朋友圈路径成功打开");
                return mw;
            }
        }
        else
        {
            LogMsg("步骤2: 头像未匹配到");
        }

        // 兜底：直接坐标点击左侧栏第4个图标
        LogMsg("兜底: 坐标点击左侧栏朋友圈图标…");
        int fbX = mainRect.Left + 30;
        int fbY = mainRect.Top + 210;
        ImageAutomationHelper.ClickScreen(mainWnd, fbX, fbY);
        SaveDebugScreenshot(mainWnd, "after_fallback_click");
        await Task.Delay(2000, ct);
        RestoreOwnWindow();
        return FindMomentsWindow();
    }

    /// <summary>在弹出卡片中通过图像识别找"朋友圈"入口并点击。</summary>
    private void TryClickInCard(IntPtr mainWnd, int winW, int winH)
    {
        try
        {
            GetWindowRect(mainWnd, out RECT rc);
            // 弹窗通常在头像下方，截取窗口下半部分
            int cardY = rc.Top + 40;
            int cardH = winH - 40;
            var cardRoi = new Rectangle(rc.Left, cardY, winW, cardH);
            using var cardMat = ImageAutomationHelper.CaptureRegion(cardRoi);
            ImageAutomationHelper.SaveDebug(cardMat, Path.Combine(ImageAutomationHelper.DebugDirectory, "card_roi.png"));

            // 用 OCR 思路：直接滑动找 "朋友圈"/"Moments" 文字区域
            // 这里简化：点击卡片中央偏下位置（朋友圈通常在左侧栏弹出的卡片中）
            // 实际上微信点击头像后弹出的卡片，朋友圈入口在卡片中下部
            int clickX = rc.Left + winW / 2;
            int clickY = cardY + (int)(cardH * 0.65);
            LogMsg($"  尝试点击卡片中朋友圈入口 ({clickX}, {clickY})");
            ImageAutomationHelper.ClickScreen(mainWnd, clickX, clickY);
        }
        catch (Exception ex)
        {
            LogMsg($"  卡片点击失败: {ex.Message}");
        }
    }

    /// <summary>保存窗口当前截图用于调试。</summary>
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
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (WeChatMainClasses.Contains(sb.ToString()) && IsWindowVisible(h))
            {
                var title = new StringBuilder(256);
                GetWindowText(h, title, title.Capacity);
                if (title.ToString().Contains(MomentsTitleHint))
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    // ====== 朋友圈内容提取 ======

    private class MomentCandidate
    {
        public MomentPost Post { get; set; } = new();
        public Point ClickCenter { get; set; }
        public Rectangle Bounds { get; set; }
    }

    private List<MomentCandidate> ExtractVisibleMoments(AutomationElement momentsEl, ScanConfig config, IntPtr wnd)
    {
        var result = new List<MomentCandidate>();
        var items = new List<(Rectangle Rect, string Name)>();

        try
        {
            var all = momentsEl.FindAllDescendants();
            foreach (var el in all)
            {
                string? name;
                try { name = el.Name; } catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;

                var rect = el.BoundingRectangle;
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                items.Add((rect, name));
            }
        }
        catch (Exception ex)
        {
            LogMsg("提取可见朋友圈失败: " + ex.Message);
            return result;
        }

        if (items.Count == 0) return result;

        items.Sort((a, b) => a.Rect.Top.CompareTo(b.Rect.Top));
        const int clusterGap = 60;
        var clusters = new List<List<(Rectangle Rect, string Name)>>();
        var current = new List<(Rectangle Rect, string Name)> { items[0] };
        for (int i = 1; i < items.Count; i++)
        {
            if (items[i].Rect.Top - items[i - 1].Rect.Top > clusterGap)
            {
                clusters.Add(current);
                current = new List<(Rectangle Rect, string Name)> { items[i] };
            }
            else current.Add(items[i]);
        }
        clusters.Add(current);

        foreach (var c in clusters)
        {
            if (ParseCluster(c, out var post, out var bounds))
            {
                int cx = bounds.X + bounds.Width / 2;
                int cy = bounds.Y + bounds.Height / 2;
                result.Add(new MomentCandidate
                {
                    Post = post,
                    Bounds = bounds,
                    ClickCenter = new Point(cx, cy)
                });
            }
        }
        return result;
    }

    private static bool ParseCluster(List<(Rectangle Rect, string Name)> cluster, out MomentPost post, out Rectangle bounds)
    {
        post = new MomentPost();
        bounds = Rectangle.Empty;
        if (cluster.Count == 0) return false;

        bounds = cluster[0].Rect;
        for (int i = 1; i < cluster.Count; i++)
            bounds = Rectangle.Union(bounds, cluster[i].Rect);

        string publisher = string.Empty;
        string content = string.Empty;
        string? postTime = null;
        var likers = new List<string>();

        var timeRegex = new Regex(@"^\s*(\d+\s*(分钟|小时|天)前|昨天|前天|\d{1,2}月\d{1,2}日|\d{4}年|\d{1,2}:\d{2})");
        var likerRegex = new Regex("[，、,]");

        var sorted = cluster.OrderBy(x => x.Rect.Top).ThenBy(x => x.Rect.Left).ToList();

        foreach (var item in sorted)
        {
            var n = item.Name.Trim();
            if (string.IsNullOrEmpty(n)) continue;

            if (timeRegex.IsMatch(n) && postTime == null) { postTime = n; continue; }
            if (publisher.Length == 0 && n.Length <= 24 && !n.Contains('\n') && !likerRegex.IsMatch(n))
            { publisher = n; continue; }
            if (likerRegex.IsMatch(n) && n.Length <= 80 && !n.Contains('。'))
            {
                var parts = likerRegex.Split(n).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (parts.Count >= 1 && parts.All(p => p.Length <= 20)) { likers.AddRange(parts); continue; }
            }
            if (content.Length == 0) content = n;
            else content += "\n" + n;
        }

        if (string.IsNullOrEmpty(publisher) && string.IsNullOrEmpty(content)) return false;

        post.Publisher = publisher;
        post.Content = content.Trim();
        post.ScanTime = DateTime.Now;
        post.Likers = likers.Distinct(StringComparer.Ordinal).ToList();
        post.ContentHash = ComputeHash($"{publisher}|{content}|{postTime ?? ""}");
        if (postTime != null) post.PostTime = TryParseTime(postTime);
        return true;
    }

    // ====== 点赞识别 ======

    /// <summary>
    /// 点入单条朋友圈详情，滚动识别点赞头像，然后返回。
    /// </summary>
    private async Task<List<string>> ScanLikersInDetailAsync(
        IntPtr wnd, MomentCandidate candidate, ScanConfig config,
        Dictionary<string, Mat> contacts, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (contacts.Count == 0) return found.ToList();

        // 确保微信在前台
        BringToFront(wnd);
        await Task.Delay(200, ct);

        // 点击该条朋友圈中心
        ImageAutomationHelper.ClickScreen(wnd, candidate.ClickCenter.X, candidate.ClickCenter.Y);
        await Task.Delay(config.DetailOpenWaitMs, ct);

        int emptyStreak = 0;
        for (int i = 0; i < config.DetailMaxScrolls; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(600, ct);

            // 前台截图
            var (mat, bounds) = CaptureWindowForeground(wnd);
            using (mat)
            {
                int roiY = bounds.Y + (int)(bounds.Height * 0.45);
                int roiH = bounds.Bottom - roiY;
                var roi = new Rectangle(bounds.X, roiY, bounds.Width, roiH);
                var matches = ImageAutomationHelper.MatchContacts(mat, contacts, config.MatchThreshold, roi);

                int newCount = 0;
                foreach (var (name, _) in matches)
                    if (found.Add(name)) newCount++;

                if (newCount > 0)
                    LogMsg($"  详情内识别到 {newCount} 个新点赞人");

                if (newCount == 0) emptyStreak++;
                else emptyStreak = 0;

                if (emptyStreak >= 2) break;
            }

            // 在详情内向下滚动
            GetClientRect(wnd, out RECT cr);
            ImageAutomationHelper.ScrollClient(wnd, -WHEEL_DELTA * 4, cr.Right / 2, cr.Bottom / 2);
        }

        // 按 Esc 返回列表
        ImageAutomationHelper.PostKey(wnd, VK_ESCAPE);
        await Task.Delay(600, ct);

        return found.ToList();
    }

    /// <summary>用 CopyFromScreen 前台截取窗口区域。</summary>
    private static (Mat mat, Rectangle bounds) CaptureWindowForeground(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out RECT rc);
        int x = rc.Left, y = rc.Top, w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) w = 1; h = 1;
        var rect = new Rectangle(x, y, w, h);
        return (ImageAutomationHelper.CaptureRegion(rect), rect);
    }

    // ====== 时间解析 ======

    private static DateTime? TryParseTime(string s)
    {
        var m = Regex.Match(s, @"(\d+)\s*分钟前");
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
        SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);
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
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
