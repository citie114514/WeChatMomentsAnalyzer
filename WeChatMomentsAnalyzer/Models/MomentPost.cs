using System;
using System.Collections.Generic;

namespace WeChatMomentsAnalyzer.Models;

/// <summary>
/// 一条朋友圈
/// </summary>
public sealed class MomentPost
{
    public long Id { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? PostTime { get; set; }
    public DateTime ScanTime { get; set; }
    /// <summary>内容指纹，用于去重</summary>
    public string ContentHash { get; set; } = string.Empty;

    public List<string> Likers { get; set; } = new();
}

/// <summary>
/// 一条点赞记录
/// </summary>
public sealed class LikeRecord
{
    public long Id { get; set; }
    public long MomentId { get; set; }
    public string FriendName { get; set; } = string.Empty;
    public DateTime ScanTime { get; set; }
}

/// <summary>
/// 好友统计行（排行榜 / 列表项）
/// </summary>
public sealed class FriendStats
{
    public string FriendName { get; set; } = string.Empty;
    public int LikeCount { get; set; }
    public DateTime LastLikeTime { get; set; }
    /// <summary>名次（从 1 开始），由业务层填充</summary>
    public int Rank { get; set; }
    /// <summary>名次展示文案（1/2/3 显示奖牌 emoji，其余显示数字）</summary>
    public string RankLabel => Rank switch
    {
        1 => "1",
        2 => "2",
        3 => "3",
        _ => Rank.ToString()
    };
}

/// <summary>
/// 扫描配置
/// </summary>
public sealed class ScanConfig
{
    /// <summary>我的微信昵称（用于识别"我的朋友圈"）</summary>
    public string MyNickname { get; set; } = string.Empty;
    /// <summary>最多滚动多少屏（默认 200：实测约 300 条朋友圈需 150 屏以上，50 屏会在约 60 条处提前截断）</summary>
    public int MaxScrollScreens { get; set; } = 200;
    /// <summary>每屏等待毫秒</summary>
    public int ScrollWaitMs { get; set; } = 1200;

    /// <summary>左侧栏"朋友圈"图标模板路径</summary>
    public string MomentsIconTemplatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Templates", "moments_icon.png");
    /// <summary>自己头像模板路径，用于点击右上角头像</summary>
    public string MyAvatarTemplatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Templates", "my_avatar.png");
    /// <summary>联系人头像库目录，文件名即昵称</summary>
    public string ContactAvatarsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatMomentsAnalyzer", "Contacts");
    /// <summary>朋友圈详情头像保存目录（每条朋友圈一个子目录，目录名为内容指纹）。
    /// 放在 %LOCALAPPDATA% 短路径下：OpenCV 原生写文件受 MAX_PATH(260) 限制，安装目录可能过长。</summary>
    public string MomentsAvatarsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatMomentsAnalyzer", "MomentsAvatars");
    /// <summary>图像匹配相似度阈值（0~1）</summary>
    public double MatchThreshold { get; set; } = 0.55;
    /// <summary>联系人头像匹配阈值（点赞区头像较小，略低于通用阈值以提升召回）</summary>
    public double ContactMatchThreshold { get; set; } = 0.60;
    /// <summary>点入朋友圈详情后等待毫秒</summary>
    public int DetailOpenWaitMs { get; set; } = 1200;
    /// <summary>在详情页内最多滚动几次寻找点赞区</summary>
    public int DetailMaxScrolls { get; set; } = 6;
}
