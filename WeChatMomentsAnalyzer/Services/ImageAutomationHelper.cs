using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace WeChatMomentsAnalyzer.Services;

/// <summary>
/// 基于 OpenCV 的图像识别辅助：截图、模板匹配、真实模拟点击/滚动。
/// 参考March7thAssistant：SendInput真实硬件模拟 + PrintWindow后台截图。
/// </summary>
public static class ImageAutomationHelper
{
    /// <summary>调试图保存目录。</summary>
    public static string DebugDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Debug");

    static ImageAutomationHelper()
    {
        try
        {
            Directory.CreateDirectory(DebugDirectory);
            // 设置为 Per-Monitor DPI 感知，避免坐标被缩放
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }
    }

    // ====== 截图 ======

    /// <summary>用 PrintWindow 后台截取窗口，即使被遮挡也能截到。</summary>
    public static (Mat mat, Rectangle bounds) CaptureWindow(IntPtr hWnd, bool clientOnly = false)
    {
        GetWindowRect(hWnd, out RECT rc);
        int x = rc.Left, y = rc.Top, w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

        if (clientOnly)
        {
            GetClientRect(hWnd, out RECT clientRc);
            POINT pt = new() { X = 0, Y = 0 };
            ClientToScreen(hWnd, ref pt);
            x = pt.X; y = pt.Y;
            w = clientRc.Right - clientRc.Left;
            h = clientRc.Bottom - clientRc.Top;
        }

        if (w <= 0 || h <= 0)
            w = 1; h = 1;

        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        try
        {
            // PW_RENDERFULLCONTENT = 2, PW_CLIENTONLY = 1
            PrintWindow(hWnd, hdc, 2);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        return (BitmapConverter.ToMat(bmp), new Rectangle(x, y, w, h));
    }

    /// <summary>截取整个屏幕（前台截图）。</summary>
    public static Mat CaptureScreen()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(screen.X, screen.Y, 0, 0, bmp.Size);
        return BitmapConverter.ToMat(bmp);
    }

    /// <summary>截取屏幕上的指定区域（前台截图，窗口需在前台）。</summary>
    public static Mat CaptureRegion(Rectangle rect)
    {
        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, bmp.Size);
        return BitmapConverter.ToMat(bmp);
    }

    // ====== 模板匹配 ======

    public static Mat? LoadTemplate(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Cv2.ImRead(path, ImreadModes.Color); }
        catch { return null; }
    }

    /// <summary>在 source 中查找 template 的最佳匹配位置（模板中心），低于 threshold 返回 null。支持多尺度缩放。</summary>
    public static Point? FindTemplate(Mat source, Mat template, double threshold = 0.75)
    {
        if (source.Empty() || template.Empty()) return null;

        double bestScore = 0;
        OpenCvSharp.Point bestLoc = default;
        double bestScale = 1;

        // 多尺度匹配：尝试多种缩放比例，适配用户截图尺寸不一的情况
        for (double scale = 0.2; scale <= 1.6; scale += 0.15)
        {
            int tw = (int)(template.Width * scale);
            int th = (int)(template.Height * scale);
            if (tw < 8 || th < 8) continue;
            if (tw > source.Width || th > source.Height) continue;

            Mat scaled;
            if (Math.Abs(scale - 1.0) < 0.01)
                scaled = template;
            else
                scaled = new Mat();

            try
            {
                if (scaled != template)
                    Cv2.Resize(template, scaled, new OpenCvSharp.Size(tw, th));

                using var result = new Mat(source.Rows - scaled.Rows + 1, source.Cols - scaled.Cols + 1, MatType.CV_32FC1);
                Cv2.MatchTemplate(source, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                if (maxVal > bestScore)
                {
                    bestScore = maxVal;
                    bestLoc = maxLoc;
                    bestScale = scale;
                }
            }
            finally
            {
                if (scaled != template) scaled.Dispose();
            }
        }

        if (bestScore >= threshold)
        {
            int finalW = (int)(template.Width * bestScale);
            int finalH = (int)(template.Height * bestScale);
            return new Point(bestLoc.X + finalW / 2, bestLoc.Y + finalH / 2);
        }
        return null;
    }

    /// <summary>在 source 中查找所有超过 threshold 的模板位置（去重）。支持多尺度缩放。</summary>
    public static List<Point> FindAllTemplates(Mat source, Mat template, double threshold = 0.75)
    {
        var points = new List<Point>();
        if (source.Empty() || template.Empty()) return points;

        double bestScore = 0;
        OpenCvSharp.Point bestLoc = default;
        double bestScale = 1;

        for (double scale = 0.2; scale <= 1.6; scale += 0.15)
        {
            int tw = (int)(template.Width * scale);
            int th = (int)(template.Height * scale);
            if (tw < 8 || th < 8) continue;
            if (tw > source.Width || th > source.Height) continue;

            Mat scaled = Math.Abs(scale - 1.0) < 0.01 ? template : new Mat();
            try
            {
                if (scaled != template) Cv2.Resize(template, scaled, new OpenCvSharp.Size(tw, th));

                using var result = new Mat(source.Rows - scaled.Rows + 1, source.Cols - scaled.Cols + 1, MatType.CV_32FC1);
                Cv2.MatchTemplate(source, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
                if (maxVal > bestScore)
                {
                    bestScore = maxVal;
                    bestLoc = maxLoc;
                    bestScale = scale;
                }

                if (maxVal >= threshold)
                {
                    int fw = (int)(template.Width * scale);
                    int fh = (int)(template.Height * scale);
                    var c = new Point(maxLoc.X + fw / 2, maxLoc.Y + fh / 2);
                    int minDist = Math.Max(fw, fh) / 2;
                    if (!points.Any(p => Math.Abs(p.X - c.X) < minDist && Math.Abs(p.Y - c.Y) < minDist))
                        points.Add(c);
                }
            }
            finally
            {
                if (scaled != template) scaled.Dispose();
            }
        }

        // 如果多尺度阈值匹配未命中，但最佳分数仍高于阈值，返回最佳位置兜底
        if (points.Count == 0 && bestScore >= threshold)
        {
            int fw = (int)(template.Width * bestScale);
            int fh = (int)(template.Height * bestScale);
            points.Add(new Point(bestLoc.X + fw / 2, bestLoc.Y + fh / 2));
        }

        return points;
    }

    /// <summary>加载联系人头像库，文件名（不含扩展名）即昵称。</summary>
    public static Dictionary<string, Mat> LoadContactTemplates(string directory)
    {
        var dict = new Dictionary<string, Mat>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return dict;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };
        foreach (var file in Directory.EnumerateFiles(directory).Where(f => exts.Contains(Path.GetExtension(f))))
        {
            try
            {
                var mat = Cv2.ImRead(file, ImreadModes.Color);
                if (!mat.Empty()) dict[Path.GetFileNameWithoutExtension(file)] = mat;
            }
            catch { }
        }
        return dict;
    }

    /// <summary>在截图中匹配联系人头像，返回匹配到的名称及位置。可通过 roi 限定区域。</summary>
    public static List<(string Name, Point Location)> MatchContacts(
        Mat screenshot, Dictionary<string, Mat> contacts, double threshold = 0.75, Rectangle? roi = null)
    {
        var matches = new List<(string, Point)>();
        if (contacts.Count == 0) return matches;

        Mat source = screenshot;
        int offX = 0, offY = 0;
        Mat? roiMat = null;

        if (roi.HasValue)
        {
            var r = roi.Value;
            r.X = Math.Max(0, r.X);
            r.Y = Math.Max(0, r.Y);
            r.Width = Math.Min(screenshot.Width - r.X, r.Width);
            r.Height = Math.Min(screenshot.Height - r.Y, r.Height);
            if (r.Width <= 0 || r.Height <= 0) return matches;
            roiMat = new Mat(screenshot, new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height));
            source = roiMat;
            offX = r.X; offY = r.Y;
        }

        foreach (var kv in contacts)
        {
            var pt = FindTemplate(source, kv.Value, threshold);
            if (pt.HasValue)
                matches.Add((kv.Key, new Point(pt.Value.X + offX, pt.Value.Y + offY)));
        }
        roiMat?.Dispose();
        return matches;
    }

    public static void SaveDebug(Mat mat, string path) { try { mat.SaveImage(path); } catch { } }

    /// <summary>
    /// 从详情页点赞/评论区截图中提取近方形的头像块。
    /// 思路：以四角均值作为背景色，与背景差异明显的连通域视为前景，
    /// 过滤出尺寸/宽高比接近正方形的块即为头像。
    /// </summary>
    public static List<Mat> ExtractSquareAvatars(Mat block, int minSize = 30, int maxSize = 96)
        => ExtractSquareAvatarsWithBounds(block, minSize, maxSize).Select(x => x.Image).ToList();

    /// <summary>
    /// 提取近方形头像块，同时返回每个头像的边界（相对 block 左上角）。
    /// 边界用于与 OCR 文字行做位置关联、以及限定联系人匹配的搜索区域。
    /// </summary>
    public static List<(Mat Image, Rect Bounds)> ExtractSquareAvatarsWithBounds(Mat block, int minSize = 30, int maxSize = 96)
    {
        var avatars = new List<(Mat, Rect)>();
        if (block.Empty() || block.Width < minSize || block.Height < minSize) return avatars;

        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(block, gray, ColorConversionCodes.BGR2GRAY);

            // 背景色取灰度直方图中位数（面板纯色背景占画面多数）。
            // 四角均值不可靠：截图左缘可能混入窗口其他内容、右侧是窗口底色，会把均值拉偏。
            using var hist = new Mat();
            Cv2.CalcHist(new[] { gray }, new[] { 0 }, null, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
            long total = (long)gray.Rows * gray.Cols;
            long acc = 0;
            double bg = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += (long)hist.At<float>(i);
                if (acc * 2 >= total) { bg = i; break; }
            }

            using var diff = new Mat();
            Cv2.Absdiff(gray, new Scalar(bg), diff);
            using var mask = new Mat();
            Cv2.Threshold(diff, mask, 25, 255, ThresholdTypes.Binary);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            // 闭运算填补头像内部与背景相近的暗洞（黑白照片否则会变空心被面积过滤掉）
            using var kernelClose = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(7, 7));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernelClose);

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);

            var boxes = new List<Rect>();
            for (int i = 1; i < n; i++)
            {
                int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);

                if (w < minSize || h < minSize || w > maxSize || h > maxSize) continue;
                double ratio = (double)h / w;
                if (ratio < 0.65 || ratio > 1.55) continue;
                if (area < w * h * 0.40) continue; // 头像照片是实心的，过滤细线条图标
                boxes.Add(new Rect(x, y, w, h));
            }

            // 去掉互相包含的重复块，按从上到下、从左到右排序
            boxes = boxes
                .Where(b => !boxes.Any(o => o != b && o.Contains(b)))
                .OrderBy(b => b.Y).ThenBy(b => b.X)
                .ToList();

            foreach (var b in boxes)
            {
                var r = new Rect(
                    Math.Max(0, b.X - 2), Math.Max(0, b.Y - 2),
                    Math.Min(block.Width - Math.Max(0, b.X - 2), b.Width + 4),
                    Math.Min(block.Height - Math.Max(0, b.Y - 2), b.Height + 4));
                if (r.Width < minSize || r.Height < minSize) continue;
                avatars.Add((new Mat(block, r).Clone(), r));
            }
        }
        catch { }
        return avatars;
    }

    // ====== 真实模拟鼠标/键盘（SendInput） ======

    /// <summary>在屏幕坐标处执行真实鼠标左键点击（SendInput）。</summary>
    public static void ClickScreen(IntPtr hWnd, int screenX, int screenY)
    {
        MoveAndClick(screenX, screenY);
    }

    /// <summary>在窗口客户区坐标处点击（自动转换为屏幕坐标后 SendInput）。</summary>
    public static void ClickClient(IntPtr hWnd, int clientX, int clientY)
    {
        var pt = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hWnd, ref pt);
        MoveAndClick(pt.X, pt.Y);
    }

    /// <summary>真实鼠标滚轮滚动（SendInput）。</summary>
    public static void ScrollClient(IntPtr hWnd, int delta, int clientX, int clientY)
    {
        var pt = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hWnd, ref pt);
        // 先移动到目标位置再滚轮
        SetCursorPos(pt.X, pt.Y);
        System.Threading.Thread.Sleep(30);
        SendMouseWheel(delta);
    }

    /// <summary>在屏幕坐标处执行真实鼠标滚轮滚动（用于定向滚动指定列表区域）。</summary>
    public static void ScrollScreen(IntPtr hWnd, int delta, int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        System.Threading.Thread.Sleep(30);
        SendMouseWheel(delta);
    }

    /// <summary>在窗口中心位置滚轮滚动。</summary>
    public static void ScrollWindowCenter(IntPtr hWnd, int delta)
    {
        GetClientRect(hWnd, out RECT rc);
        var pt = new POINT { X = rc.Right / 2, Y = rc.Bottom / 2 };
        ClientToScreen(hWnd, ref pt);
        SetCursorPos(pt.X, pt.Y);
        System.Threading.Thread.Sleep(30);
        SendMouseWheel(delta);
    }

    /// <summary>发送键盘按键（SendInput）。</summary>
    public static void PostKey(IntPtr hWnd, byte vk)
    {
        SendKeyPress(vk);
    }

    private static void MoveAndClick(int x, int y)
    {
        LogDebug($"MoveAndClick: ({x}, {y})");

        // 保存当前鼠标位置，点击后恢复，避免长时间占用用户鼠标
        var original = new POINT();
        GetCursorPos(ref original);
        try
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(80);
            SendMouseDown();
            System.Threading.Thread.Sleep(60);
            SendMouseUp();
            System.Threading.Thread.Sleep(80);
        }
        finally
        {
            SetCursorPos(original.X, original.Y);
        }
    }

    private static void LogDebug(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(DebugDirectory, "debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private static void SendMouseDown()
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN }
            }
        };
        SendInput(1, new[] { inp }, INPUT_SIZE);
    }

    private static void SendMouseUp()
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP }
            }
        };
        SendInput(1, new[] { inp }, INPUT_SIZE);
    }

    private static void SendMouseWheel(int delta)
    {
        var inp = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = (uint)delta }
            }
        };
        SendInput(1, new[] { inp }, INPUT_SIZE);
    }

    private static void SendKeyPress(byte vk)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0 } }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0 } }
        };
        SendInput(2, inputs, INPUT_SIZE);
    }

    // ====== Win32 ======

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int WHEEL_DELTA = 120;
    private static readonly int INPUT_SIZE = Marshal.SizeOf<INPUT>();

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }
}
