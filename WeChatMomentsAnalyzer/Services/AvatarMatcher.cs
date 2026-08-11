using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace WeChatMomentsAnalyzer.Services;

/// <summary>
/// 联系人头像匹配结果。
/// </summary>
/// <param name="Name">超过阈值时为最佳联系人昵称，否则为 null。</param>
/// <param name="Score">最佳匹配分数（0~1）。</param>
/// <param name="BestCandidate">得分最高的联系人昵称（供诊断），无联系人时为 null。</param>
public readonly record struct AvatarMatch(string? Name, double Score, string? BestCandidate);

/// <summary>
/// 联系人头像匹配器：把联系人头像与朋友圈点赞区头像统一归一化到 64×64 并施加圆角矩形掩膜，
/// 仅在头像区域内做三通道归一化互相关（NCC）比较，规避方形裁剪四角背景色差异导致的伪匹配/漏匹配。
/// </summary>
/// <remarks>
/// 微信头像是圆角矩形（非纯方形也非纯圆形），填充方形裁剪区、仅在四个角外侧露出背景。
/// 旧方案（<see cref="ImageAutomationHelper.MatchSingleAvatar"/>）直接对原始方形头像做多尺度
/// CCoeffNormed，四角背景像素（通讯录底色 vs 点赞区底色不同）会拉低真实匹配分数导致漏匹配。
/// 本匹配器用与头像形状一致的圆角矩形掩膜把四角背景排除在外，并固定到统一尺寸，
/// 从而与 DPI / 截图比例无关，单尺度比较即可。
/// </remarks>
public sealed class AvatarMatcher : IDisposable
{
    private const int Canonical = 64;
    // 圆角矩形掩膜：内缩 MaskInset 像素避开裁剪边缘余白与抗锯齿环，四角圆半径 CornerRadius。
    // 微信头像圆角半径约为边长的 1/5，64×64 下约 12~13；可按真机截图调参。
    private const int MaskInset = 2;
    private const int CornerRadius = 12;

    private readonly Dictionary<string, Mat> _normalized = new(StringComparer.Ordinal);
    private readonly Mat _mask;
    private readonly Mat _invMask;

    public int ContactCount => _normalized.Count;
    public double Threshold { get; }

    /// <summary>加载联系人头像库并预归一化。</summary>
    /// <param name="contactAvatarsDirectory">联系人头像目录，文件名（不含扩展名）即昵称。</param>
    /// <param name="threshold">匹配阈值（建议 0.55~0.65，掩膜 NCC 对真实头像通常较高）。</param>
    public AvatarMatcher(string contactAvatarsDirectory, double threshold)
    {
        Threshold = threshold;
        _mask = CreateRoundedRectMask(Canonical, MaskInset, CornerRadius);
        _invMask = new Mat();
        Cv2.BitwiseNot(_mask, _invMask);
        LoadContacts(contactAvatarsDirectory);
    }

    /// <summary>构造圆角矩形掩膜：中间十字带 + 四角圆，组合成内缩 inset、圆角半径 radius 的填充圆角矩形。</summary>
    private static Mat CreateRoundedRectMask(int size, int inset, int radius)
    {
        var mask = new Mat(size, size, MatType.CV_8UC1, new Scalar(0));
        int x0 = inset, y0 = inset, x1 = size - 1 - inset, y1 = size - 1 - inset;
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        // 中间十字带（竖带 + 横带）
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(x0 + radius, y0, w - 2 * radius, h), new Scalar(255), -1);
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(x0, y0 + radius, w, h - 2 * radius), new Scalar(255), -1);
        // 四角圆
        Cv2.Circle(mask, new OpenCvSharp.Point(x0 + radius, y0 + radius), radius, new Scalar(255), -1);
        Cv2.Circle(mask, new OpenCvSharp.Point(x1 - radius, y0 + radius), radius, new Scalar(255), -1);
        Cv2.Circle(mask, new OpenCvSharp.Point(x0 + radius, y1 - radius), radius, new Scalar(255), -1);
        Cv2.Circle(mask, new OpenCvSharp.Point(x1 - radius, y1 - radius), radius, new Scalar(255), -1);
        return mask;
    }

    private void LoadContacts(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };
        foreach (var file in Directory.EnumerateFiles(dir).Where(f => exts.Contains(Path.GetExtension(f))))
        {
            try
            {
                using var raw = Cv2.ImRead(file, ImreadModes.Color);
                if (raw.Empty()) continue;
                var n = Normalize(raw);
                if (n != null) _normalized[Path.GetFileNameWithoutExtension(file)] = n;
            }
            catch { /* 单个模板加载失败不影响整体 */ }
        }
    }

    /// <summary>归一化：缩放到 64×64 BGR（圆角矩形头像居中），供掩膜 NCC 比较。</summary>
    private Mat? Normalize(Mat raw)
    {
        if (raw.Empty()) return null;
        var resized = new Mat();
        Cv2.Resize(raw, resized, new OpenCvSharp.Size(Canonical, Canonical), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    /// <summary>
    /// 匹配单个头像。仅读取 <paramref name="avatar"/>，不修改原 Mat。
    /// 返回最佳联系人及其分数；超过阈值时 <see cref="AvatarMatch.Name"/> 非空。
    /// </summary>
    public AvatarMatch Match(Mat avatar)
    {
        if (_normalized.Count == 0) return new AvatarMatch(null, 0, null);
        if (avatar == null || avatar.Empty()) return new AvatarMatch(null, 0, null);

        Mat? query = null;
        try
        {
            query = Normalize(avatar);
            if (query == null) return new AvatarMatch(null, 0, null);

            string? bestName = null;
            string? matchedName = null;
            double bestScore = 0;
            foreach (var kv in _normalized)
            {
                double score = MaskedNcc(query, kv.Value);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestName = kv.Key;
                }
            }

            if (bestScore >= Threshold) matchedName = bestName;
            return new AvatarMatch(matchedName, bestScore, bestName);
        }
        finally
        {
            query?.Dispose();
        }
    }

    /// <summary>
    /// 三通道圆角矩形掩膜归一化互相关。
    /// 均值仅在掩膜内计算并减去，掩膜外置零，故四角背景完全不参与比较。
    /// </summary>
    private double MaskedNcc(Mat a, Mat b)
    {
        try
        {
            // 掩膜内均值（每通道）
            var meanA = Cv2.Mean(a, _mask);
            var meanB = Cv2.Mean(b, _mask);

            // 中心化：全图减均值（Mat 算术返回 MatExpr，需 ToMat 物化），再把掩膜外置零
            using var aC = (a - new Scalar(meanA.Val0, meanA.Val1, meanA.Val2)).ToMat();
            using var bC = (b - new Scalar(meanB.Val0, meanB.Val1, meanB.Val2)).ToMat();
            aC.SetTo(new Scalar(0, 0, 0), _invMask);
            bC.SetTo(new Scalar(0, 0, 0), _invMask);

            // 分子：Σ(mask 内 a'·b')，三通道求和
            using var prod = new Mat();
            Cv2.Multiply(aC, bC, prod);
            var sumProd = Cv2.Sum(prod);
            double num = sumProd.Val0 + sumProd.Val1 + sumProd.Val2;

            // 分母：sqrt(Σ a'² · Σ b'²)
            using var aSq = new Mat();
            using var bSq = new Mat();
            Cv2.Multiply(aC, aC, aSq);
            Cv2.Multiply(bC, bC, bSq);
            var sumA = Cv2.Sum(aSq);
            var sumB = Cv2.Sum(bSq);
            double denomA = sumA.Val0 + sumA.Val1 + sumA.Val2;
            double denomB = sumB.Val0 + sumB.Val1 + sumB.Val2;
            double denom = Math.Sqrt(denomA * denomB);

            return denom <= 1e-6 ? 0 : num / denom;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        foreach (var kv in _normalized) kv.Value.Dispose();
        _normalized.Clear();
        _mask.Dispose();
        _invMask.Dispose();
    }
}
