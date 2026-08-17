"""主题模块：检测 Windows 深浅色模式，提供两套「液态玻璃」QSS 并实时切换。

设计参考 mianbeishiwole/Liquid-Glass-Vue 的玻璃配方：
  - 背景是半透明的多彩「极光」渐变 —— 给磨砂玻璃一个可反射的色彩底层
    （配合 Win11 亚克力模糊时，桌面会被真模糊，透过极光露出磨砂质感）；
  - 每个前景框（卡片 / 按钮 / 输入框 / 分栏标题……）都是「玻璃」：
    低不透明度白色基底 + 斜向高光渐变 + 顶部 1px 亮边（反光）+ 底部暗边（厚度）
    + 1px 白色描边 + 大圆角。玻璃不是透明度为 0，而是「有内容可磨砂、有边缘可反光」。
亚克力不可用时自动回退到不透明极光，界面仍保持玻璃质感。
"""
import sys
from PyQt5.QtWidgets import QApplication

# 当前生效主题（'light' / 'dark'），供自定义绘制控件（如直方图）读取配色
current = "light"

# 亚克力模糊是否已成功开启（开启后窗口背景可用更透明的极光，露出桌面模糊）
acrylic_on = False


# ChatGPT / Notion-inspired finishing layer.  The original theme still supplies
# every niche widget rule; this layer deliberately flattens the visual language
# into a quiet workspace: neutral surfaces, hairline borders and one green
# action colour.  Keeping it as an overlay makes theme coverage resilient as
# new controls are added elsewhere in the app.
_WORKSPACE_LIGHT = """
QWidget { color: #242424; font-family: "Microsoft YaHei UI", "Segoe UI"; }
QWidget#float_window, QWidget#float_window_acrylic { border: 1px solid #E8E8E5; border-radius: 14px; }
QWidget#header { background: #FFFFFF; border: 1px solid #E9E9E7; border-radius: 10px; }
#card, #glass_popup, QFrame#pet_bubble, QFrame#pet_status, QFrame#pet_bar { background: #FFFFFF; border: 1px solid #E9E9E7; border-radius: 10px; }
QLabel#title { color: #191919; font-size: 16px; font-weight: 700; }
QLabel#subtitle, QLabel#status { color: #787774; }
QLabel#accent { color: #0F7B61; }
QLabel#pill, QLabel#pill_accent { border-radius: 6px; padding: 4px 8px; }
QLabel#pill { background: #F7F7F5; border: 1px solid #ECECE9; color: #787774; }
QLabel#pill_accent { background: #E7F3EE; border: 1px solid #C9E5D9; color: #0F7B61; }
QLabel#today_tag_done { background: #E7F3EE; border: 1px solid #C9E5D9; color: #0F7B61; border-radius: 6px; }
QLabel#today_tag_todo { background: #F7F7F5; border: 1px solid #E9E9E7; color: #787774; border-radius: 6px; }
QLabel#empty { background: #FAFAF9; border: 1px dashed #D9D9D5; border-radius: 10px; color: #9B9A97; }
QPushButton { background: #FFFFFF; border: 1px solid #E3E3E0; border-radius: 7px; padding: 5px 10px; color: #37352F; }
QPushButton:hover { background: #F7F7F5; border-color: #D4D4D0; }
QPushButton:pressed { background: #EFEFEC; }
QPushButton#primary { background: #10A37F; border: 1px solid #10A37F; border-radius: 7px; color: white; }
QPushButton#primary:hover { background: #0D8C6D; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button { background: #FFFFFF; border: 1px solid #E3E3E0; color: #4A4A46; }
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon { color: #787774; border-radius: 6px; }
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover { background: #F1F1EF; color: #37352F; }
QPushButton#danger_icon:hover, QPushButton#close_icon:hover { background: #FDECEC; color: #D44C47; }
QLineEdit, QTimeEdit, QSpinBox, QDateEdit { background: #FFFFFF; border: 1px solid #E3E3E0; border-radius: 7px; color: #37352F; selection-background-color: #10A37F; }
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus { border: 1px solid #10A37F; background: #FFFFFF; }
QTabBar::tab { background: transparent; border: none; color: #787774; padding: 8px 10px; margin: 0 2px; }
QTabBar::tab:first, QTabBar::tab:last { border-radius: 7px; }
QTabBar::tab:selected { background: #ECECEA; color: #242424; font-weight: 700; border: none; }
QTabBar::tab:hover { background: #F3F3F1; color: #37352F; }
QCheckBox::indicator { background: #FFFFFF; border: 1px solid #C9C9C5; border-radius: 4px; }
QCheckBox::indicator:checked { background: #10A37F; border-color: #10A37F; }
QProgressBar { background: #EDEDEA; border: none; border-radius: 4px; color: #37352F; }
QProgressBar::chunk { background: #10A37F; border-radius: 4px; }
QScrollBar::handle:vertical { background: #D5D5D1; border-radius: 4px; }
QScrollBar::handle:vertical:hover { background: #BDBDB8; }
QLabel#section_header { background: #F3F7F5; border: none; color: #587065; border-radius: 6px; }
QLabel#section_header_fixed { background: #E7F3EE; border: none; color: #0F7B61; border-radius: 6px; }
QLabel#stat_value, QLabel#day_total { background: #F7F7F5; border: 1px solid #E9E9E7; color: #37352F; }
QLabel#day_total_peak { background: #E7F3EE; border: 1px solid #C9E5D9; color: #0F7B61; }
QMenu, QToolTip { background: #FFFFFF; color: #37352F; border: 1px solid #E3E3E0; }
QMenu::item:selected { background: #F1F1EF; color: #242424; }
QDialog { background: #FAFAF9; border: 1px solid #E3E3E0; }
QWidget#chat_window { background: #FAFAF9; border: 1px solid #E3E3E0; border-radius: 12px; }
QLabel#chat_me { background: #10A37F; border: none; color: white; }
QLabel#chat_pet { background: #FFFFFF; border: 1px solid #E9E9E7; color: #37352F; }
QLineEdit#pet_input { background: #FFFFFF; border: 1px solid #E3E3E0; color: #37352F; }
"""

_WORKSPACE_DARK = """
QWidget { color: #ECECEC; font-family: "Microsoft YaHei UI", "Segoe UI"; }
QWidget#float_window, QWidget#float_window_acrylic { border: 1px solid #303030; border-radius: 14px; }
QWidget#header { background: #212121; border: 1px solid #303030; border-radius: 10px; }
#card, #glass_popup, QFrame#pet_bubble, QFrame#pet_status, QFrame#pet_bar { background: #212121; border: 1px solid #303030; border-radius: 10px; }
QLabel#title { color: #F5F5F5; } QLabel#subtitle, QLabel#status { color: #A6A6A6; } QLabel#accent { color: #36C9A5; }
QLabel#pill { background: #2A2A2A; border: 1px solid #383838; color: #B5B5B5; }
QLabel#pill_accent, QLabel#today_tag_done { background: #143E35; border: 1px solid #236A59; color: #6FE2C4; }
QLabel#today_tag_todo { background: #2A2A2A; border: 1px solid #383838; color: #B5B5B5; }
QLabel#empty { background: #202020; border: 1px dashed #424242; color: #8A8A8A; }
QPushButton { background: #2A2A2A; border: 1px solid #3A3A3A; border-radius: 7px; color: #ECECEC; }
QPushButton:hover { background: #343434; border-color: #4A4A4A; }
QPushButton#primary { background: #10A37F; border: 1px solid #10A37F; color: #FFFFFF; }
QPushButton#primary:hover { background: #0D8C6D; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button { background: #2A2A2A; border: 1px solid #3A3A3A; color: #ECECEC; }
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon { color: #A6A6A6; border-radius: 6px; }
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover { background: #343434; color: #FFFFFF; }
QPushButton#danger_icon:hover, QPushButton#close_icon:hover { background: #542C2C; color: #FFB4AB; }
QLineEdit, QTimeEdit, QSpinBox, QDateEdit, QLineEdit#pet_input { background: #2A2A2A; border: 1px solid #3A3A3A; border-radius: 7px; color: #ECECEC; selection-background-color: #10A37F; }
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus { border: 1px solid #10A37F; }
QTabBar::tab { background: transparent; border: none; color: #A6A6A6; padding: 8px 10px; margin: 0 2px; }
QTabBar::tab:first, QTabBar::tab:last { border-radius: 7px; }
QTabBar::tab:selected { background: #343434; color: #F5F5F5; font-weight: 700; border: none; }
QTabBar::tab:hover { background: #2C2C2C; color: #F5F5F5; }
QCheckBox::indicator { background: #2A2A2A; border: 1px solid #5A5A5A; border-radius: 4px; }
QCheckBox::indicator:checked { background: #10A37F; border-color: #10A37F; }
QProgressBar { background: #343434; border: none; border-radius: 4px; } QProgressBar::chunk { background: #10A37F; border-radius: 4px; }
QScrollBar::handle:vertical { background: #4A4A4A; border-radius: 4px; } QScrollBar::handle:vertical:hover { background: #626262; }
QLabel#section_header { background: #29332F; border: none; color: #B8CEC4; border-radius: 6px; }
QLabel#section_header_fixed { background: #143E35; border: none; color: #6FE2C4; border-radius: 6px; }
QLabel#stat_value, QLabel#day_total { background: #2A2A2A; border: 1px solid #383838; color: #ECECEC; }
QLabel#day_total_peak { background: #143E35; border: 1px solid #236A59; color: #6FE2C4; }
QMenu, QToolTip { background: #2A2A2A; color: #ECECEC; border: 1px solid #3A3A3A; } QMenu::item:selected { background: #343434; }
QDialog, QWidget#chat_window { background: #212121; border: 1px solid #303030; }
QLabel#chat_me { background: #10A37F; border: none; color: white; } QLabel#chat_pet { background: #2A2A2A; border: 1px solid #3A3A3A; color: #ECECEC; }
"""


def try_acrylic(widget):
    """给顶层窗口尝试启用 Windows 亚克力模糊背景。

    通过 SetWindowCompositionAttribute 设置 ACCENT_ENABLE_ACRYLICBLURBEHIND。
    返回 True=开启成功（调用方应把窗口切成更透明的渐变背景以露出模糊）。
    任何异常都静默返回 False，让调用方回退到不透明渐变，界面不受影响。
    """
    global acrylic_on
    if sys.platform != "win32":
        return False
    try:
        import ctypes

        class ACCENT(ctypes.Structure):
            _fields_ = [
                ("AccentState", ctypes.c_uint),
                ("AccentFlags", ctypes.c_uint),
                ("GradientColor", ctypes.c_uint),
                ("AnimationId", ctypes.c_uint),
            ]

        class WCAD(ctypes.Structure):
            _fields_ = [
                ("Attribute", ctypes.c_int),
                ("Data", ctypes.c_void_p),
                ("SizeOfData", ctypes.c_size_t),
            ]

        hwnd = int(widget.winId())
        # GradientColor = 0xAABBGGRR：0xCC 不透明度(80%) + 极淡冷白，配合极光渐变
        accent = ACCENT(4, 2, 0xCCF2F6FA, 0)   # ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
        data = WCAD(19, ctypes.byref(accent), ctypes.sizeof(accent))
        ok = ctypes.windll.user32.SetWindowCompositionAttribute(hwnd, ctypes.byref(data))
        if bool(ok):
            acrylic_on = True
        return bool(ok)
    except Exception:
        return False


# ============ 玻璃配方（浅色） ============
# 浅色下文字用深色（玻璃基底偏浅，保证对比度）。
_LIGHT_QSS = """
QWidget {
    background: transparent;
    color: #20242E;
    font-family: "Microsoft YaHei UI";
}
QWidget#float_window, QWidget#float_window_acrylic {
    border: 1px solid rgba(255, 255, 255, 200);
    border-top: 1px solid rgba(255, 255, 255, 235);
    border-radius: 16px;
}
QWidget#float_window {
    /* 亚克力失败：不透明极光渐变，玻璃卡片悬在彩色背景上 */
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(147, 197, 253, 248),
        stop:0.32 rgba(196, 181, 253, 248),
        stop:0.64 rgba(253, 186, 233, 248),
        stop:1 rgba(153, 246, 228, 248));
}
QWidget#float_window_acrylic {
    /* 亚克力开启：半透明极光，桌面被真模糊后透出，形成磨砂玻璃 */
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(147, 197, 253, 195),
        stop:0.32 rgba(196, 181, 253, 195),
        stop:0.64 rgba(253, 186, 233, 195),
        stop:1 rgba(153, 246, 228, 195));
}
QWidget#header {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.34), stop:1 rgba(255, 255, 255, 0.14));
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-top: 1px solid rgba(255, 255, 255, 0.85);
    border-bottom: 1px solid rgba(80, 110, 160, 0.16);
    border-radius: 12px;
}
QLabel, QCheckBox, QFrame, QListWidget, QScrollArea {
    background: transparent;
}
QWidget#main_tabs,
QWidget#plan_tab, QWidget#timer_tab, QWidget#reminder_tab, QWidget#stats_tab {
    background: transparent;
}
/* 前景框：玻璃配方 —— 斜向高光 + 顶部亮边 + 底部暗边 + 白描边 + 大圆角 */
#card, #glass_popup, QFrame#pet_bubble, QFrame#pet_status, QFrame#pet_bar {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.34),
        stop:0.45 rgba(255, 255, 255, 0.17),
        stop:1 rgba(255, 255, 255, 0.10));
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-top: 1px solid rgba(255, 255, 255, 0.85);
    border-bottom: 1px solid rgba(80, 110, 160, 0.16);
    border-radius: 16px;
}
#glass_popup { border-radius: 14px; }
QFrame#pet_bar { border-radius: 18px; }
QFrame#pet_status {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(130, 180, 255, 0.34), stop:1 rgba(130, 180, 255, 0.14));
    border: 1px solid rgba(150, 190, 255, 0.45);
}
QLabel#title {
    font-size: 16px;
    font-weight: bold;
    color: #20242E;
}
QLabel#subtitle {
    font-size: 12px;
    color: #566072;
    line-height: 150%;
}
QLabel#accent {
    color: #2563EB;
    font-weight: bold;
}
QLabel#pill, QLabel#pill_accent {
    border-radius: 8px;
    padding: 4px 8px;
    font-size: 12px;
}
QLabel#pill {
    background: rgba(255, 255, 255, 0.22);
    border: 1px solid rgba(255, 255, 255, 0.4);
    color: #566072;
}
QLabel#pill_accent {
    background: rgba(120, 165, 255, 0.35);
    border: 1px solid rgba(255, 255, 255, 0.5);
    color: #2563EB;
    font-weight: bold;
}
QLabel#today_tag_done, QLabel#today_tag_todo {
    border-radius: 9px;
    padding: 2px 8px;
    font-size: 11px;
    font-weight: bold;
}
QLabel#today_tag_done {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(31, 198, 171, 0.38), stop:1 rgba(15, 118, 110, 0.26));
    border: 1px solid rgba(31, 198, 171, 0.6);
    color: #0B6B5F;
}
QLabel#today_tag_todo {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.30), stop:1 rgba(255, 255, 255, 0.16));
    border: 1px solid rgba(120, 150, 210, 0.45);
    color: #5B6472;
}
QLabel#empty {
    background: rgba(255, 255, 255, 0.13);
    border: 1px dashed rgba(255, 255, 255, 0.55);
    border-radius: 12px;
    color: #6E7686;
    font-size: 13px;
    padding: 22px 12px;
}
QLabel#big_time {
    font-size: 44px;
    font-weight: bold;
    color: #20242E;
}
QLabel#status {
    font-size: 12px;
    color: #566072;
}
QLabel#stat_value {
    background: rgba(255, 255, 255, 0.24);
    border: 1px solid rgba(255, 255, 255, 0.42);
    border-top: 1px solid rgba(255, 255, 255, 0.7);
    border-radius: 10px;
    color: #20242E;
    font-size: 13px;
    font-weight: bold;
    padding: 7px 4px;
}
#accent_bar {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 #0F766E, stop:1 #34D399);
    border-radius: 2px;
}
QPushButton {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.34), stop:1 rgba(255, 255, 255, 0.15));
    border: 1px solid rgba(255, 255, 255, 0.45);
    border-top: 1px solid rgba(255, 255, 255, 0.75);
    border-radius: 12px;
    padding: 5px 12px;
    font-size: 13px;
    color: #232A38;
}
QPushButton:hover {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.5), stop:1 rgba(255, 255, 255, 0.24));
    border-color: rgba(255, 255, 255, 0.6);
}
QPushButton:pressed {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(190, 205, 235, 0.35), stop:1 rgba(255, 255, 255, 0.16));
}
QPushButton:disabled {
    background: rgba(255, 255, 255, 0.10);
    border-color: rgba(255, 255, 255, 0.22);
    color: #A2AAB8;
}
QPushButton#primary {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #5B9BFF, stop:1 #2563EB);
    border: none;
    border-radius: 12px;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#primary:hover { background: #3B76F0; }
QPushButton#primary:disabled { background: #A9C2F2; color: #EEF4FF; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.28), stop:1 rgba(255, 255, 255, 0.12));
    border: 1px solid rgba(255, 255, 255, 0.4);
    border-top: 1px solid rgba(255, 255, 255, 0.7);
    color: #3D4452;
}
QPushButton#secondary:hover, QPushButton#preset:hover, QPushButton#header_button:hover {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.44), stop:1 rgba(255, 255, 255, 0.22));
    border-color: rgba(255, 255, 255, 0.6);
}
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon {
    background: transparent;
    border: none;
    padding: 0;
    color: #6E7A8C;
    font-size: 13px;
}
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover {
    background: rgba(90, 140, 255, 0.22);
    color: #2563EB;
}
QPushButton#danger_icon:hover, QPushButton#close_icon:hover {
    background: rgba(255, 110, 110, 0.22);
    color: #DC2626;
}
QPushButton#toggle_on {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #1FC6AB, stop:1 #0F766E);
    border: none;
    border-radius: 12px;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#toggle_off {
    background: rgba(255, 255, 255, 0.18);
    border: 1px solid rgba(255, 255, 255, 0.4);
    border-radius: 12px;
    color: #5B6472;
}
QLineEdit, QTimeEdit, QSpinBox, QDateEdit {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.28), stop:1 rgba(255, 255, 255, 0.13));
    border: 1px solid rgba(255, 255, 255, 0.4);
    border-top: 1px solid rgba(255, 255, 255, 0.7);
    border-radius: 12px;
    padding: 5px 8px;
    font-size: 13px;
    color: #20242E;
    selection-background-color: #2563EB;
    selection-color: #FFFFFF;
}
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus {
    border: 1px solid rgba(77, 130, 255, 0.9);
    background: rgba(255, 255, 255, 0.32);
}
QTabWidget::pane {
    border: none;
    background: transparent;
}
QTabBar { background: transparent; }
QTabBar::tab {
    background: rgba(255, 255, 255, 0.10);
    color: #566072;
    padding: 7px 13px;
    font-size: 13px;
    border: 1px solid rgba(255, 255, 255, 0.22);
    border-right: none;
}
QTabBar::tab:first { border-top-left-radius: 11px; border-bottom-left-radius: 11px; }
QTabBar::tab:last {
    border-right: 1px solid rgba(255, 255, 255, 0.22);
    border-top-right-radius: 11px; border-bottom-right-radius: 11px;
}
QTabBar::tab:selected {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.45), stop:1 rgba(255, 255, 255, 0.20));
    color: #2563EB;
    border-color: rgba(255, 255, 255, 0.55);
    font-weight: bold;
}
QTabBar::tab:hover { color: #20242E; background: rgba(255, 255, 255, 0.22); }
QCheckBox { font-size: 13px; spacing: 8px; }
QCheckBox::indicator {
    width: 18px; height: 18px;
    border: 1px solid rgba(255, 255, 255, 0.7);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.22);
}
QCheckBox::indicator:hover { background: rgba(255, 255, 255, 0.34); }
QCheckBox::indicator:checked {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #1FC6AB, stop:1 #0F766E);
    border-color: rgba(255, 255, 255, 0.8);
}
QListWidget {
    background: transparent;
    border: none;
    outline: 0;
    font-size: 13px;
}
QListWidget::item { padding: 2px 0; }
QListWidget::item:selected { background: transparent; }
QScrollBar:vertical {
    background: transparent; width: 8px; margin: 2px;
}
QScrollBar::handle:vertical { background: rgba(255, 255, 255, 0.30); border-radius: 4px; min-height: 30px; }
QScrollBar::handle:vertical:hover { background: rgba(255, 255, 255, 0.45); }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QProgressBar {
    background: rgba(255, 255, 255, 0.16);
    border: 1px solid rgba(255, 255, 255, 0.28);
    border-radius: 6px;
    height: 12px;
    text-align: center;
    font-size: 9px;
    color: #20242E;
}
QProgressBar::chunk {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #1FC6AB, stop:1 #0F766E);
    border-radius: 6px;
}
QMenu {
    background: rgba(250, 252, 255, 242);
    border: 1px solid rgba(150, 170, 205, 120);
    border-radius: 12px;
    padding: 4px;
    font-size: 13px;
}
QMenu::item { padding: 5px 18px; border-radius: 6px; }
QMenu::item:selected { background-color: rgba(90, 150, 255, 0.18); color: #2563EB; }
QToolTip {
    background: rgba(250, 252, 255, 244);
    color: #20242E;
    border: 1px solid rgba(150, 170, 205, 120);
    padding: 4px;
}
QDialog {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(235, 246, 255, 250), stop:0.55 rgba(246, 240, 255, 250), stop:1 rgba(240, 250, 247, 250));
    border: 1px solid rgba(255, 255, 255, 0.6);
}
QDialogButtonBox, QFormLayout { background: transparent; }
QWidget#chat_window {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(147, 197, 253, 246),
        stop:0.5 rgba(196, 181, 253, 246),
        stop:1 rgba(153, 246, 228, 246));
    border: 1px solid rgba(255, 255, 255, 0.55);
    border-top: 1px solid rgba(255, 255, 255, 0.85);
    border-radius: 14px;
}
QScrollArea#chat_scroll, QWidget#chat_host {
    background: transparent;
}
QLabel#chat_me {
    background-color: rgba(37, 99, 235, 215);
    color: #FFFFFF;
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 13px;
}
QLabel#chat_pet {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.34), stop:1 rgba(255, 255, 255, 0.16));
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 13px;
    color: #20242E;
}
QFrame#pet_bubble { border-radius: 12px; }
QFrame#pet_status { border-radius: 12px; }
QLabel#pb_text {
    background: transparent;
    color: #20242E;
    font-size: 12px;
}
QLabel#ps_text {
    background: transparent;
    color: #2563EB;
    font-size: 12px;
}
QLineEdit#pet_input {
    background: rgba(255, 255, 255, 0.18);
    border: 1px solid rgba(120, 150, 210, 0.6);
    border-radius: 10px;
    padding: 4px 8px;
    min-height: 18px;
    font-size: 12px;
    color: #20242E;
    selection-background-color: #2563EB;
    selection-color: #FFFFFF;
}
QLineEdit#pet_input:hover { background: rgba(255, 255, 255, 0.26); }
QLineEdit#pet_input:focus {
    background: rgba(255, 255, 255, 0.30);
    border: 1px solid #3B82F6;
}
QLabel#section_header {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 rgba(110, 160, 255, 0.36), stop:1 rgba(110, 160, 255, 0.16));
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 9px;
    padding: 3px 10px;
    font-size: 11px;
    font-weight: bold;
    color: #2563EB;
}
QLabel#section_header_fixed {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 rgba(45, 212, 155, 0.34), stop:1 rgba(45, 212, 155, 0.14));
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 9px;
    padding: 3px 10px;
    font-size: 11px;
    font-weight: bold;
    color: #0E7A68;
}
QLabel#day_total {
    background: rgba(255, 255, 255, 0.20);
    border: 1px solid rgba(255, 255, 255, 0.35);
    border-radius: 7px;
    padding: 2px 8px;
    font-size: 11px;
    color: #566072;
}
QLabel#day_total_peak {
    background: rgba(110, 160, 255, 0.35);
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 7px;
    padding: 2px 8px;
    font-size: 11px;
    font-weight: bold;
    color: #2563EB;
}
"""


# ============ 玻璃配方（深色） ============
# 深色下文字用浅色（玻璃基底偏深，保证对比度）。
_DARK_QSS = """
QWidget {
    background: transparent;
    color: #F1F4F9;
    font-family: "Microsoft YaHei UI";
}
QWidget#float_window, QWidget#float_window_acrylic {
    border: 1px solid rgba(255, 255, 255, 30);
    border-top: 1px solid rgba(255, 255, 255, 60);
    border-radius: 16px;
}
QWidget#float_window {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(50, 70, 120, 250),
        stop:0.32 rgba(80, 52, 128, 250),
        stop:0.64 rgba(128, 48, 104, 250),
        stop:1 rgba(30, 96, 96, 250));
}
QWidget#float_window_acrylic {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(50, 70, 120, 176),
        stop:0.32 rgba(80, 52, 128, 176),
        stop:0.64 rgba(128, 48, 104, 176),
        stop:1 rgba(30, 96, 96, 176));
}
QWidget#header {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.17), stop:1 rgba(255, 255, 255, 0.07));
    border: 1px solid rgba(255, 255, 255, 0.20);
    border-top: 1px solid rgba(255, 255, 255, 0.34);
    border-bottom: 1px solid rgba(0, 0, 0, 0.28);
    border-radius: 12px;
}
QLabel, QCheckBox, QFrame, QListWidget, QScrollArea {
    background: transparent;
}
QWidget#main_tabs,
QWidget#plan_tab, QWidget#timer_tab, QWidget#reminder_tab, QWidget#stats_tab {
    background: transparent;
}
#card, #glass_popup, QFrame#pet_bubble, QFrame#pet_status, QFrame#pet_bar {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.17),
        stop:0.45 rgba(255, 255, 255, 0.08),
        stop:1 rgba(255, 255, 255, 0.04));
    border: 1px solid rgba(255, 255, 255, 0.20);
    border-top: 1px solid rgba(255, 255, 255, 0.34);
    border-bottom: 1px solid rgba(0, 0, 0, 0.28);
    border-radius: 16px;
}
#glass_popup { border-radius: 14px; }
QFrame#pet_bar { border-radius: 18px; }
QFrame#pet_status {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(120, 160, 255, 0.24), stop:1 rgba(120, 160, 255, 0.10));
    border: 1px solid rgba(150, 190, 255, 0.30);
}
QLabel#title {
    font-size: 16px;
    font-weight: bold;
    color: #F4F6FA;
}
QLabel#subtitle {
    font-size: 12px;
    color: #AEB6C6;
    line-height: 150%;
}
QLabel#accent {
    color: #8AB4FF;
    font-weight: bold;
}
QLabel#pill, QLabel#pill_accent {
    border-radius: 8px;
    padding: 4px 8px;
    font-size: 12px;
}
QLabel#pill {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid rgba(255, 255, 255, 0.18);
    color: #B6BCC9;
}
QLabel#pill_accent {
    background: rgba(110, 160, 255, 0.22);
    border: 1px solid rgba(255, 255, 255, 0.24);
    color: #A8C7FF;
    font-weight: bold;
}
QLabel#today_tag_done, QLabel#today_tag_todo {
    border-radius: 9px;
    padding: 2px 8px;
    font-size: 11px;
    font-weight: bold;
}
QLabel#today_tag_done {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(31, 198, 171, 0.32), stop:1 rgba(15, 118, 110, 0.20));
    border: 1px solid rgba(31, 198, 171, 0.5);
    color: #7BE0C9;
}
QLabel#today_tag_todo {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid rgba(255, 255, 255, 0.18);
    color: #AEB6C6;
}
QLabel#empty {
    background: rgba(255, 255, 255, 0.06);
    border: 1px dashed rgba(255, 255, 255, 0.20);
    border-radius: 12px;
    color: #8E95A6;
    font-size: 13px;
    padding: 22px 12px;
}
QLabel#big_time {
    font-size: 44px;
    font-weight: bold;
    color: #F4F6FA;
}
QLabel#status {
    font-size: 12px;
    color: #AEB6C6;
}
QLabel#stat_value {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid rgba(255, 255, 255, 0.20);
    border-top: 1px solid rgba(255, 255, 255, 0.32);
    border-radius: 10px;
    color: #F4F6FA;
    font-size: 13px;
    font-weight: bold;
    padding: 7px 4px;
}
#accent_bar {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 #34D399, stop:1 #14B8A6);
    border-radius: 2px;
}
QPushButton {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.18), stop:1 rgba(255, 255, 255, 0.07));
    border: 1px solid rgba(255, 255, 255, 0.22);
    border-top: 1px solid rgba(255, 255, 255, 0.36);
    border-radius: 12px;
    padding: 5px 12px;
    font-size: 13px;
    color: #F1F4F9;
}
QPushButton:hover {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.26), stop:1 rgba(255, 255, 255, 0.12));
    border-color: rgba(255, 255, 255, 0.32);
}
QPushButton:pressed {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(0, 0, 0, 0.10), stop:1 rgba(255, 255, 255, 0.06));
}
QPushButton:disabled {
    background: rgba(255, 255, 255, 0.06);
    border-color: rgba(255, 255, 255, 0.10);
    color: #70757F;
}
QPushButton#primary {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #6E9EF5, stop:1 #4B7DE8);
    border: none;
    border-radius: 12px;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#primary:hover { background: #5B8DEF; }
QPushButton#primary:disabled { background: #3E4D67; color: #8793A8; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.14), stop:1 rgba(255, 255, 255, 0.05));
    border: 1px solid rgba(255, 255, 255, 0.18);
    border-top: 1px solid rgba(255, 255, 255, 0.30);
    color: #DDE2EA;
}
QPushButton#secondary:hover, QPushButton#preset:hover, QPushButton#header_button:hover {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.22), stop:1 rgba(255, 255, 255, 0.10));
    border-color: rgba(255, 255, 255, 0.28);
}
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon {
    background: transparent;
    border: none;
    padding: 0;
    color: #A9ADB7;
    font-size: 13px;
}
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover {
    background: rgba(96, 130, 200, 0.28);
    color: #A8C7FF;
}
QPushButton#danger_icon:hover, QPushButton#close_icon:hover {
    background: rgba(200, 80, 90, 0.28);
    color: #FFB4AB;
}
QPushButton#toggle_on {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #10B897, stop:1 #0E8F77);
    border: none;
    border-radius: 12px;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#toggle_off {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid rgba(255, 255, 255, 0.18);
    border-radius: 12px;
    color: #A9ADB7;
}
QLineEdit, QTimeEdit, QSpinBox, QDateEdit {
    background: rgba(255, 255, 255, 0.09);
    border: 1px solid rgba(255, 255, 255, 0.20);
    border-top: 1px solid rgba(255, 255, 255, 0.32);
    border-radius: 12px;
    padding: 5px 8px;
    font-size: 13px;
    color: #F1F4F9;
    selection-background-color: #5B8DEF;
    selection-color: #FFFFFF;
}
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus {
    border: 1px solid rgba(140, 180, 255, 0.9);
    background: rgba(255, 255, 255, 0.14);
}
QTabWidget::pane {
    border: none;
    background: transparent;
}
QTabBar { background: transparent; }
QTabBar::tab {
    background: rgba(255, 255, 255, 0.06);
    color: #AEB6C6;
    padding: 7px 13px;
    font-size: 13px;
    border: 1px solid rgba(255, 255, 255, 0.10);
    border-right: none;
}
QTabBar::tab:first { border-top-left-radius: 11px; border-bottom-left-radius: 11px; }
QTabBar::tab:last {
    border-right: 1px solid rgba(255, 255, 255, 0.10);
    border-top-right-radius: 11px; border-bottom-right-radius: 11px;
}
QTabBar::tab:selected {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 rgba(255, 255, 255, 0.24), stop:1 rgba(255, 255, 255, 0.10));
    color: #A8C7FF;
    border-color: rgba(255, 255, 255, 0.26);
    font-weight: bold;
}
QTabBar::tab:hover { color: #F1F4F9; background: rgba(255, 255, 255, 0.12); }
QCheckBox { font-size: 13px; spacing: 8px; }
QCheckBox::indicator {
    width: 18px; height: 18px;
    border: 1px solid rgba(255, 255, 255, 0.32);
    border-radius: 6px;
    background: rgba(255, 255, 255, 0.08);
}
QCheckBox::indicator:hover { background: rgba(255, 255, 255, 0.16); }
QCheckBox::indicator:checked {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #10B897, stop:1 #0E8F77);
    border-color: rgba(255, 255, 255, 0.5);
}
QListWidget {
    background: transparent;
    border: none;
    outline: 0;
    font-size: 13px;
}
QListWidget::item { padding: 2px 0; }
QListWidget::item:selected { background: transparent; }
QScrollBar:vertical {
    background: transparent; width: 8px; margin: 2px;
}
QScrollBar::handle:vertical { background: rgba(255, 255, 255, 0.14); border-radius: 4px; min-height: 30px; }
QScrollBar::handle:vertical:hover { background: rgba(255, 255, 255, 0.24); }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QProgressBar {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid rgba(255, 255, 255, 0.16);
    border-radius: 6px;
    height: 12px;
    text-align: center;
    font-size: 9px;
    color: #F1F4F9;
}
QProgressBar::chunk {
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
        stop:0 #10A37F, stop:1 #0E8F77);
    border-radius: 6px;
}
QMenu {
    background: rgba(34, 38, 50, 244);
    border: 1px solid #3C414C;
    border-radius: 12px;
    padding: 4px;
    font-size: 13px;
    color: #F1F4F9;
}
QMenu::item { padding: 5px 18px; border-radius: 6px; }
QMenu::item:selected { background-color: rgba(72, 100, 160, 0.4); color: #A8C7FF; }
QToolTip {
    background: rgba(34, 38, 50, 246);
    color: #F1F4F9;
    border: 1px solid #3C414C;
    padding: 4px;
}
QDialog {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(34, 48, 84, 252), stop:0.55 rgba(58, 42, 92, 252), stop:1 rgba(28, 80, 80, 252));
    border: 1px solid rgba(255, 255, 255, 0.20);
}
QDialogButtonBox, QFormLayout { background: transparent; }
QWidget#chat_window {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(50, 70, 120, 248),
        stop:0.5 rgba(80, 52, 128, 248),
        stop:1 rgba(30, 96, 96, 248));
    border: 1px solid rgba(255, 255, 255, 0.22);
    border-top: 1px solid rgba(255, 255, 255, 0.34);
    border-radius: 14px;
}
QScrollArea#chat_scroll, QWidget#chat_host {
    background: transparent;
}
QLabel#chat_me {
    background-color: rgba(91, 141, 239, 215);
    color: #FFFFFF;
    border: 1px solid rgba(255, 255, 255, 0.30);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 13px;
}
QLabel#chat_pet {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:1,
        stop:0 rgba(255, 255, 255, 0.17), stop:1 rgba(255, 255, 255, 0.08));
    border: 1px solid rgba(255, 255, 255, 0.20);
    border-radius: 10px;
    padding: 6px 10px;
    font-size: 13px;
    color: #F1F4F9;
}
QFrame#pet_bubble { border-radius: 12px; }
QFrame#pet_status { border-radius: 12px; }
QLabel#pb_text {
    background: transparent;
    color: #F1F4F9;
    font-size: 12px;
}
QLabel#ps_text {
    background: transparent;
    color: #A8C7FF;
    font-size: 12px;
}
QLineEdit#pet_input {
    background: rgba(255, 255, 255, 0.10);
    border: 1px solid #46516B;
    border-radius: 10px;
    padding: 4px 8px;
    min-height: 18px;
    font-size: 12px;
    color: #F1F4F9;
    selection-background-color: #5B8DEF;
    selection-color: #FFFFFF;
}
QLineEdit#pet_input:hover { background: rgba(255, 255, 255, 0.16); }
QLineEdit#pet_input:focus {
    background: rgba(255, 255, 255, 0.16);
    border: 1px solid #8AB4FF;
}
QLabel#section_header {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 rgba(120, 170, 255, 0.24), stop:1 rgba(120, 170, 255, 0.10));
    border: 1px solid rgba(255, 255, 255, 0.24);
    border-radius: 9px;
    padding: 3px 10px;
    font-size: 11px;
    font-weight: bold;
    color: #9CC0FF;
}
QLabel#section_header_fixed {
    background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
        stop:0 rgba(45, 212, 155, 0.22), stop:1 rgba(45, 212, 155, 0.10));
    border: 1px solid rgba(255, 255, 255, 0.24);
    border-radius: 9px;
    padding: 3px 10px;
    font-size: 11px;
    font-weight: bold;
    color: #6FD6C2;
}
QLabel#day_total {
    background: rgba(255, 255, 255, 0.08);
    border: 1px solid rgba(255, 255, 255, 0.16);
    border-radius: 7px;
    padding: 2px 8px;
    font-size: 11px;
    color: #B6BCC9;
}
QLabel#day_total_peak {
    background: rgba(110, 160, 255, 0.25);
    border: 1px solid rgba(255, 255, 255, 0.24);
    border-radius: 7px;
    padding: 2px 8px;
    font-size: 11px;
    font-weight: bold;
    color: #A8C7FF;
}
"""


def paint_glass_background(widget, painter, variant="aurora"):
    """在带 WA_TranslucentBackground 的顶层窗口上手工绘制玻璃背景。

    背景：Qt 已知行为——透明顶层窗口（WA_TranslucentBackground）的 QSS 背景
    不会进入 backing store，grab() 和真实屏幕都画不出来（子控件不受影响）。
    所以玻璃底板必须在 paintEvent 里用 QPainter 手动画。

    variant:
      "aurora" —— 全窗极光渐变（浅色粉蓝/深色紫靛）+ 圆角 + 白色描边 + 顶部亮线。
                  亚克力开启时半透明露出桌面模糊，否则近不透明，保证可读。
      "panel"  —— 玻璃卡片配方：白色斜向高光 + 顶部亮边 + 底部暗边（弹窗等浮层用）。
    """
    from PyQt5.QtGui import (
        QBrush, QColor, QLinearGradient, QPainter, QPainterPath, QPen,
    )
    from PyQt5.QtCore import QRectF, Qt

    rect = QRectF(widget.rect())
    radius = 16.0 if variant == "aurora" else 14.0
    dark = current == "dark"

    painter.save()
    painter.setRenderHint(QPainter.Antialiasing, True)
    path = QPainterPath()
    path.addRoundedRect(rect, radius, radius)
    painter.setClipPath(path)

    if variant == "aurora":
        if dark:
            # A quiet graphite canvas keeps the task content, rather than the
            # window chrome, at the centre of attention.
            stops = [(0.0, (24, 24, 24)), (0.48, (31, 31, 31)),
                     (1.0, (25, 28, 27))]
            alpha = 222 if acrylic_on else 255
        else:
            # Notion-like warm white gives the app a paper-workspace feel.
            stops = [(0.0, (250, 250, 249)), (0.55, (247, 247, 245)),
                     (1.0, (243, 246, 244))]
            alpha = 224 if acrylic_on else 255
        g = QLinearGradient(rect.topLeft(), rect.bottomRight())
        for pos, (r, gg, b) in stops:
            g.setColorAt(pos, QColor(r, gg, b, alpha))
        painter.fillRect(rect, QBrush(g))
    else:
        # 玻璃卡片：左上斜向白色高光 + 底部一点暗边
        if dark:
            g = QLinearGradient(rect.topLeft(), rect.bottomRight())
            g.setColorAt(0.0, QColor(255, 255, 255, 44))
            g.setColorAt(0.45, QColor(255, 255, 255, 20))
            g.setColorAt(1.0, QColor(255, 255, 255, 10))
        else:
            g = QLinearGradient(rect.topLeft(), rect.bottomRight())
            g.setColorAt(0.0, QColor(255, 255, 255, 90))
            g.setColorAt(0.45, QColor(255, 255, 255, 45))
            g.setColorAt(1.0, QColor(255, 255, 255, 26))
        painter.fillRect(rect, QBrush(g))

    painter.setClipping(False)
    # 描边：整体白边 + 顶部更亮（反光边缘）
    border_c = QColor(224, 224, 220, 235 if not dark else 48)
    painter.setPen(QPen(border_c, 1.0))
    painter.setBrush(Qt.NoBrush)
    painter.drawRoundedRect(rect.adjusted(0.5, 0.5, -0.5, -0.5), radius, radius)
    painter.setPen(QPen(QColor(255, 255, 255, 160 if not dark else 46), 1.0))
    painter.drawLine(int(rect.left()) + 6, int(rect.top()) + 1,
                     int(rect.right()) - 6, int(rect.top()) + 1)
    painter.restore()


def detect_system_theme():
    """读取注册表判断 Windows 深浅色模式，返回 'light' 或 'dark'。"""
    try:
        import winreg
        key = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        )
        value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        winreg.CloseKey(key)
        return "light" if value == 1 else "dark"
    except Exception:
        # 非 Windows 或读取失败时，按应用初始调色板判断
        app = QApplication.instance()
        if app is not None:
            return "light" if app.palette().color(app.palette().Window).lightness() > 128 else "dark"
        return "dark"


def apply_theme(app, theme):
    """对 QApplication 应用对应主题 QSS，并记录当前主题。"""
    global current
    current = theme
    qss = (_LIGHT_QSS + _WORKSPACE_LIGHT) if theme == "light" else (_DARK_QSS + _WORKSPACE_DARK)
    app.setStyleSheet(qss)
    return theme


def start_theme_watcher(app, callback, interval_ms=3000):
    """启动定时器轮询系统主题，变化时回调。返回 QTimer 对象。"""
    from PyQt5.QtCore import QTimer

    current = {"theme": detect_system_theme()}

    def check():
        t = detect_system_theme()
        if t != current["theme"]:
            current["theme"] = t
            apply_theme(app, t)
            callback(t)

    timer = QTimer()
    timer.timeout.connect(check)
    timer.start(interval_ms)
    return timer


def main():
    """测试入口：直接运行 python theme.py 查看主题检测结果。"""
    app = QApplication(sys.argv)
    print("检测到主题:", detect_system_theme())


if __name__ == "__main__":
    main()
