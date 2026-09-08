# packaging/ — PetPlanner 公共分发打包

给别的同学分发用的「中性化公共版」安装包。**不改变**你个人日常用的 `dist-wpf/` 与活数据
`dist-wpf/data.json`。

## 版本隔离（个人版 ⇄ 安装包测试版）

同一份源码产出两个版本，**身份完全隔离，可同时运行**：

| 维度 | 个人版（默认编译） | 安装包测试版（`build_release.sh`） |
|---|---|---|
| 桌宠 / 文案 | 艾莲（Brand 默认） | 小蓝（`PublicNeutral=true`） |
| 版本身份 | 个人版（`AppInfo`） | 测试版（`TestBuild=true` → `TEST_BUILD`） |
| 单实例管道 | `PetPlanner_Personal_SingleInstance` | `PetPlanner_Test_SingleInstance` |
| 开机自启值 | `PetPlannerPersonal` | `PetPlannerTest` |
| 关于页 / 托盘 | 显示「个人版」 | 显示「测试版」 |
| 数据目录 | exe 所在目录（各跑各的） | 安装目录（`%LOCALAPPDATA%\Programs\PetPlanner`） |

隔离效果：两版进程名都叫 `PetPlanner.exe`，但可**同时运行**、数据互不干扰；
卸载/重装测试版**只杀安装目录内的进程**，不会误杀正在跑的个人版；开机自启互不覆盖。

## 交付物
- `PetPlanner-Setup.exe`（仓库根）—— win-x64 自包含安装向导，双击 → 选安装目录 → 快捷方式 → 即用。
  对方电脑**无需安装 .NET 8**（应用本体与向导都内嵌了运行库）。
- 包内应用是**中性化公共版（测试版身份）**：无「艾莲」桌宠素材（新用户看到中性圆球兜底形象）、无个人 API key
  （secret.json）、无个人学习记录、AI 聊天 / 实时字幕 / 语音播报默认关闭（使用者可自行填 key/模型开启）。

## 一键重打
```bash
cd <仓库根>
bash packaging/build_release.sh
```
流程：`dotnet publish` 应用（win-x64 自包含单文件）→ 进 `packaging/_stage/app` → 剥掉
`desk_pet/**`(艾莲)、`.pdb`、`secret.json`、个人 `data.json`，换成 `clean-data.json` → 再 `dotnet publish`
安装向导（把清理后的 `PetPlanner.exe` + `data.json` 内嵌）→ 拷到仓库根 `PetPlanner-Setup.exe`。

要求：本机有 .NET 8 SDK；`Microsoft.WindowsDesktop.App.Runtime.win-x64` 运行库包已在 NuGet 缓存
（可离线自包含发布）。

## 需要「换版本重打」时
若改了应用源码，直接重跑 `build_release.sh` 即可（从当前源码重发，产物更新）。

若改了中立默认值/默认关闭项：编辑 `clean-data.json`（键结构须与
`KaoyanPlanner.WPF/KaoyanPlanner.WPF/Services/DataStore.cs` 的 `CreateDefaults()` 对齐——浅合并，
要覆盖的整块对象须写全，否则代码默认会站住脚；文件行尾保持 CRLF）。

## 安装 / 卸载
- 安装：双击 `PetPlanner-Setup.exe` → 选目录（默认 `%LOCALAPPDATA%\Programs\PetPlanner`，可写）→
  ☑桌面/开始菜单快捷方式 → 安装后可选启动。
- 卸载：再次运行 `PetPlanner-Setup.exe`，对已安装目录会显示「卸载」→ 清理快捷方式与安装目录
  （卸载前会先结束运行中的 PetPlanner）。

## 冒烟清单（每次重打后至少做前 3 条）
1. `packaging/_stage/app/` 恰为 `PetPlanner.exe` + `data.json` + 空 `desk_pet/`，无 ani/pdb/secret/个人记录。
2. 把 stage 整目录拷到临时目录单独运行 `PetPlanner.exe`，存活 6s+，Windows 事件日志无 .NET Runtime 1000/1026；
   `data.json` md5 启动前后不动（首次启动不人为写盘）。
3. 跑仓库根 `PetPlanner-Setup.exe` 选临时目录安装：目录文件齐全、`data.json` 与 `clean-data.json` 一致、
   `desk_pet/` 为空目录、快捷方式生成、勾「安装后启动」则进程存活。
4. 二次运行 Setup → 卸载 → 目录与快捷方式清空。
5. 隐私终检：对最终 exe `strings` 扫 `21495` / `C:\Users` / `gsvi_venv` 无命中；
   且 `艾莲` / `绝区零` 在 exe 的 UTF-8 与 UTF-16 两种编码下均为 0 命中（`小蓝` 出现属正常）。
