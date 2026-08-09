using System.Collections.Generic;
using WeChatMomentsAnalyzer.Data;
using WeChatMomentsAnalyzer.Models;

namespace WeChatMomentsAnalyzer.Services;

/// <summary>
/// 业务分析服务：包装仓储，提供按好友查询、排行榜、统计等。
/// </summary>
public sealed class AnalysisService
{
    private readonly MomentsRepository _repo;

    public AnalysisService(MomentsRepository repo)
    {
        _repo = repo;
    }

    /// <summary>保存"我的昵称"到配置（去首尾空白：历史上曾存入带前导空格的昵称，
    /// 与扫描解析的 publisher 精确匹配失败，导致排行榜/按好友查询全部为空）</summary>
    public void SaveMyNickname(string nickname) => _repo.SetSetting("my_nickname", nickname.Trim());

    public string LoadMyNickname() => _repo.GetSetting("my_nickname", string.Empty)!.Trim();

    /// <summary>查询指定好友给我点过赞的所有朋友圈</summary>
    public List<MomentPost> GetMomentsLikedByFriend(string friendName)
    {
        var me = LoadMyNickname();
        if (string.IsNullOrEmpty(me)) return new List<MomentPost>();
        return _repo.GetMomentsLikedByFriend(me, friendName);
    }

    /// <summary>排行榜：好友给我点赞数倒序</summary>
    public List<FriendStats> GetRanking()
    {
        var me = LoadMyNickname();
        if (string.IsNullOrEmpty(me)) return new List<FriendStats>();
        var list = _repo.GetRanking(me);
        for (int i = 0; i < list.Count; i++) list[i].Rank = i + 1;
        return list;
    }

    /// <summary>给我点过赞的所有好友昵称（自动补全用）</summary>
    public List<string> GetAllLikerNames()
    {
        var me = LoadMyNickname();
        if (string.IsNullOrEmpty(me)) return new List<string>();
        return _repo.GetAllLikerNames(me);
    }

    public (int moments, int likes) GetStats() => (_repo.CountMoments(), _repo.CountLikes());
}
