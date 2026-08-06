using System;
using Microsoft.UI.Xaml.Data;

namespace WeChatMomentsAnalyzer.Views;

/// <summary>
/// DateTime -> 友好文案的转换器
/// </summary>
public sealed class DateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DateTime dt)
        {
            if (dt == default) return "—";
            var diff = DateTime.Now - dt;
            if (diff.TotalMinutes < 1) return "刚刚";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} 小时前";
            if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
            return dt.ToString("yyyy-MM-dd");
        }
        return value?.ToString() ?? "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Likers 列表 -> 逗号拼接字符串
/// </summary>
public sealed class LikersToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is System.Collections.Generic.List<string> list)
        {
            return list.Count == 0 ? "暂无" : string.Join("、", list);
        }
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
