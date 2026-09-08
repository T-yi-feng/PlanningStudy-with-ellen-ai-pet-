#!/usr/bin/env bash
# ============================================================================
# PetPlanner 公共分发版 一键打包
#
#  产出：仓库根 / PetPlanner-Setup.exe（win-x64 自包含安装向导，含中性化应用本体）
#
# 铁律：
#  - 只写 packaging/_stage/ 与各工程 bin/obj；绝不写 dist-wpf/ 或仓库根 data.json（活数据）。
#  - 发布 -o 一律绝对路径（AI_GUIDE §10.1：-o 相对调用时 cwd 解析，不是项目目录）。
#  - 中性化：去掉 desk_pet 艾莲素材、secret、个人 data.json → 换 packaging/clean-data.json。
#
# 用法（在仓库根）：bash packaging/build_release.sh
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACK="$ROOT/packaging"
APP="$ROOT/KaoyanPlanner.WPF/KaoyanPlanner.WPF/KaoyanPlanner.WPF.csproj"
SETUP="$PACK/PetPlanner.Setup/PetPlanner.Setup.csproj"
STAGE="$PACK/_stage/app"
SETUP_OUT="$PACK/_stage/setup"

echo "==> 0) 清中间目录（$STAGE / $SETUP_OUT）"
rm -rf "$PACK/_stage"
mkdir -p "$STAGE" "$SETUP_OUT"

echo "==> 1) 应用本体：win-x64 自包含单文件 → stage（PublicNeutral=true → 文案走 Brand『小蓝』中性版；TestBuild=true → 版本身份走『测试版』，与个人版隔离）"
dotnet publish "$APP" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -p:PublicNeutral=true -p:TestBuild=true -o "$STAGE"

echo "==> 2) 中性化清理 + 白名单断言"
rm -rf "$STAGE/desk_pet"                     # 艾莲动画（整目录删，含可能的 *.psd）
rm -f "$STAGE"/*.pdb "$STAGE"/*.runtimeconfig.json "$STAGE"/*.deps.json
rm -f "$STAGE"/data.json* "$STAGE"/secret.json "$STAGE"/*.tmp
cp "$PACK/clean-data.json" "$STAGE/data.json"   # 换成中性模板（浅合并：显式覆盖 pet_chat/pet_idle）
mkdir -p "$STAGE/desk_pet"                   # 空目录：让公共用户「打开皮肤文件夹 / 拖放导入」可用
# 允许：PetPlanner.exe + data.json + 空 desk_pet/ + WPF 单文件旁置的 5 个原生 *cor3.dll。
if ls -A "$STAGE" | grep -vxE 'PetPlanner\.exe|data\.json|desk_pet|.*_cor3\.dll' >/dev/null; then
  echo "!! stage 有意外残留文件（含个人内容？）:"; ls -A "$STAGE"; exit 1
fi
echo "    stage 内容："; ls -A "$STAGE"

echo "==> 3) 安装向导：清 bin/obj（防 150MB payload 增量被跳过）→ 发布"
rm -rf "$PACK/PetPlanner.Setup/bin" "$PACK/PetPlanner.Setup/obj"
dotnet publish "$SETUP" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=None -o "$SETUP_OUT"

echo "==> 4) 拷交付物到仓库根"
cp "$SETUP_OUT/PetPlanner.Setup.exe" "$ROOT/PetPlanner-Setup.exe"
ls -lh "$ROOT/PetPlanner-Setup.exe"
echo "==> OK：仓库根已生成 PetPlanner-Setup.exe"
