using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using WeChatMomentsAnalyzer.Models;

namespace WeChatMomentsAnalyzer.Data;

/// <summary>
/// SQLite 仓储：朋友圈 + 点赞记录 + 配置
/// </summary>
public sealed class MomentsRepository
{
    private readonly string _connStr;

    public MomentsRepository(string? dbPath = null)
    {
        dbPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatMomentsAnalyzer",
            "moments.db");

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connStr = $"Data Source={dbPath}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS moments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                publisher TEXT NOT NULL,
                content TEXT NOT NULL,
                post_time TEXT,
                scan_time TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE
            );
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS likes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                moment_id INTEGER NOT NULL,
                friend_name TEXT NOT NULL,
                scan_time TEXT NOT NULL,
                UNIQUE(moment_id, friend_name),
                FOREIGN KEY(moment_id) REFERENCES moments(id)
            );
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """);
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_likes_friend ON likes(friend_name);");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_likes_moment ON likes(moment_id);");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_moments_publisher ON moments(publisher);");
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 清空朋友圈与点赞数据（扫描开始时调用）：历史版本曾写入评论/OCR/位置等污染数据，
    /// 按条替换式更新无法清除未被重新扫到的脏行，故每次扫描全量重建。
    /// </summary>
    public void ClearAll()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            conn.Execute("DELETE FROM likes;", transaction: tx);
            conn.Execute("DELETE FROM moments;", transaction: tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 插入或更新一条朋友圈与其点赞列表（按 content_hash 去重）
    /// </summary>
    public void UpsertMoment(MomentPost post)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var existingId = conn.ExecuteScalar<long?>(
                "SELECT id FROM moments WHERE content_hash = @h;",
                new { h = post.ContentHash }, tx);

            long momentId;
            if (existingId is long id)
            {
                momentId = id;
                conn.Execute("""
                    UPDATE moments
                    SET publisher = @p, content = @c, post_time = @pt, scan_time = @st
                    WHERE id = @id;
                    """,
                    new { p = post.Publisher, c = post.Content, pt = post.PostTime?.ToString("O"), st = post.ScanTime.ToString("O"), id }, tx);
            }
            else
            {
                momentId = conn.ExecuteScalar<long>("""
                    INSERT INTO moments(publisher, content, post_time, scan_time, content_hash)
                    VALUES (@p, @c, @pt, @st, @h);
                    SELECT last_insert_rowid();
                    """,
                    new { p = post.Publisher, c = post.Content, pt = post.PostTime?.ToString("O"), st = post.ScanTime.ToString("O"), h = post.ContentHash }, tx);
            }

            // 替换式更新点赞列表：先清空该朋友圈旧记录，避免历史污染/重复累积
            conn.Execute("DELETE FROM likes WHERE moment_id = @mid;",
                new { mid = momentId }, tx);

            foreach (var liker in post.Likers)
            {
                conn.Execute("""
                    INSERT OR IGNORE INTO likes(moment_id, friend_name, scan_time)
                    VALUES (@mid, @fn, @st);
                    """,
                    new { mid = momentId, fn = liker, st = post.ScanTime.ToString("O") }, tx);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 查询某个好友给我点过赞的所有朋友圈
    /// </summary>
    public List<MomentPost> GetMomentsLikedByFriend(string myNickname, string friendName)
    {
        using var conn = OpenConnection();
        var rows = conn.Query<(long Id, string Publisher, string Content, string? PostTime, string ScanTime)>(
            """
            SELECT m.id, m.publisher, m.content, m.post_time, m.scan_time
            FROM moments m
            INNER JOIN likes l ON l.moment_id = m.id
            WHERE m.publisher = @me AND l.friend_name = @f
            ORDER BY m.post_time DESC;
            """,
            new { me = myNickname, f = friendName });

        var list = new List<MomentPost>();
        foreach (var r in rows)
        {
            var likers = conn.Query<string>(
                "SELECT friend_name FROM likes WHERE moment_id = @mid;",
                new { mid = r.Id }).ToList();
            list.Add(new MomentPost
            {
                Id = r.Id,
                Publisher = r.Publisher,
                Content = r.Content,
                PostTime = string.IsNullOrEmpty(r.PostTime) ? null : DateTime.Parse(r.PostTime),
                ScanTime = DateTime.Parse(r.ScanTime),
                Likers = likers
            });
        }
        return list;
    }

    /// <summary>
    /// 排行榜：按好友给我点赞数倒序
    /// </summary>
    public List<FriendStats> GetRanking(string myNickname)
    {
        using var conn = OpenConnection();
        var rows = conn.Query<FriendStats>(
            """
            SELECT l.friend_name AS FriendName,
                   COUNT(*) AS LikeCount,
                   MAX(l.scan_time) AS LastLikeTime
            FROM likes l
            INNER JOIN moments m ON m.id = l.moment_id
            WHERE m.publisher = @me AND l.friend_name <> @me
            GROUP BY l.friend_name
            ORDER BY LikeCount DESC, LastLikeTime DESC;
            """,
            new { me = myNickname });

        return rows.ToList();
    }

    /// <summary>
    /// 全部给我点过赞的好友昵称（用于自动补全）
    /// </summary>
    public List<string> GetAllLikerNames(string myNickname)
    {
        using var conn = OpenConnection();
        return conn.Query<string>(
            """
            SELECT DISTINCT l.friend_name
            FROM likes l
            INNER JOIN moments m ON m.id = l.moment_id
            WHERE m.publisher = @me AND l.friend_name <> @me
            ORDER BY l.friend_name;
            """,
            new { me = myNickname }).ToList();
    }

    public int CountMoments()
    {
        using var conn = OpenConnection();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM moments;");
    }

    public int CountLikes()
    {
        using var conn = OpenConnection();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM likes;");
    }

    public string? GetSetting(string key, string? def = null)
    {
        using var conn = OpenConnection();
        var v = conn.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @k;", new { k = key });
        return v ?? def;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = OpenConnection();
        conn.Execute("""
            INSERT INTO settings(key, value) VALUES (@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, new { k = key, v = value });
    }
}
