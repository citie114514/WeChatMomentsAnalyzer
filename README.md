# 微信朋友圈点赞分析器

一个使用 **WinUI 3 + Material Design 3** 设计的桌面程序，通过 **UI 自动化**控制微信 PC 客户端，扫描你的朋友圈，识别 **某个好友给你点过赞的所有朋友圈**，并提供 **点赞排行榜**。

## 功能

- **扫描朋友圈**：自动打开微信朋友圈、向下滚动、抓取每条朋友圈的发布者、正文、时间、点赞人列表
- **按好友查询**：选择/输入好友昵称 → 列出他/她给你点过赞的全部朋友圈（含正文、时间、所有点赞人）
- **点赞排行榜**：按"给你点赞数"对所有好友排序，一眼看出谁最关注你
- **MD3 设计语言**：基于 Material Design 3 的明暗配色、TypeScale、形状（CornerRadius）、Card / Button / Chip 样式，跟随系统主题
- **本地存储**：使用 SQLite（位于 `%LOCALAPPDATA%\WeChatMomentsAnalyzer\moments.db`），支持重复扫描去重

## 技术栈

| 层 | 技术 |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.5) + .NET 8 |
| 设计语言 | Material Design 3（自定义 XAML 资源字典） |
| MVVM | CommunityToolkit.Mvvm |
| 微信自动化 | UIAutomation COM（dynamic 后期绑定）+ Win32 P/Invoke |
| 存储 | Microsoft.Data.Sqlite + Dapper |

## 项目结构

```
WeChatMomentsAnalyzer/
├── App.xaml(.cs)              应用入口
├── MainWindow.xaml(.cs)       主窗口（NavigationView 导航）
├── AppServices.cs             服务容器
├── Theme/Md3Theme.xaml        MD3 配色/字号/形状/控件样式
├── Models/MomentPost.cs       数据模型
├── Data/MomentsRepository.cs  SQLite 仓储
├── Services/
│   ├── WeChatAutomationService.cs   微信 UI 自动化
│   └── AnalysisService.cs           业务查询/排行
├── ViewModels/                MVVM ViewModel
└── Views/                     扫描页/好友查询页/排行榜页
```

## 环境要求

- Windows 10 1809 (17763) 及以上
- .NET 8 SDK
- Windows App SDK 1.5 工作负载（Visual Studio 2022 17.4+ 或独立 Build Tools）
- 微信 PC 客户端 3.x / 4.x（已登录）

## 构建与运行

```powershell
# 还原 + 构建
.\build.ps1

# 构建后直接运行
.\build.ps1 -Run

# Release x64
.\build.ps1 -Config Release -Arch x64 -Run
```

或在 Visual Studio 2022 中打开 `WeChatMomentsAnalyzer.sln`，按 F5 运行。

## 使用流程

1. **启动并登录微信 PC 客户端**，把微信窗口保持在桌面（不要最小化）。
2. **打开本程序**，进入"扫描朋友圈"页。
3. **填写你的微信昵称**（必须与微信客户端显示完全一致，用于识别"我"发布的朋友圈）。
4. 设置扫描屏数（默认 30，每屏约 4-8 条），点击 **开始扫描**。
   - 扫描过程中程序会自动打开微信朋友圈窗口、向下滚动并抓取。**请勿操作鼠标键盘**。
   - 进度条与日志会实时显示。
5. 扫描完成后，切到：
   - **按好友查询**：输入/选择好友昵称 → 查询 → 列出他/她给你点过赞的全部朋友圈。
   - **点赞排行榜**：自动按点赞数排序，可点击"刷新"重新加载。

## 工作原理

```
[微信 PC 客户端] ──UIAutomation COM──> [WeChatAutomationService]
                                              │
                                              ▼
                                    ElementFromHandle / FindAll
                                              │
                                              ▼
                                    按坐标聚类 → 解析发布者/正文/点赞人
                                              │
                                              ▼
                                    [MomentsRepository (SQLite)]
                                              │
                            ┌─────────────────┴─────────────────┐
                            ▼                                   ▼
                  [按好友查询页]                         [排行榜页]
        SELECT m.* FROM moments m              SELECT friend, COUNT(*) AS cnt
        JOIN likes l ON l.moment_id=m.id       FROM likes JOIN moments ...
        WHERE m.publisher=@me AND l.friend=@f  GROUP BY friend ORDER BY cnt DESC
```

## 微信 UI 自动化说明

- 窗口类名 `WeChatMainWndForPC` 通过 `EnumWindows` 枚举查找。
- 朋友圈入口：优先查找 Name 含"朋友圈"且支持 InvokePattern 的元素；失败则按坐标点击侧边栏。
- 滚动：将鼠标移到朋友圈窗口中央，发送 `MOUSEEVENTF_WHEEL` 滚动一屏。
- 内容提取：`FindAll(TreeScope_Descendants, TrueCondition)` 收集所有带 Name 的元素，按 `BoundingRectangle.Y` 聚类为单条朋友圈，再用正则区分发布者/时间/正文/点赞人。

## 注意事项

- **不同微信版本的 UI 树结构存在差异**，启发式解析（坐标聚类 + 正则）在大多数情况下可用，但可能漏抓或误判。可在 `WeChatAutomationService.ParseCluster` 中调整正则。
- 点赞人列表若被微信渲染为图形而 UIAutomation 拿不到 Name，需要后续接入 OCR 兜底（已预留扩展点）。
- 程序不会上传任何数据，全部存储在本地 SQLite。
- 自动化操作微信属于非官方交互，使用风险自负。

## 许可

MIT
