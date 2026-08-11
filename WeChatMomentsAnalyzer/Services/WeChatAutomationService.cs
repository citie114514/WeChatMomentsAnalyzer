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
        
        // 全量重建：旧版本曾把评论/OCR 文本/位置文本计入点赞，历史库中的污染数据
        // 无法靠按条替换式更新自愈，故每次扫描开始先清空，由当前干净逻辑完整重建
        repo.ClearAll();
        LogMsg("已清空旧的朋友圈/点赞数据，本次扫描将完整重建。");

        // 联系人头像库预归一化一次，供整次扫描的每条朋友圈点赞头像复用匹配。
        // 匹配器内部按圆形掩膜 NCC 比较，规避方形裁剪四角背景色差异导致的漏匹配。
        using var matcher = new AvatarMatcher(config.ContactAvatarsDirectory, config.ContactMatchThreshold);
        if (matcher.ContactCount == 0)
            LogMsg("警告：联系人头像库为空，点赞人将无法识别。请先在扫描页执行「扫描联系人」。");
        else
            LogMsg($"已加载联系人头像库 {matcher.ContactCount} 个，匹配阈值 {config.ContactMatchThreshold:F2}。");
        
        try
        {
            // 相册页可能停留在上次会话的中间/底部：先滚回顶部再开始扫描
            if (!IsDetailView(momentsEl) && !IsFeedView(momentsEl))
            {
                LogMsg("相册滚回顶部…");
                BringToFront(targetWnd);
                GetClientRect(targetWnd, out RECT topRc);
                // 单次大滚轮会被微信限幅，分多次小步滚到顶；落点取内容列（避开置顶区与空白间隔等非滚动区）
                for (int i = 0; i < 10; i++)
                {
                    ImageAutomationHelper.ScrollClientBursts(targetWnd, WHEEL_DELTA * 2, 3, 100,
                        (int)(topRc.Right * 0.35), (int)(topRc.Bottom * 0.72));
                    await Task.Delay(200, ct);
                }
                await Task.Delay(800, ct);
            }

            var seenHashes = new HashSet<string>(StringComparer.Ordinal);
            int totalScreens = 0;
            int totalMoments = 0;
            int totalAvatars = 0;
            int emptyStreak = 0;
            int scrollLevel = 0;
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

                    var saved = await ScanOneMomentAsync(targetWnd, momentsEl, item, config, repo, matcher, ct);
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
                    if (scrollLevel < 2)
                    {
                        scrollLevel++;
                        LogMsg($"相册滚动无效（内容未变），切换滚动策略 {scrollLevel + 1}");
                    }
                    else
                    {
                        LogMsg("滚动后内容未变化，已到达底部，结束扫描。");
                        break;
                    }
                }
                else scrollLevel = 0;
                prevScreenFingerprint = fingerprint;

                await ScrollAlbumAsync(targetWnd, momentsEl, scrollLevel, config.ScrollWaitMs, ct);
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

            // 全量重建头像库：旧版裁剪偏移错误曾产出整库纯色脏模板，残留文件会导致伪匹配，
            // 与朋友圈数据的 ClearAll 语义保持一致
            foreach (var f in Directory.GetFiles(config.ContactAvatarsDirectory, "*.png"))
            {
                try { File.Delete(f); } catch { }
            }
            LogMsg("已清空旧联系人头像库，本次扫描将完整重建。");

            using var automation = CreateAutomation();
            var root = automation.FromHandle(mainWnd.Value);
            GetWindowRect(mainWnd.Value, out RECT wrc);
            var wndRect = new Rectangle(wrc.Left, wrc.Top, wrc.Right - wrc.Left, wrc.Bottom - wrc.Top);
            // UIA 坐标与 CopyFromScreen 同为物理像素，但微信布局按逻辑像素设计，头像偏移需按 DPI 缩放换算
            double dpi = GetDpiForWindow(mainWnd.Value) / 96.0;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            string prevFp = string.Empty;
            int stall = 0;

            for (int screen = 0; screen < 120; screen++)
            {
                ct.ThrowIfCancellationRequested();
                var items = GetContactItems(root, mainWnd.Value);
                string fp = string.Join("|", items.Select(i => i.Name));

                int newCount = 0;
                foreach (var item in items)
                {
                    // 底部截断条目头像不完整，留给滚动后的下一屏（不记入 seen）
                    if (item.Rect.Bottom > wndRect.Bottom - 4) continue;
                    if (!seen.Add(item.Name)) continue;

                    // 头像在条目内部左侧但不贴左缘（条目含列表左内边距）：实测左偏≈36 逻辑像素、头像≈30 逻辑像素、垂直居中。
                    // 旧版 Left+6 裁到的是头像左侧的纯色背景，导致整库模板失效。
                    // 截取条目左侧条带，在多个候选偏移中取标准差最大者（纯色背景 stddev 近 0），防御布局微调。
                    int avSize = (int)Math.Round(30 * dpi);
                    int stripW = (int)Math.Round(80 * dpi);
                    using var strip = ImageAutomationHelper.CaptureRegion(
                        new Rectangle(item.Rect.Left, item.Rect.Top, stripW, item.Rect.Height));
                    int bestX = -1;
                    double bestSd = -1;
                    foreach (int offLogical in new[] { 36, 28, 44, 20 })
                    {
                        int cx = (int)Math.Round(offLogical * dpi);
                        if (cx + avSize > strip.Width) continue;
                        using var cand = new Mat(strip, new OpenCvSharp.Rect(cx, Math.Max(0, (strip.Height - avSize) / 2), avSize, avSize));
                        double sd = ImageAutomationHelper.StdDev(cand);
                        if (sd > bestSd) { bestSd = sd; bestX = cx; }
                    }
                    if (bestX < 0) bestX = (int)Math.Round(36 * dpi);
                    using var av = new Mat(strip, new OpenCvSharp.Rect(bestX, Math.Max(0, (strip.Height - avSize) / 2), avSize, avSize)).Clone();
                    string path = Path.Combine(config.ContactAvatarsDirectory, SanitizeFileName(item.Name) + ".png");
                    ImageAutomationHelper.SaveDebug(av, path);
                    count++;
                    newCount++;
                }

                LogMsg($"联系人第 {screen + 1} 屏：新增 {newCount} 个，累计 {count} 个");

                // 到底检测：内容未变且无新增计一次停滞；停滞期间按级升级滚动策略，
                // 连续 4 次停滞即认为已到底部自动结束（不再固定滚满上限屏数）
                if (fp == prevFp && newCount == 0) stall++;
                else stall = 0;
                if (stall >= 4) { LogMsg("通讯录已滚动到底，结束。"); break; }
                prevFp = fp;

                await ScrollContactsAsync(mainWnd.Value, root, wndRect, items, Math.Min(stall, 2), ct);
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
    /// 定向滚动联系人列表。默认用旧版验证有效的单次中等滚轮（拆成小步反而使微信平滑滚动动画反复重启、几乎不动）；
    /// 无效时按策略升级：①单次滚轮 ②UIA ScrollPattern ③更大滚轮翻页。滚轮取列表水平中心，避免落在右侧详情面板。
    /// </summary>
    private async Task ScrollContactsAsync(IntPtr wnd, AutomationElement root, Rectangle wndRect,
        List<ContactItemInfo> items, int level, CancellationToken ct)
    {
        int listLeft = items.Count > 0 ? items.Min(i => i.Rect.Left) : wndRect.Left + 90;
        int listRight = items.Count > 0 ? items.Max(i => i.Rect.Right) : wndRect.Left + 450;
        int cx = (listLeft + listRight) / 2;
        int sy = wndRect.Top + wndRect.Height / 2;

        BringToFront(wnd);
        if (level == 0)
        {
            ImageAutomationHelper.ScrollScreen(wnd, -WHEEL_DELTA * 3, cx, sy);
        }
        else if (level == 1)
        {
            if (!TryScrollPattern(root, new Point(cx, sy)))
                ImageAutomationHelper.ScrollScreen(wnd, -WHEEL_DELTA * 5, cx, sy);
        }
        else
        {
            // 点击滚动条轨道在底部时会落在拇指上方触发“按页上翻”，造成底部来回震荡且鼠标跳到滚动条处；
            // 改用更大滚轮翻页，鼠标保持在列表中心
            ImageAutomationHelper.ScrollScreen(wnd, -WHEEL_DELTA * 6, cx, sy);
        }
        await Task.Delay(700, ct);
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
                    if (r.Top < wndRect.Top + 40 || r.Top > wndRect.Bottom - 30) continue;
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
    // 地理位置文本（如"福州市·长乐下沙沙滩"）：纯图片朋友圈会把位置显示为文本，
    // 不能把它当作动态正文或独立条目
    // 注意：.NET 正则不支持 \p{IsHan} 脚本名（类型初始化时抛 RegexParseException），
    // 汉字用 CJK 统一表意文字范围 \u4e00-\u9fff 代替
    private static readonly Regex LocationOnlyRegex = new(@"^[\u4e00-\u9fffA-Za-z0-9_\-]{1,12}·[\u4e00-\u9fffA-Za-z0-9_\-·]{1,24}$");

    /// <summary>读取朋友圈列表页中完整可见的 ListItem（每条朋友圈一个，名称含日期和内容摘要）。</summary>
    private List<ListItemInfo> GetListPageItems(AutomationElement root, IntPtr wnd)
    {
        var candidates = new List<ListItemInfo>();
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

                    // 只保留顶部完整可见的条目（工具栏高约 72px）；底部允许部分截断（点击坐标会夹紧到窗口内）
                    if (r.Top < wndRect.Top + 72 || r.Top > wndRect.Bottom - 80) continue;

                    string name;
                    try { name = el.Name?.Trim() ?? string.Empty; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;
                    // 窄元素是日期标签/徽章，不是条目；“置顶”条目跳过
                    if (r.Width < 200) continue;
                    if (name.StartsWith("置顶", StringComparison.Ordinal)) continue;
                    if (name.Length < 2) continue;
                    if (name == "朋友圈") continue;
                    if (!seen.Add(name)) continue; // UIA 偶尔重复暴露同一 ListItem

                    candidates.Add(new ListItemInfo(name, r));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogMsg("读取朋友圈列表失败: " + ex.Message);
        }

        // 个性签名/昵称等头部噪声位于首条带日期条目的上方：
        // 按垂直顺序遍历，见过带日期前缀的条目后才接受无前缀条目——
        // 既排除头部签名（曾误点签名导致导航离开相册页），又覆盖同日多条的非首条（无日期前缀）
        var result = new List<ListItemInfo>();
        bool seenDate = false;
        foreach (var item in candidates.OrderBy(i => i.Rect.Top))
        {
            if (AlbumItemDatePrefixRegex.IsMatch(item.Name))
            {
                seenDate = true;
                result.Add(item);
            }
            else if (seenDate)
            {
                // 位置信息元素（无日期前缀、形如"城市·地点"）不是动态条目，跳过避免误记为一条动态
                if (LocationOnlyRegex.IsMatch(item.Name)) continue;
                result.Add(item);
            }
        }
        return result;
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
        ScanConfig config, MomentsRepository repo, AvatarMatcher matcher, CancellationToken ct)
    {
        try
        {
            // 1) 点入详情。相册条目布局：左侧日期、中间图片九宫格、右侧文字。
            //    优先点击图片区进入详情；无图条目该位置落在文字上同样可进入。
            int imgX = item.Rect.Left + 245;
            int imgY = item.Rect.Top + Math.Max(60, item.Rect.Height / 2);
            // 底部截断条目：点击坐标夹紧到窗口内，避免点到窗口外
            GetWindowRect(wnd, out RECT irc);
            imgY = Math.Min(imgY, irc.Bottom - 24);
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
            var (avatarCount, likerNames) = await RecordDetailAvatarsAsync(wnd, root, post, config, matcher, ct);
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

        // 纯图片朋友圈的正文位置会显示地理位置文本，不能记为动态内容
        if (LocationOnlyRegex.IsMatch(post.Content)) post.Content = "[图片]";

        if (timeText != null) post.PostTime = TryParseTime(timeText);
        post.ContentHash = ComputeHash($"{post.Publisher}|{post.Content}|{timeText ?? ""}");
        return post;
    }

    /// <summary>
    /// 在详情页截取帖子（日期行）下方的点赞/评论区，检测方形头像并保存，
    /// 并通过联系人头像库模板匹配得到点赞人昵称列表。
    /// 长文/九宫格帖子会把点赞区挤出视口：先下滚直到帖子主体底部进入视口下部，
    /// 再向下分段截取合并（头像按像素相似去重、昵称取并集），确保点赞列表完整覆盖。
    /// OCR 文字行落盘供诊断，并用于排除评论者头像（不直接计入点赞人）。
    /// </summary>
    private async Task<(int avatarCount, List<string> names)> RecordDetailAvatarsAsync(
        IntPtr wnd, AutomationElement root, MomentPost post, ScanConfig config, AvatarMatcher matcher, CancellationToken ct)
    {
        var empty = (0, new List<string>());
        try
        {
            GetWindowRect(wnd, out RECT rc);
            var wndRect = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);

            // 详情页有进入动画、点赞区渲染滞后：等待 + 必要时下滚，直到帖子主体底部进入视口下部
            int postBottom = -1, blockBottom = -1;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                (postBottom, blockBottom) = MeasureDetailBlocks(root);
                if (postBottom > 0 && postBottom <= wndRect.Bottom - 120) break;
                if (postBottom > wndRect.Bottom - 120 && attempt >= 1)
                    ImageAutomationHelper.ScrollClientBursts(wnd, -WHEEL_DELTA, 3, 120,
                        (int)(wndRect.Width * 0.35), (int)(wndRect.Height * 0.7));
                await Task.Delay(700, ct);
            }
            if (postBottom <= 0) return empty;

            string dir = Path.Combine(config.MomentsAvatarsDirectory, post.ContentHash);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("HHmmssfff");

            var allNames = new HashSet<string>(StringComparer.Ordinal);
            var allAvatars = new List<Mat>();
                // 向下分段截取：每段截完后若点赞/评论块尚未完整入视口，继续下滚补截
                for (int seg = 0; seg < 3; seg++)
                {
                    (postBottom, blockBottom) = MeasureDetailBlocks(root);
                    if (postBottom <= 0) break;
                    if (blockBottom <= postBottom) blockBottom = wndRect.Bottom - 10;

                    int roiTop = Math.Max(postBottom + 4, wndRect.Top + 72);
                    int roiBottom = Math.Min(Math.Min(blockBottom, wndRect.Bottom - 10), roiTop + 600);
                    if (roiBottom - roiTop >= 20)
                    {
                        using var block = ImageAutomationHelper.CaptureRegion(
                            new Rectangle(wndRect.X + 1, roiTop, wndRect.Width - 2, roiBottom - roiTop));
                        ImageAutomationHelper.SaveDebug(block, Path.Combine(dir, $"detail_below_{stamp}_{seg}.png"));

                        // OCR 文字行落盘供诊断，同时用于识别评论者头像（见 IsCommentAvatar）
                        var ocrLines = new List<OcrLine>();
                        if (_ocr.IsAvailable)
                        {
                            try
                            {
                                ocrLines = await _ocr.RecognizeAsync(block);
                                File.WriteAllText(Path.Combine(dir, $"detail_ocr_{stamp}_{seg}.txt"),
                                    string.Join("\n", ocrLines.Select(l => l.Text)));
                            }
                            catch { }
                        }

                        // 提取本段头像：逐个与联系人库匹配得到点赞人昵称，并按像素相似去重（相邻分段可能重叠）
                        var segAvatars = ImageAutomationHelper.ExtractSquareAvatarsWithBounds(block);
                        try
                        {
                            foreach (var (img, bounds) in segAvatars)
                            {
                                // 评论行（"昵称：内容"）左侧的头像是评论者而非点赞人，跳过匹配
                                if (!IsCommentAvatar(bounds, ocrLines) && matcher.ContactCount > 0)
                                {
                                    var m = matcher.Match(img);
                                    if (m.Name != null)
                                        allNames.Add(m.Name);
                                    else if (m.Score > 0.35)
                                        LogMsg($"  头像未匹配（最佳候选 {m.BestCandidate} 分数 {m.Score:F2}）");
                                }
                                if (!allAvatars.Any(ex => SameAvatar(ex, img)))
                                    allAvatars.Add(img.Clone());
                            }
                        }
                        finally
                        {
                            foreach (var (img, _) in segAvatars) img.Dispose();
                        }
                    }

                    // 点赞/评论块已完整可见 → 截取完成
                    if (blockBottom <= wndRect.Bottom - 10) break;

                    ImageAutomationHelper.ScrollClientBursts(wnd, -WHEEL_DELTA, 3, 120,
                        (int)(wndRect.Width * 0.35), (int)(wndRect.Height * 0.7));
                    await Task.Delay(700, ct);
                }

            for (int i = 0; i < allAvatars.Count; i++)
                ImageAutomationHelper.SaveDebug(allAvatars[i], Path.Combine(dir, $"avatar_{stamp}_{i:00}.png"));
            if (allAvatars.Count > 0 || allNames.Count > 0)
                LogMsg($"  日期下方 {allAvatars.Count} 头像，匹配点赞人 {allNames.Count} 个 → {dir}");
            var nameList = allNames.ToList();
            foreach (var m in allAvatars) m.Dispose();
            return (allAvatars.Count, nameList);
        }
        catch (Exception ex)
        {
            LogMsg("记录详情头像失败: " + ex.Message);
            return empty;
        }
    }

    /// <summary>测量详情页：帖子主体 ListItem（带名称）底边 与 点赞/评论块（无名称）底边。</summary>
    private static (int postBottom, int blockBottom) MeasureDetailBlocks(AutomationElement root)
    {
        int postBottom = -1, blockBottom = -1;
        foreach (var el in root.FindAllDescendants())
        {
            try
            {
                if (el.ControlType != FlaUI.Core.Definitions.ControlType.ListItem) continue;
                var r = el.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) continue;
                var name = el.Name?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(name)) postBottom = Math.Max(postBottom, r.Bottom);
                else blockBottom = Math.Max(blockBottom, r.Bottom);
            }
            catch { }
        }
        return (postBottom, blockBottom);
    }

    /// <summary>
    /// 判断头像是否属于评论者：评论区布局为“头像左 + 昵称上/内容下 + 日期右对齐”，
    /// 即评论头像右侧紧邻昵称文本行（昵称行无冒号，旧版依赖“：”的判定实测全部漏判）；
    /// 点赞行头像右侧同一水平带没有文本。用于避免把评论人计入点赞人
    /// （历史上“表哥：新年快乐”中的“表哥”曾被误计为点赞人）。
    /// </summary>
    private static bool IsCommentAvatar(Rect avatar, List<OcrLine> lines)
    {
        foreach (var l in lines)
        {
            var t = l.Text;
            if (string.IsNullOrEmpty(t)) continue;
            var lr = l.Bounds;
            if (lr.Width <= 0 || lr.Height <= 0) continue;
            int avCy = avatar.Y + avatar.Height / 2;
            int lCy = lr.Y + lr.Height / 2;
            // 文本行需与头像垂直重叠（昵称行位于头像上半区）
            if (Math.Abs(avCy - lCy) > (avatar.Height + lr.Height) / 2 + 6) continue;
            // 昵称文本起点紧跟头像右缘；右对齐的日期行距离过远不会落入该窗口
            if (lr.X >= avatar.X + avatar.Width - 8 && lr.X <= avatar.X + avatar.Width + 90) return true;
        }
        return false;
    }

    /// <summary>判断两个头像是否相同（缩放 16x16 比较平均绝对差），用于跨分段重叠区去重。</summary>
    private static bool SameAvatar(Mat a, Mat b)
    {
        try
        {
            using var ra = new Mat();
            using var rb = new Mat();
            Cv2.Resize(a, ra, new OpenCvSharp.Size(16, 16));
            Cv2.Resize(b, rb, new OpenCvSharp.Size(16, 16));
            using var diff = new Mat();
            Cv2.Absdiff(ra, rb, diff);
            return Cv2.Mean(diff).Val0 < 18;
        }
        catch { return false; }
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

    /// <summary>
    /// 相册页下滚。窗口中心常落在置顶区与内容之间等不可滚动空白区，故落点取内容列；
    /// 无效时按策略升级：①左内容列 ②右内容列 ③UIA ScrollPattern。滚完把鼠标移回标题栏清除悬停态。
    /// </summary>
    private async Task ScrollAlbumAsync(IntPtr wnd, AutomationElement root, int level, int waitMs, CancellationToken ct)
    {
        BringToFront(wnd);
        GetClientRect(wnd, out RECT rc);
        int y = (int)(rc.Bottom * 0.72);

        if (level == 0)
            ImageAutomationHelper.ScrollClientBursts(wnd, -WHEEL_DELTA, 3, 120, (int)(rc.Right * 0.35), y);
        else if (level == 1)
            ImageAutomationHelper.ScrollClientBursts(wnd, -WHEEL_DELTA, 6, 120, (int)(rc.Right * 0.70), y);
        else
        {
            GetWindowRect(wnd, out RECT wr);
            var pt = new Point(wr.Left + rc.Right / 2, wr.Top + (int)(rc.Bottom * 0.6));
            if (!TryScrollPattern(root, pt))
                ImageAutomationHelper.ScrollClientBursts(wnd, -WHEEL_DELTA, 10, 100, rc.Right / 2, y);
        }
        await Task.Delay(waitMs, ct);

        GetWindowRect(wnd, out RECT wrc);
        ImageAutomationHelper.MoveCursor(wrc.Left + (wrc.Right - wrc.Left) / 2, wrc.Top + 12);
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
    private static extern uint GetDpiForWindow(IntPtr hWnd);

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
