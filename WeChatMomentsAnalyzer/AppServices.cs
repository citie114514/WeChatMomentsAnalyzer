using System;
using WeChatMomentsAnalyzer.Data;
using WeChatMomentsAnalyzer.Services;

namespace WeChatMomentsAnalyzer;

/// <summary>
/// 简单的服务容器（替代 DI），全局共享仓储与分析服务实例。
/// </summary>
public static class AppServices
{
    public static MomentsRepository Repository { get; } = new();
    public static AnalysisService Analysis { get; } = new(Repository);
    public static WeChatAutomationService Automation { get; } = new();
}
