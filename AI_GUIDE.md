# 📘 AI_GUIDE — 考研计划（WPF 版）工程导读

> **写给后续 AI 的第一份文档。** 改动任何代码前，先读「§0 最高契约」和「§2 目录地图」；
> 改到具体模块前，读对应章节。这份文档描述「什么放哪、用什么逻辑」，代码里的中文注释也值得信。
>
> 项目本质：把一套 **PyQt5 桌面程序完整移植成 C#/WPF（.NET 8）**。旧版设计文档在
> `docs/CODEX_DESKTOP_DESIGN_SPEC.md`（Python 视角，只有对照价值）。代码注释与 `Tools` 之外
> 全是中文，本导读亦用中文。
>
> ⚠️ **别信仓库根 `README.md`**：它还是 PyQt5 时代的旧文档（写着 `python main.py`、
> `pip install -r requirements.txt`），对应文件早已删除。工程现状以此导读为准。

---

## §0 最高契约（改动前必读，违反 = 灾难）

1. **data.json 字节级往返是最高契约。** 应用加载 → 无操作保存 → 字节必须完全一致。
   依赖三件事：`System.Text.Json.Nodes.JsonObject` 作主对象（保键序/未知键/嵌套配置原样存活）、
   `PythonJson` 手写序列化器复刻 Python `json.dump(ensure_ascii=False, indent=2)` 的字节格式
   （**CRLF 行尾**、整数值浮点补 `.0` 如 `61.2`、中文原样 UTF-8）、以及「惰性键」规则（见下）。
   任何「重建整个 JsonObject」「重排键」「换序列化器」都会打破往返。
2. **活数据 = `dist-wpf/data.json`**（用户跑发布版，exe 旁）。仓库根 `data.json` 只是回拨了
   mtime 的 dev 种子（mtime **必须保持在活数据之后更旧**，否则 `PreserveNewest` 会用旧种子覆盖活数据）。
   开发跑 bin 里的 exe 只改输出目录副本，**真数据从不被开发触碰**。
3. **惰性键规则：** 新功能若引入新顶层键（如 `plans`/`active_plan`/`pet_skin`/`focus_plan`），**绝不能加进
   `CreateDefaults()`**——否则 Load 的浅合并会把键写进默认结构，打破往返。只能在用户真正操作时才落盘；
   切回默认值时 `Remove(key)` 恢复字节原状。读方法纯内存默认、绝不写 Data。
4. **用户的任何学习数据不得删除**（每日计划 daily、固定任务 tasks、专注记录 focus_history、
   focus_sessions、计划 plans）。删除任务/清理是显式用户操作，不是系统行为。
5. 手动编辑活数据前 **必须先杀掉 PetPlanner.exe**——进程内存持有 JsonObject，任何 Save 都会把旧值写回
   （`data.json.tmp` 残留 + md5 每次读都变 = 还有进程在写）。

---

## §1 技术栈与一句话架构

- **C# / .NET 8**，`net8.0-windows`，`UseWPF=true` + `UseWindowsForms=true`（**必须**，为 WinForms 的
  NotifyIcon / ColorDialog / FolderBrowserDialog）。
- `AssemblyName=PetPlanner` → 进程/exe = **PetPlanner.exe**（查进程用 `tasklist | grep -i PetPlanner`）。
- 依赖仅一个：`NAudio`（实时字幕回环采集）。
- 架构是**分层 + 组合根**：`Services/`（纯 C#，可单测，不碰 UI）← `Views/`（WPF 界面）← `App`（组合根）。
  UI 不直接读盘，一律走 `DataStore`；服务层的事件（后台线程触发）UI 侧必须 `Dispatcher.InvokeAsync` 封回主线程。
- 主题：**骨白浅色 + 强调蓝 #339CFF**，`Theme/` 下 4 个 ResourceDictionary 在 `App.xaml` 合并。
  深浅色：`Colors.Dark.xaml` 是暗色变体（README 宣称跟随系统，实际启用情况见 Theme 代码）。

---

## §2 目录地图（文件 → 干什么）

```
KaoyanPlanner.WPF/
├── KaoyanPlanner.WPF.sln
├── KaoyanPlanner.WPF/                        ← 主项目（PetPlanner）
│   ├── App.xaml(.cs)                         组合根：单实例→加载数据→心跳→托盘→主窗+桌宠
│   ├── AppInfo.cs                            版本身份唯一来源：个人版/测试版（TEST_BUILD）→
│   │                                        单实例管道、自启值、关于页、托盘名（版本隔离，见 §10.9）
│   ├── GlobalUsings.cs                       关键！WinForms 冲突名的 WPF 版全局别名（见 §8）
│   ├── KaoyanPlanner.WPF.csproj              发布配置：desk_pet 递归分发、ExcludeFromSingleFile
│   ├── Services/                             纯 C# 服务层（全部可单测）
│   │   ├── DataStore.cs                      数据主对象 + 读写 + 默认结构 + 今日任务/固定任务/专注 API
│   │   ├── PythonJson.cs                     字节级往返序列化器（CRLF、浮点补 .0）
│   │   ├── AppPaths.cs                       路径解析（KAOYAN_DATA_DIR 环境变量可覆盖）
│   │   ├── SecretService.cs                  secret.json 读写（api_key/asr_api_key，不入库）
│   │   ├── Fmt.cs / CaptionLabels.cs         格式化 / 字幕语言中文标签
│   │   ├── HeartbeatService.cs               心跳：跨日欠卡结算+归档、提醒触发、未完成提醒
│   │   ├── FocusTimerService.cs              专注正计时（时钟注入可单测、15s 节流落盘）
│   │   ├── PlanPalette.cs                    专注目标(固定任务名)→图表色板映射（BrushKeyFor 纯函数可单测）
│   │   ├── ReminderService.cs / NotificationService.cs   定时提醒匹配 / 系统通知+弹窗
│   │   ├── SingleInstance.cs / TrayService.cs / AutostartService.cs   单实例/托盘/注册表自启
│   │   ├── AniClip.cs                         .ani 动画解析（RIFF ACON → 冻结帧）
│   │   ├── PetSkinService.cs                 桌宠形象系统（子文件夹皮肤、导入、解析，见 §6）
│   │   ├── PetCommandService.cs              宠物对话里的指令（改计划/遥控专注）
│   │   ├── PetChatService.cs                 宠物 AI 闲聊（智谱 GLM，回退链，离线话术）
│   │   ├── CaptionService.cs                 NAudio 回环采集 → 硅基流动 ASR（流式字幕）
│   │   ├── TtsService.cs                      GPT-SoVITS 语音播报（拉起/终止服务进程）
│   │   └── IPetFocus.cs                      桌宠↔专注计时 的桥接口
│   ├── Controls/
│   │   ├── TaskItemControl.xaml(.cs)         今日任务行（打卡/勾选/编辑/删除，动画完成后才落盘）
│   │   ├── TimeBox.xaml(.cs)                 HH:mm 掩码输入
│   │   ├── HistogramControl.cs               24 小时专注直方图（自定义 OnRender）
│   │   └── FocusGallery.cs                   统计页画廊：鼠标聚焦缩放 + 有界滚动（见 §5 StatsTab）
│   ├── Views/
│   │   ├── MainWindow.xaml(.cs)              主窗口外壳 + 页签导航 + 标题倒计时 + 位置持久化
│   │   ├── PlanSidebar.xaml(.cs)             左侧计划导航栏（列表/切换/新建/重命名/删除）
│   │   ├── Tabs/                             PlanTab/TimerTab/ReminderTab/StatsTab 四大主面板
│   │   ├── Settings/                         设置页（整页，非浮窗）：SettingsPage 宿主 + ISettingsSection 接口 + 6 分栏，见 §7
│   │   ├── Pet/                              PetWindow(桌宠)/ChatWindow(聊天记录)/PetBubbleWindow(气泡)
│   │   │                                     /BlackboardWindow(字幕黑板)
│   │   └── Dialogs/                          仅剩 3 个真弹窗：FixedTaskDialog/PlanNameDialog/ReminderPopupWindow
│   ├── Native/DwmInterop.cs                  圆角窗口（DwmSetWindowAttribute）
│   └── Theme/                                Colors/Colors.Dark/Typography/Styles/Animations
├── KaoyanPlanner.WPF.Tests/                  xUnit 测试（§9）
├── dist-wpf/                                 发布产物目录 + 活数据 data.json + 桌宠动画 desk_pet/
├── desk_pet/                                 桌宠动画源（顶层=默认艾莲，子文件夹=皮肤）
├── assets/icon.png / icon.ico                应用图标
├── model/ellen/                              GPT-SoVITS 艾莲声音模型（外部服务用，不打包）
├── docs/CODEX_DESKTOP_DESIGN_SPEC.md         旧 PyQt5 设计文档（仅对照）
├── tools/bench_tts.py                        TTS 服务性能脚本
├── secret.json                               本地密钥（gitignore，不入库）
└── data.json                                 dev 种子（mtime 已回拨，见 §0.2）
```

---

## §3 数据层（DataStore / PythonJson）

- `DataStore.Data` 是 **`JsonObject`**（`System.Text.Json.Nodes`）。一切读写 = 原地改这个对象，
  天然保键序、保未知键、保嵌套配置整体存活。**禁止**把某块反序列化成强类型再写回。
- `Load()`：文件存在 → `JsonNode.Parse` → 与 `CreateDefaults()` **浅合并**（实数据在上、默认在下，
  只补缺失键）→ `MigrateFocus()`（旧 focus_sessions 迁进 focus_history）。损坏/IO 异常 → 改名 `.bak` + 默认。
- `Save()`：`PythonJson.ToJson(Data)` → `.tmp` + `File.Move(overwrite)` 原子写，**CRLF、无尾随换行**。
  成功后触发 `Changed` 事件（UI 据此重建面板）。`SaveQuiet()` 同写但不触发 `Changed`（窗口位置/专注节流等高频低价值写入）。
- 内部静态帮助（`internal static`，**必须 `DataStore.GetString(...)` 类型名调用，别用实例**，CS0176）：
  `GetString(JsonNode?)` / `GetBool(JsonNode?, def)` / `GetInt` / `GetDouble` / `GetObj(parent, key)` /
  `GetOrCreateObj(parent, key)`。它们对 null/缺失键宽容返回默认。
- 今日任务 API：`EnsureToday()`（保证 `daily[today]` 存在，不 Save）、`AddTask/SetTaskDone/DeleteTask/EditTask`（改 `daily[today]` 数组，Save）。
- 固定任务：`AddFixed/ParseDays/…/PunchFixed`。**打卡是切换式**（未打→打一次，有欠卡则补卡抵消 1 天；已打→撤销）。
  当天是否补卡/刚打卡完成只存**内存 `_todayPunch`**（taskId→(date,backfill,completed)），**绝不落盘**（字节契约）。
- **日界**：每天从凌晨 2 点起算（`WindowToday`，00:00–02:00 归前一天），**时间统计同样按 2 点→次日 2 点**分段——每日归档、专注计时、统计页「天」都是这个窗口。
- 跨日：`HeartbeatService` 驱动 `RolloverFixed()`（结算欠卡 owed，有变化才 Save）→ `EnsureToday()`（归档旧日）→ `DayChanged` 事件刷新面板。
- 专注计时 API：`AddFocusSeconds(int hour, int seconds, string? plan = null)` **双写**——总时长写
  `focus_history[today][hour]`（形状不变、旧数据兼容，`today` = 窗口日），plan 非空时再写**惰性键 `focus_plan`**：
  `focus_plan = {"yyyy-MM-dd": {"<专注目标名>": 分钟(3位小数)}}`（天级不分小时，**不进 CreateDefaults**）。
  **专注目标 = 长期计划（固定任务）的名称**：计时页右侧面板列「不分类」+ 全部固定任务（`GetFixedTaskNames()` 按 tasks 顺序），
  选中某个固定任务即在其名下计时；plan 为 null/空串（「不分类」）只写总时长，**绝不落 focus_plan 键**（字节契约）。
  改名迁移：`RenamePlan`（计划分类）与 `EditFixed`（固定任务改名）都会把 `focus_plan[date][旧名]→新名`
  （同名**求和**防「已删残留键」碰撞）；`DeletePlan`/`DeleteFixed` **不碰** focus_plan（学习数据不删，历史灰显）。
  统计里「未分类」= 当日总 − Σ已标记（钳制 ≥0，统计页推导，不额外存储）。
  专注目标→颜色映射走纯函数 `PlanPalette.BrushKeyFor(name, fixedTaskNames)`（§2 的 `Services/PlanPalette.cs`，
  已删除目标/不分类不在列表 → 灰）。
- 常见坑：`GetOrCreateObj` 拿到的 JsonArray 若被方法「重建」（如 ClearDone 换新数组），旧引用变 stale，
  调用方改完必须**重新取** `daily[today]`。JsonNode 跨父节点合并要先 `DeepClone()`（复父限制）。

---

## §4 应用启动与主窗口

**`App.OnStartup` 顺序**（改启动流程看这里）：
单实例检查（`SingleInstance`，已有实例则唤醒主窗并 Shutdown）→ `new DataStore()` + `Load()` →
`HeartbeatService` + `FocusTimerService` → `MainWindow`（传入共享 focusTimer）→ `PetWindow`（同样共享）→
`_mainWindow.PetWindow = _petWindow`（设置页换皮肤/字幕要实时通知桌宠）→ `TrayService` →
`heartbeat.Start()` → `mainWindow.Show()` + `petWindow.Show()`（桌宠启动默认显示，阶段二要求）。
`OnExit`：`ShutdownTts`（终止语音服务）→ `ShutdownCaption`（停采集/识别）→ 托盘/单实例 Dispose。

**`MainWindow` 布局**（XAML）：`RowDefinitions 48/*/30`（头部 / 内容 / 状态栏）。
内容区 `ColumnDefinitions 52/Auto/272/*`（**左侧 52px rail** / 1px GridSplitter / **272px PlanSidebar** / **主区 mainHost**）。
- rail = 竖排 5 个 RadioButton（`RailButtonStyle`，GroupName=rail）：计划/计时/提醒/统计/⚙设置，选中 = 左侧 2px 蓝条。
- `SelectTab(tag)`：`mainHost.Content` 换 UserControl + **160ms 淡入 + Y 4→0**（`CubicEase EaseOut`），
  尊重 `SystemParameters.ClientAreaAnimation` 与 `reduce_motion`。`SyncRail` 同步勾选态。
  **同页再点**兜底恢复不透明（防中断动画残留半透明）。
- 头部：图标 + 可双击改名的标题 + 考研倒计时（`exam_date`）+ 右侧最小化/最大化/关闭。
- 状态栏：任务进度 + 专注状态 + ❄冻结/欠卡标记。
- 位置尺寸持久化：`LocationChanged/SizeChanged` → 400ms 节流 `PersistBounds()` 写 `window_pos`（`[left,top,width,height]`，SaveQuiet）。
- 设置导航：`NavigateToSettings(section?)`（构造 SettingsPage → 记 `_settingsPrevTag` → `SelectTab("settings")` → `ShowSection`）、
  `ReturnFromSettings()`（回到进入前页签）、`SettingsPageInstance`（惰性单例）。桌宠/聊天窗右键「设置」都走这里（非浮窗）。

---

## §5 四大主面板 + 侧边栏 + 对话框

| 文件 | 职责 | 关键逻辑 |
|---|---|---|
| `Tabs/PlanTab` | 今日计划 | 顶部计划名标题 + 每日任务列表（TaskItemControl）+ 底部添加/清理已完成。按 `active_plan` 过滤临时任务，但**保留原数组索引**给 SetTaskDone/DeleteTask。`EnsureToday` 跨日归档 |
| `Controls/TaskItemControl` | 任务行 | 勾选→完成动效（80ms 缩放）→**动画完才 Save→重建划线行**（回调查勾选态防竞态）；右侧 ✎编辑/✕删除/右键菜单 |
| `Tabs/TimerTab` | 专注计时 | 正计时（44px 大字）、开始/暂停/重置、喝水提醒（计时不停）、今日专注统计卡、跳统计页；**右侧计划选择面板**——「不分类」置顶 + 全部**长期计划（固定任务）**，默认「不分类」（`Border.MouseLeftButtonDown` 防重入；当前目标被删/改名 → 回退「不分类」）；右侧每行显示当日该目标已专注分钟 |
| `Services/FocusTimerService` | 计时核心 | 注入 `Func<long> clock`（生产=TickCount64）可单测；**忽略 >2000ms 空隙**（休眠不算）；整秒 `AddFocusSeconds` 各自 `Math.Round(…,3)`（镜像 Python 逐秒 round，2s=0.034 这种）；≥15s SaveQuiet 节流；只发 `HistoryChanged`；`CurrentPlan` 属性（null=不指定，由 App/TimerTab 显式赋值，服务层不读计划列表）→ 每次落盘双写总/分时长 |
| `Tabs/StatsTab` | 专注统计 | **日/周切换**（SegmentedButtonStyle，Click 防重入）。整区统计卡放 `FocusGallery`（**鼠标聚焦缩放**：指针压着哪张卡，哪张就最高最亮/其余随「与焦点卡间距」高斯衰减变矮变淡，逐帧缓动；**有界滚动**，首末到头即停、不无限循环；滚轮/拖拽驱动，指针在内层可溢出列表上时滚轮让给它）。日视图 4 卡 = `HistogramControl` 24h 直方图 + 单日摘要 + **各计划分布卡**（色点+名称+时长+占比条+%，`未分类=总−已标记`灰显）+ 近 10 天卡，◀/▶ 翻历史日。周视图（**本周一 00:00 至今**）2 卡 = 周摘要 + 本周各计划**堆叠比例条**+图例。颜色走 `PlanPalette.BrushKeyFor`（按**固定任务**顺序稳定、Chart1..6 回绕、已删除任务/不分类→灰） |
| `Tabs/ReminderTab` | 提醒 | `TimeBox` HH:mm 掩码 + `ReminderService`（MatchingAt/NextEnabled/TryParseHm/PendingToday）+ 未完成任务提醒配置 |
| `Views/PlanSidebar` | 计划导航 | Notion 式：列出全部计划、点击切换、＋新建、右键重命名/删除。数据 `plans`/`active_plan` **惰性键**，任务可选 `plan` 字段，`EffectivePlan(task)`=显式优先否则 `plans[0]` |
| `Dialogs/ReminderPopupWindow` | 提醒弹窗 | 无激活（WS_EX_NOACTIVATE）右下角，10s 自关，多弹窗栈式堆叠。**动画打在内容根上，绝不能打 Window.RenderTransform** |
| `Dialogs/FixedTaskDialog` / `PlanNameDialog` | 表单弹窗 | 新建固定任务 / 计划改名 |

---

## §6 桌宠子系统（Pet / 形象系统）

### 6.1 PetWindow（`Views/Pet/PetWindow.xaml.cs`，约 800 行）

桌宠窗口（透明、置顶、单击拖动）。**状态机**：`normal/talking/happy/present/sleep`，一次性动画
（happy/present）播完回 `BaseAnim()`（输入框聚焦→talking，闲置>5min→sleep，否则 normal）。
`_animTimer` 按帧推进；`RefreshStatus` 1s 刷新头顶状态条（任务进度 + 专注时长 + 冻结/欠卡）。

- **动画加载 `LoadClips()`**：读 `pet_skin` → `PetSkinService.SkinDirFor` → `ResolveClipFiles` →
  5 个 kind 逐候选解析；sleep 缺省复用 normal；4 核心缺一 → 整组退回蓝色圆球（`AniLoader.MakeFallbackClip`）。
  帧是冻结的 BitmapSource（跨线程安全）。`ReloadSkin()` = 重载 + 复位状态机（设置页换皮肤后调用）。
- **对话**：底部常驻输入框（可 Ctrl+V 粘贴图片→b64）→ `SendChat`：本地指令（`PetCommandService`）优先，
  否则 AI 闲聊（`PetChatService`），有图走视觉模型；`_tts_speak` 顺带播报。回复 `ReactToReply` 触发 present/happy。
- **气泡** `PetBubbleWindow`：Q弹 BackEase 动画、10s 自关。
- **右键菜单**：回主窗 / 设置(`NavigateToSettings("general")`) / 聊天记录 / 语音播报开关+设置(`"tts"`) /
  实时字幕开关+设置(`"caption"`) / 开机自启 / 退出。**语音播报/字幕设置已迁进设置页，不再弹窗**。
- **字幕**：`CaptionService` 后台采集识别（事件在后台线程 → 必须 `Dispatcher.InvokeAsync` 封回）→
  `_blackboard`（BlackboardWindow）滚动显示；字幕到达时进 talking 表情，停 1.6s 回常态。
  `ApplyCaptionSettings()`（public，设置页每次提交调用）刷新黑板语言/外观/定位。
- **闲话** `IdleSpeak`：按 `pet_idle` 配置间隔主动冒泡（默认 8 分钟）；优先说欠卡/未完成，偶尔 AI 生成，兜底本地话术。
- **TTS**：`TtsService` 合成 WAV → MediaPlayer 播放；`PlayRequested` 在后台线程触发，UI 侧必须封回。

### 6.2 PetSkinService — 桌宠形象系统（`Services/PetSkinService.cs`，纯 C# 可单测）

**文件夹契约**（用户往 `desk_pet/` 放一个子文件夹就是一个形象）：

```
desk_pet/
├── normal_1.ani …               ← 默认艾莲（顶层文件，不动）
├── 小蓝/                        ← 每个子文件夹 = 一个形象
│   ├── normal.ani   (或 normal_1.ani)    常态（必需）
│   ├── talking.ani  (或 talking_1.ani)   说话（必需）
│   ├── happy.ani    (或 happy_1.ani)     开心（必需）
│   ├── present.ani  (或 present_1.ani)   完成（必需）
│   ├── sleep.ani    (或 alternate.ani)   睡觉（可选，缺省用常态）
│   └── preview.png                       设置预览图（可选，缺省用常态第一帧）
```

- `KindCandidates`：kind → 候选文件名数组（**规范名在前、遗留别名在后**）：normal→[normal.ani, normal_1.ani]、
  talking→[talking.ani, talking_1.ani]、happy→[happy.ani, happy_1.ani]、present→[present.ani, present_1.ani]、sleep→[sleep.ani, alternate.ani]。
- `ResolveClipFiles(skinDir)` 逐 kind 取**第一个存在的候选**；`IsValidSkinFolder` = 4 核心全在。
- `SkinDirFor(root, skinName)`：空串→root（默认）；否则 root/name，**防路径穿越**（`/`、`\`、`.`、`..` 拒绝）。
- `ListSkins(root)`：默认条目在最前，子目录按名排序，跳过 `.` 开头。
- `ImportSkin(sourceDir, root, out error)`：校验 4 核心 → `SanitizeSkinName`（非法字符→`_`、Windows 设备名加 `_` 前缀、去首尾点空格、上限 40）→
  复制为**规范文件名**；同名自动 `_2/_3`（绝不覆盖）；重导同文件夹 no-op。
- 数据键：`pet_skin`（**惰性**，见 §0.3）。设置页 `SelectSkin(name)`：空名→`Remove("pet_skin")`；否则写；`SaveQuiet` + `PetWindow.ReloadSkin()`。
- csproj 已把 `desk_pet\**\*.ani` + `**\*.png` 递归分发（`%(RecursiveDir)` 保子目录，`ExcludeFromSingleFile="true"` 保实体文件）。

### 6.3 语音线路生命周期（「线路占用」根因与治理，`Services/TtsService.cs` + `Native/ChildProcessJob.cs`）

- **进程树形态**：`server_cmd` 经 `cmd.exe /c …` 拉起，实际是 `cmd → venv python → Anaconda python` 三代，
  最终监听 `127.0.0.1:9880` 并占显存。正常退出走 `App.OnExit → PetWindow.ShutdownTts → TtsService.Dispose`；
  但**任务管理器强杀 / Stop-Process / 崩溃时托管 OnExit 不执行**，旧版本会留下三代孤儿继续占端口和显存，
  下次启动撞到自己的旧孤儿 → 气泡报「线路占用」。
- **Job Object 随父同死（核心修复）**：`Native/ChildProcessJob.cs` 用 Win32 Job Object
  （`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE=0x2000`）把拉起的 cmd `AssignProcessToJobObject` 进去；
  父进程无论怎么死，OS 关闭最后一个 job 句柄时会**自动杀光作业内所有子孙**（cmd/两层 python 全陪葬），
  端口与显存即时释放。嵌套作业（本进程已在外层 job，如被沙箱/IDE 拉起）Assign 失败时静默降级，不影响主流程。
  ⚠ 结构体必须用 `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`（x64 144 字节），`Affinity` 是 `UIntPtr`(8B)，
  误写成 uint 会让长度变 136 → `SetInformationJobObject` 静默返回 false（已踩过）。
- **合成串行化**：`SemaphoreSlim _synthGate` 串行 `SynthesizeAsync`（单卡 GPU 并发 /tts 会互相挤压）；
  `_synthCts` 在 `SetEnabled(false)`/`Dispose` 时取消在途合成，`SetEnabled(true)` 换新 CTS，
  HTTP/写文件全程传 token，`OperationCanceledException` 静默。
- **重启竞态**：`KillProc` 用 `Kill(entireProcessTree:true)` 后 `WaitForExit(3000)`，避免旧进程没退净就重拉。
- **CaptionService 不拉起外部进程**：VadLoop/TransLoop 都是 `while(_running && gen==_gen)` + 1s TryTake 超时，
  Stop 置标志 + StopCapture + 投哨兵，两个后台线程 1s 内退出并 Dispose `WasapiLoopbackCapture`，关闭链健壮。
- **重定向 stdout/stderr 必须排空**：GSVI 加载日志量大，匿名管道约 4KB 缓冲一满子进程就阻塞、永远起不来；
  故 `BeginOutputReadLine/BeginErrorReadLine` 空回调持续读掉丢弃。

---

## §7 设置页（整页非浮窗，立即生效）

**进入方式**：主窗底部 ⚙ / 桌宠右键「设置/语音/字幕」/ 聊天记录窗 ⚙ → `MainWindow.NavigateToSettings(section)`。
**布局**：页头（「← 返回」+ 标题）+ 左侧 200px 分栏导航（6 个 RadioButton，`SettingsNavItemStyle`=文字行，
选中=浅蓝底+强调色字）+ GridSplitter + 右侧 `sectionHost`（ContentControl）。
**动画**：`ShowSection(tag)` 复刻 `SelectTab` 的 160ms 淡入+上移；`ISettingsSection.Refresh()`（PetTab 重扫皮肤）。
**模型**：**所有修改立即生效**——开关/下拉/日期一改即写盘，文本框 LostFocus/回车提交，无保存按钮，页头只有返回。
**防事件风暴**：每个分栏 ctor 先 `_loading=true` → 填控件 → `_loading=false` → 再挂事件；处理器 `_loading` 时早退。

| 分栏 | 内容 | 落盘逻辑 |
|---|---|---|
| `SettingsGeneralTab` | 考研日期 DatePicker（写 `exam_date` yyyy-MM-dd）、窗口置顶（`window_topmost`+`host.ApplyTopmost`）、开机自启（`AutostartService.SetEnabled`，失败回滚+MessageBox）、降低动效（`reduce_motion`） |
| `SettingsPetTab` | **皮肤卡片列表**（120×120 预览：preview.png 或常态第一帧；选中=Accent 2px 边框；无效置灰）＋「导入皮肤文件夹…」（FolderBrowserDialog）＋「打开皮肤文件夹」（Process.Start）＋**拖放导入区**（DragOver 设 `Effects=Copy`+`Handled=true`，Drop 取 FileDrop 目录）＋ pet_idle 开关/间隔（clamp ≥2） | 见 §6.2 |
| `SettingsChatTab` | pet_chat.enabled + base_url/model/api_key（LostFocus/Enter 提交，SaveQuiet） |
| `SettingsTtsTab` | tts.server_cmd/ref_audio_path/prompt_text/prompt_lang（空→"zh"），LostFocus/Enter 提交 |
| `SettingsCaptionTab` | asr_api_key（PasswordBox→`SecretService.Set`）+ 模型/语言 ComboBox + 字号（clamp 12-40）+ 5 色板 + 自定义 Forms.ColorDialog；提交后 `host.PetWindow?.ApplyCaptionSettings()` |
| `SettingsAboutTab` | 版本、数据目录、**添加形象教程**（SectionHeaderStyle 是 Border 样式，须包 Border 用） |

---

## §8 主题 / 样式 / WinForms 命名冲突（改 XAML 和 cs 前必读）

- **主题资源**（`Theme/`，`App.xaml` 合并）：`Colors.xaml`（骨白+蓝 #339CFF 全套 Brushes + PopupShadowEffect/CardShadowEffect 两档阴影）、
  `Typography.xaml`（TextBlock 系样式：BodyText/MetadataText/MutedText/TitleText/PageTitleText/BigTimerText）、
  `Styles.xaml`（Button 系/Pill/卡/进度条/CheckBox/ScrollBar/RailButtonStyle/**CardStyle(TargetType=Border)**/**SectionHeaderStyle(TargetType=Border)**，
  以及**隐式 ContextMenu/MenuItem/Separator/ComboBox/ComboBoxItem/ToolTip**——右键菜单和下拉框已自绘，不再是系统原生样式）、
  `Animations.xaml`。
- **全局动效体系（Codex 风丝滑统一，改控件前必读）**：所有交互态走「叠层 Border 的 Opacity 淡入淡出」，
  **不要用 ColorAnimation 动画共享 SolidColorBrush**（一个控件变色会污染所有引用该画刷的控件）。统一刻度：
  圆角——控件 8 / 卡片·对话框 12 / 菜单·下拉弹层 10 / 胶囊 Pill 8（方形圆角，全圆已废弃）/ 进度·勾选等微元素 3~5；
  时长——hover 120~130ms、exit 100~110ms、press 缩放 90ms（ScaleTransform 0.96~0.97，回弹 120ms）、
  页面转场 180ms/8px；缓动一律 `CubicEase EaseOut`，图标钮回弹用 `BackEase`。
  代码建的 UI 元素入场用 `Controls/UiMotion.cs`（FadeScaleIn/FadeSlideUp/TweenColor；TweenColor 只补间**自有** SolidColorBrush，
  同样禁止动画共享画刷）。一切动画都要判 `SystemParameters.ClientAreaAnimation && !ReduceMotion` 降级（reduce_motion 设置项）。
  窗口级圆角用 `DwmInterop.ApplyRoundedCorners`，Hide（非 Close）复用的窗（如 ChatWindow）在 IsVisibleChanged 重播入场。
- **Codex 风格字体系统（2026-09-08 起）**：`Typography.xaml` 定义 `IconFont`（**单字体 `Segoe MDL2 Assets`，禁止复合字体链**——WPF 对 PUA 码点
  E000–F8FF 不做字符级逐字形回退，回退链会在 rail/窗控/日历钮/步进器上整链渲染成空白或方块）与
  `UiFontWeight`（默认 SemiBold 偏粗）。隐式 TextBlock/BodyText/MetadataText/MutedText 与 Styles.xaml 全套控件
  Setter 都绑 `{DynamicResource UiFontWeight}`，`App.ApplyUiStyle(bool)` 改 Application 级资源即可全站即时切换字重
  （设置-通用「外观 / Codex 风格字体（偏粗体）」写 `ui.codex_font`，默认开）。**蓝底（Accent）框内文字永远用白色**，
  白底框内黑色——此原则不随开关变化；新增蓝底 Pill/横幅时按此处理。
- **图标一律走 IconFont + MDL2 码点**（&#xE7C3; 等），**禁止在 XAML/CS 里用 emoji 字符**（Segoe UI 缺 glyph 渲染豆腐块）。
  **注意**：`<Button Content="&#xE7C3;"/>` 这类实体字符 Content 会被隐式 TextBlock 样式（FontFamily=UIFont）覆盖，渲染空白；
  图标必须写成 `<Button><TextBlock Text="&#xE7C3;" FontFamily="{StaticResource IconFont}" FontSize="N"/></Button>`（TextBlock 本地值优先）。
  文案装饰用「·」「—」等安全符号。
- **自绘控件速查**：`Controls/NumberStepper`（−/＋ 步进器，Min/Max/Step/ValueChanged，替代「每[N]分钟」裸输入框）；
  `Controls/ToastHost`（全局单例右下角 Toast，带「撤销」回调，删除任务用）；`Controls/UiMotion`（代码建 UI 入场动画）；
  DatePicker 系统控件已在 Styles.xaml 末尾整体重绘（温白底 r8、日历 Popup r10、MDL2 E787 图标、focus 蓝环）。
  `MainWindow` 内置 **Ctrl+K 命令面板**（cmdPopup/cmdList，命令集在 `BuildCommands()`，切页/建任务/冻结/清理/桌宠）。
  `SettingsChatTab` API Key 用 PasswordBox+「显示密钥」切换；`SettingsTtsTab` 有服务状态灯（TCP 127.0.0.1:9880 探测）+试听
  （`TtsService.Preview`）+参考音频浏览；`SettingsPetTab` 桌宠个性化（大小/不透明度滑块，写 `pet_ui`，`PetWindow.ApplyAppearance`）。
- **WinForms 冲突是最大坑**：`UseWindowsForms=true` 让 SDK 全局导入 System.Windows.Forms/System.Drawing，
  与 WPF 同名类型冲突（UserControl/TextBox/Application/ComboBox/Point/Brushes/Orientation/DataObject/Image…）。
  `GlobalUsings.cs` 已用 `global using X = System.Windows.X;` 统一指向 WPF 版；需要 WinForms 类型（NotifyIcon/ColorDialog/FolderBrowserDialog）时文件里 `using Forms = System.Windows.Forms;`。
  **全局别名没覆盖的**（Cursor/Cursors/ColorConverter/Pen/DragDropEffects/RadioButton/DragEventArgs 等）在文件里写文件级别名，
  若同时有全局同名别名会 CS1537。
- **XAML 的样式 TargetType 坑**：`dotnet build` 不校验样式目标类型，`{StaticResource SectionHeaderStyle}` 挂到
  TextBlock 上编译照过、**运行到该页才崩**（XamlParseException）。改 XAML 时对照：TextBlock 用 Typography 系，
  Border 用 CardStyle/SectionHeaderStyle/EmptyStateCardStyle（RadioButton 可挂 ToggleButton 样式，子类合法）。
  **新增视图页务必跑 SettingsPageSmokeTests 式 STA 冒烟**（见 §9）。

---

## §9 构建 / 测试 / 发布 / 冒烟

```bash
# 构建（0 警 0 错）
cd KaoyanPlanner.WPF
dotnet build KaoyanPlanner.WPF/KaoyanPlanner.WPF.csproj

# 测试（当前 139 个）
dotnet test KaoyanPlanner.WPF.Tests/KaoyanPlanner.WPF.Tests.csproj

# 发布（-o 相对调用时 cwd 解析！必须绝对路径）
dotnet publish KaoyanPlanner.WPF/KaoyanPlanner.WPF.csproj -c Release \
  -o /c/Users/21495/Desktop/Coding/kaoyan_planner/dist-wpf
```

**测试约定**：`KaoyanPlanner.WPF.Tests/`，xUnit。
- 服务层纯逻辑测试：DataStoreTests（含**真数据字节往返** `RoundTrip_RealData_BytesIdentical`、惰性键 `PetSkin_LazyWriteThenRemove_BytesIdentical`）、
  PetSkinServiceTests（22 个：ListSkins/ResolveClipFiles/IsValidSkinFolder/ImportSkin 同名 _2/重导 no-op/SanitizeSkinName Theory）、
  FocusTimerServiceTests（假时钟）、ReminderServiceTests、PetCommandServiceTests、PlanTests、AniClipTests。
- **SettingsPageSmokeTests**：STA 线程 `new Application` + 合并 4 个 Theme/*.xaml（pack URI
  `pack://application:,,,/PetPlanner;component/Theme/X.xaml`）→ 逐个 `new` 各分栏 + `SettingsPage.ShowSection` 逐栏切。
  专门抓「编译不报、加载才崩」的 XAML 问题。**新增设置分栏/视图页后必跑**。
- 冒烟（发布后）：`tasklist | grep -i PetPlanner` 进程存活；`md5sum dist-wpf/data.json` 启动前后字节不动
  （专注中会每 15s 写 focus_history——今天的秒数在涨是**正常**，不是损坏）；事件日志无 .NET Runtime 1000/1026 崩记录
  （`Get-WinEvent -LogName Application`）。
- 桌面宠物出现在屏幕右下角、设置⚙就地转场、切皮肤活体换装 → 人工核验项。

---

## §10 高频坑速查（全量在会话 memory `wpf-rewrite-pitfalls`）

1. `dotnet publish -o` 相对调用时 cwd 解析（不是项目目录）→ 一律绝对路径。
2. 单文件发布 + CopyToOutputDirectory 资源（data.json/desk_pet）默认打进 exe 内部 → 加 `ExcludeFromSingleFile="true"` 才是实体文件。
3. 运行中的应用会覆盖 dist-wpf/data.json 的手动编辑 → 先杀进程再改。
4. 进程名是 **PetPlanner**，不是 KaoyanPlanner.WPF。
5. TTS/字幕服务事件在后台线程触发 → UI 必须 `Dispatcher.InvokeAsync` 封回，否则跨线程崩进程。
6. `Window.RenderTransform` 只能 Identity，窗口级动画必须打在内容根元素上（否则 InvalidOperationException 崩进程）。
7. 自绘窗（黑板/聊天窗）拖拽/缩放要用 `PointToScreen` 屏幕坐标，别用 GetPosition(this)（坐标系随移动漂移→抖动）。
8. AI/ASR 模型会「下线」：回退链必须无条件多模型（**任意 HTTP 错误 AND 网络/超时异常都 continue**），别写死单点。
   坑（2026-09-08 修）：PetChatService 此前对网络/超时直接 throw，单点抖动就让整条链掉进本地话术；现在网络错误与 HTTP 错误一样换下一个模型，退避 400~600ms，主模型排兜底前。
9. 惰性键（plans/active_plan/pet_skin/archive/focus_plan）不进 CreateDefaults。
10. `_todayPunch` 只存内存绝不落盘；`DailyTasks()` 返回副本，删任务要改真实 `daily[day]` 数组。
11. 数据文件 CRLF：手动编辑（尤其 Python）后必须还原 `\r\n`，否则字节往返测试挂。
12. **版本隔离（2026-09-08 起）**：同一源码两版身份——个人版（默认，艾莲）⇄ 安装包测试版（`-p:PublicNeutral=true -p:TestBuild=true`，小蓝）。身份全部走 `AppInfo.cs`：单实例管道、自启注册表值、关于页、托盘名按版本区分；安装器 `KillApp` 只杀**安装目录内**的 PetPlanner 进程（绝不误杀个人版）。两版可同时运行、数据各自独立。改身份相关代码先看 AppInfo，别硬编码 `PetPlanner_SingleInstance`/`PetPlanner` 值名。
13. **语音孤儿 / 线路占用（2026-09-08 修）**：强杀主程序后 GSVI 三代进程会成孤儿占 9880+显存 → 已用 Job Object KILL_ON_JOB_CLOSE 随父同死（见 §6.3）。排查端口：`Get-NetTCPConnection -LocalPort 9880 -State Listen` + `Get-CimInstance Win32_Process`（看 ParentProcessId/CommandLine）。
14. **代理/沙箱 shell 的 PYTHONPATH 会污染子进程 Python**：从带 `PYTHONPATH`/conda 的终端启动本程序时，GSVI 子进程继承后可能 `ModuleNotFoundError: fastapi` 秒退；从资源管理器/开机自启（干净环境）启动则正常。冒烟拉起失败先排查启动环境，别误判成 Job Object 问题。

---

*最后更新：2026-09-08 第三轮——①Codex 风格字体系统（UiFontWeight 动态字重 + 蓝底白字原则 + 设置开关，§8）、
②全站 emoji→IconFont MDL2（根治豆腐块）、全圆胶囊→方形圆角 8、DatePicker 自绘、API Key 密码框、TTS 设置页状态灯/试听、
桌宠个性化滑块、NumberStepper 步进器（喝水/闲话间隔）、ToastHost 撤销删除、Ctrl+K 命令面板、统计页增强（22-24/刻度/空态）；
③版本隔离+语音线路 Job Object（前轮）。应用版本 2.0.1，测试 139 个。改大模块前建议同步更新本导读与 memory。*
