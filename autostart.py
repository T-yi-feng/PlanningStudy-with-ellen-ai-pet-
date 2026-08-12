"""开机自启管理：通过 Windows 注册表 Run 键实现（HKCU，无需管理员权限）。

Windows 登录时会把 Run 键下的每条命令跑一遍。用本项目自己的值名
（APP_NAME）管理，与用户手动在 shell:startup 放的快捷方式互不冲突。
"""
import os
import re
import sys
import winreg

# HKCU\Software\Microsoft\Windows\CurrentVersion\Run
_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
APP_NAME = "KaoyanPlanner"   # Run 键里的值名（本项目唯一标识）


def _run_command():
    """生成 Run 键里存放的启动命令。

    - 打包成 exe（冻结）：直接指向 exe 自身路径，无 Python 环境依赖。
    - 源码运行："pythonw.exe" "C:\\...\\main.py"（pythonw 启动无黑色控制台窗口；
      若本机没有 pythonw 则退回当前解释器）。
    """
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    python_dir = os.path.dirname(sys.executable)
    pythonw = os.path.join(python_dir, "pythonw.exe")
    interpreter = pythonw if os.path.exists(pythonw) else sys.executable
    main_py = os.path.join(os.path.dirname(os.path.abspath(__file__)), "main.py")
    return f'"{interpreter}" "{main_py}"'


def _stored_target(value):
    """从 Run 键的启动命令里取出「程序本体」路径：
    源码 = main.py；打包 = exe。取不到返回 None。

    兼容带引号/不带引号两种写法（如 '"pythonw.exe" "C:\\...\\main.py"' 或
    '"C:\\...\\KaoyanPlanner.exe"'）。
    """
    for token in re.findall(r'"([^"]*)"', value):
        if token.lower().endswith("main.py"):
            return token
    # 冻结（打包）模式：命令就是 exe 自身；取第一个确实存在的 .exe
    for token in re.findall(r'"([^"]*)"', value):
        if token.lower().endswith(".exe") and os.path.exists(token):
            return token
    return None


def _current_target():
    """当前程序本体：源码 = 项目里 main.py；打包 = exe。"""
    if getattr(sys, "frozen", False):
        return sys.executable
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "main.py")


def is_enabled():
    """自启是否真正生效：Run 键里有本项目条目，且指向的就是当前程序本体。

    条目存在但指向别的路径/已失效（项目被移动、exe 挪位、旧路径）→ 视为未开启，
    勾上会重写为正确路径。
    """
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, APP_NAME)
    except OSError:
        return False
    target = _stored_target(value)
    current = _current_target()
    return (
        target is not None
        and os.path.normcase(os.path.abspath(target))
        == os.path.normcase(os.path.abspath(current))
    )


def set_enabled(on):
    """开→写入自启命令；关→删除自启项。返回是否成功（失败=注册表不可写等）。"""
    try:
        if on:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, _RUN_KEY) as key:
                winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, _run_command())
        else:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER, _RUN_KEY, 0, winreg.KEY_SET_VALUE
            ) as key:
                try:
                    winreg.DeleteValue(key, APP_NAME)
                except FileNotFoundError:
                    pass  # 本来就没开：目标状态已达成，视为成功
        return True
    except OSError:
        return False
