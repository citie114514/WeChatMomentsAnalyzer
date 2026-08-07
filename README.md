# 微信朋友圈点赞分析器

一个使用 **WinUI 3 + Material Design 3** 设计的桌面程序，通过 **UI 自动化**控制微信 PC 客户端，扫描你的朋友圈，识别 **某个好友给你点过赞的所有朋友圈**，并提供 **点赞排行榜**。

## 功能

- **扫描朋友圈**：自动打开微信朋友圈并进入**个人相册页**，逐屏滚动；跳过"置顶"，对左侧带日期（今天/昨天/M月D日）的条目逐条点入详情，记录发布者、正文、时间，并截取**日期下方的点赞/评论区头像**保存；每条记录完点"返回"回相册继续
- **按好友查询**：选择/输入好友昵称 → 列出他/她给你点过赞的全部朋友圈（含正文、时间、所有点赞人）
- **点赞排行榜**：按"给你点赞数"对所有好友排序，一眼看出谁最关注你
- **MD3 设计语言**：基于 Material Design 3 的明暗配色、TypeScale、形状（CornerRadius）、Card / Button / Chip 样式，跟随系统主题
- **本地存储**：SQLite 位于 `%LOCALAPPDATA%\WeChatMomentsAnalyzer\moments.db`，支持重复扫描去重；详情头像截图位于 `%LOCALAPPDATA%\WeChatMomentsAnalyzer\MomentsAvatars\<内容指纹>\`；扫描日志位于同目录 `scan_log.txt`

## 技术栈

| 层 | 技术 |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.5) + .NET 8 |
| 设计语言 | Material Design 3（自定义 XAML 资源字典） |
| MVVM | CommunityToolkit.Mvvm |
| 微信自动化 | FlaUI (UIA3) + Win32 P/Invoke（SendInput 真实点击/滚轮、CopyFromScreen 截图） |
| 图像 | OpenCvSharp（头像连通域提取） |
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
│   ├── WeChatAutomationService.cs   微信 UI 自动化（扫描主链路）
│   ├── ImageAutomationHelper.cs     截图/点击/滚轮/OpenCV 头像提取
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
[微信主窗口] 点头像 → 个人面板 → 点"朋友圈" → [朋友圈窗口·个人相册页]
                                                    │ 逐屏滚动；跳过置顶；
                                                    │ 仅选左侧带日期的条目
                                                    ▼
                              点击条目图片区(无图点文字区) → [详情页]
                                                    │ 解析 发布者/正文/时间
                                                    │ 截图日期下方点赞·评论区
                                                    │ OpenCV 提取方形头像保存
                                                    ▼ 点"返回"
                                    [MomentsRepository (SQLite)]
                                                    │
                            ┌───────────────────────┴───────────────────┐
                            ▼                                           ▼
                  [按好友查询页]                                 [排行榜页]
```

## 微信 UI 自动化说明

- 微信 4.x 主窗口类名为 `Qt*QWindowIcon`（标题"微信"），旧版 3.x 为 `WeChatMainWndForPC`，均通过 `EnumWindows` + 进程名（`WeChat`/`Weixin`）查找。
- 朋友圈入口（主路径，每步均验证）：真实点击主窗口左上角头像 → 弹出个人面板（类名以 `QWindowToolSaveBits` 结尾的独立顶层窗口）→ 面板内 UIA 定位"朋友圈"Button 并真实点击（失败用校准坐标兜底）。朋友圈窗口已存在时，该入口会把它导航到个人相册页。
- 视图判别（UIA）：含 Text"详情"=详情页；含 Text"相册"=个人相册页（扫描目标）；含 List"朋友圈"=信息流页（自动经面板入口切到相册）。
- 相册条目过滤：ListItem 名称须以日期前缀开头（`今天/昨天/N分钟前/M月D日/…年…月`），跳过"置顶"与签名条；条目须完全位于可视区。
- 进入详情：优先点条目图片九宫格区（Left+245），失败改点右侧文字区，再失败按 Esc 收起浮层重试；以 Text"详情"出现为进入判据。
- 详情记录：最长命名的 ListItem 即帖子本体（"发布者 内容 包含N张图片 完整日期时间"），正则解析后按内容指纹去重入库；随后截取帖子（日期行）下方区域，用灰度直方图中位数作背景、连通域+形态学闭运算提取近方形头像，保存到 `MomentsAvatars\<指纹>\`；最后点左上角"返回"回相册。
- 滚动/点击均为 SendInput 真实硬件事件；扫描期间本程序自动最小化避免遮挡。

## 注意事项

- **不同微信版本的 UI 树结构存在差异**，启发式解析（日期前缀正则 + 详情命名规则）在微信 4.x 上验证可用，但可能漏抓或误判；相关正则集中在 `WeChatAutomationService` 顶部，便于调整。
- 扫描会真实点击/滚动微信窗口，**扫描期间请勿操作鼠标键盘**；若误入其他页面，程序会尝试按 Esc/点返回恢复。
- 头像提取依赖截图与 OpenCV 连通域分析，纯色或过暗的头像可能漏检；原始截图（`detail_below_*.png`）会一并保存便于核查。
- 头像/截图保存路径受 Windows MAX_PATH(260) 限制，故放在 `%LOCALAPPDATA%` 短路径下；请勿把程序部署到极深目录后改回安装目录存储。
- 程序不会上传任何数据，全部存储在本地 SQLite。
- 自动化操作微信属于非官方交互，使用风险自负。

## 致谢

本软件的设计与开发参考了以下优秀的开源项目，在此表示诚挚感谢：

### 项目参考

- [March7thAssistant](https://github.com/moesnow/March7thAssistant) — 提供了 **SendInput 真实硬件模拟 + 前台截图 + OpenCV 模板匹配** 的自动化方案参考，本软件中 `WeChatAutomationService` 与 `ImageAutomationHelper` 的实现思路受其启发。
- [MaaAssistantArknights (MAA)](https://github.com/MaaAssistantArknights/MaaAssistantArknights) — 提供了**图像识别 + UI 自动化**的整体架构设计参考，本软件在自动化流程编排、状态机设计等方面借鉴了其成熟经验。

### 开源依赖

本软件离不开以下开源项目的支持，感谢所有维护者与贡献者：

| 项目 | 用途 |
|---|---|
| [FlaUI](https://github.com/FlaUI/FlaUI) | Windows UI 自动化（UIA3） |
| [OpenCvSharp](https://github.com/shimat/opencvsharp) | 图像处理与模板匹配 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM 框架 |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | 本地数据存储 |
| [Dapper](https://github.com/DapperLib/Dapper) | 轻量级 ORM |

## 声明

- 本软件使用 [GPL-3.0](https://www.gnu.org/licenses/gpl-3.0.html) 协议开源，仅供学习和交流使用。
- 本软件是免费、开源的项目，任何形式的商业化使用、二次销售或代练收费均与本软件无关。
- 自动化操作微信 PC 客户端属于非官方行为，使用本软件产生的任何风险及后果由用户自行承担。
- 本软件不会收集、上传或向第三方传输任何用户数据，所有数据均存储在本地。

## 许可

GPL-3.0
