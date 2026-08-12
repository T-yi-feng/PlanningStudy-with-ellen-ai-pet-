"""路径解析：区分「用户数据」与「只读资源」两种目录，兼容源码运行与 PyInstaller 打包。

- 源码运行：两者都是项目目录（data.json / secret.json 在项目里，desk_pet 动画也在项目里）。
- PyInstaller 单文件（onefile）冻结后：
  * 用户数据 → exe 所在目录（随 exe 移动，便携）；
  * 只读资源 → 临时解压目录 sys._MEIPASS（每次启动解压到 %TEMP%，退出即删，
    所以可写文件绝不能放这里，否则重启丢失）。
"""
import os
import sys


def base_dir():
    """用户数据目录：exe 所在目录（冻结）或项目目录（源码）。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


def resource_dir():
    """只读资源目录：_MEIPASS（冻结）或项目目录（源码）。"""
    return getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))


def data_path(rel):
    """可写数据文件的完整路径（如 data.json、secret.json）。"""
    return os.path.join(base_dir(), rel)


def resource_path(rel):
    """只读资源文件的完整路径（如 desk_pet 动画）。"""
    return os.path.join(resource_dir(), rel)
