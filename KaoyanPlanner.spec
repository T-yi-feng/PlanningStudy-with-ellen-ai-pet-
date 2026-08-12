# -*- mode: python ; coding: utf-8 -*-
# 构建：python -m PyInstaller --noconfirm KaoyanPlanner.spec
# 产物：dist/KaoyanPlanner.exe（单文件、无控制台、带图标）

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=[
        # 桌宠动画（只读资源，打包进 exe）
        ('desk_pet/normal_1.ani', 'desk_pet'),
        ('desk_pet/talking_1.ani', 'desk_pet'),
        ('desk_pet/happy_1.ani', 'desk_pet'),
        ('desk_pet/present_1.ani', 'desk_pet'),
        ('desk_pet/alternate.ani', 'desk_pet'),
    ],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='KaoyanPlanner',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    runtime_tmpdir=None,
    console=False,          # 无黑色控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='icon.ico',
)
