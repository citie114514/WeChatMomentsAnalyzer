using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinOcrLine = Windows.Media.Ocr.OcrLine;

namespace WeChatMomentsAnalyzer.Services;

/// <summary>OCR 识别出的一行文字及其边界（相对原图左上角）。</summary>
public sealed record OcrLine(string Text, Rect Bounds);

/// <summary>
/// 基于 Windows.Media.Ocr（WinRT 原生）的 OCR 服务。
/// 对朋友圈详情截图做中文识别，解析点赞/评论区的昵称。
/// 零额外原生依赖：net8.0-windows10 目标框架已内置 WinRT 投影。
/// </summary>
public sealed class OcrService
{
    private const double Scale = 2.5;

    private readonly OcrEngine? _engine;

    public OcrService()
    {
        // 优先简体中文，降级到用户系统语言
        _engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool IsAvailable => _engine != null;
    public string LanguageTag => _engine?.RecognizerLanguage?.LanguageTag ?? "(不可用)";

    /// <summary>对截图做 OCR，返回每行文字及其边界（相对原图左上角）。</summary>
    public async Task<List<OcrLine>> RecognizeAsync(Mat image)
    {
        var result = new List<OcrLine>();
        if (_engine == null || image == null || image.Empty()) return result;

        try
        {
            // 预处理：放大 2.5x（小字号中文）+ 灰度 + CLAHE 对比度增强
            using var scaled = new Mat();
            Cv2.Resize(image, scaled,
                new OpenCvSharp.Size((int)(image.Width * Scale), (int)(image.Height * Scale)),
                0, 0, InterpolationFlags.Cubic);

            using var gray = new Mat();
            Cv2.CvtColor(scaled, gray, ColorConversionCodes.BGR2GRAY);
            using var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(gray, enhanced);
            // 转回 3 通道，便于位图解码
            using var bgr = new Mat();
            Cv2.CvtColor(enhanced, bgr, ColorConversionCodes.GRAY2BGR);

            // Mat → SoftwareBitmap（经 Bitmap + 内存流）
            using var bmp = BitmapConverter.ToBitmap(bgr);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;

            var dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            using var sb = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var ocrResult = await _engine.RecognizeAsync(sb);
            if (ocrResult == null) return result;

            foreach (var line in ocrResult.Lines)
                result.Add(new OcrLine(line.Text ?? string.Empty, ComputeLineBounds(line)));
        }
        catch
        {
            // OCR 失败不影响扫描主流程
        }
        return result;
    }

    /// <summary>从 OCR 行中解析点赞/评论昵称列表（去重）。</summary>
    public static List<string> ExtractNames(List<OcrLine> lines)
    {
        var names = new List<string>();
        if (lines == null) return names;

        foreach (var ln in lines)
        {
            var t = (ln.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(t)) continue;

            if (t.Contains('：') || t.Contains(':'))
            {
                // 评论行 "昵称：内容"，取冒号前为昵称
                var idx = t.IndexOfAny(new[] { '：', ':' });
                var n = t[..idx].Trim();
                if (IsValidName(n)) names.Add(n);
            }
            else
            {
                // 点赞/昵称串：去掉 "赞了""等N人""觉得很赞" 等后缀噪声，按分隔符拆分
                var cleaned = NoiseTailRegex.Replace(t, "");
                foreach (var n in cleaned.Split(new[] { '、', '，', ',', ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var nm = n.Trim();
                    if (IsValidName(nm)) names.Add(nm);
                }
            }
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    // 尾部噪声："等3人""觉得很赞""赞了""都很赞" 等
    private static readonly Regex NoiseTailRegex = new(
        @"(等\d*人?|觉得.*|都很?赞|赞了?|个人|大家都).*$", RegexOptions.Compiled);

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "等", "人", "觉得", "赞了", "赞", "很赞", "个人", "大家", "他们都", "全部", "的", "回复"
    };

    private static bool IsValidName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return false;
        if (n.Length > 20) return false;
        if (NoiseWords.Contains(n)) return false;
        if (int.TryParse(n, out _)) return false;
        return true;
    }

    private static Rect ComputeLineBounds(WinOcrLine line)
    {
        if (line.Words == null || line.Words.Count == 0) return default;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var w in line.Words)
        {
            var r = w.BoundingRect;
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.X + r.Width);
            maxY = Math.Max(maxY, r.Y + r.Height);
        }
        return new Rect((int)(minX / Scale), (int)(minY / Scale),
                        (int)((maxX - minX) / Scale), (int)((maxY - minY) / Scale));
    }
}
