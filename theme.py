"""主题模块：检测 Windows 深浅色模式，提供两套 QSS 并实时切换。"""
import sys
from PyQt5.QtWidgets import QApplication

# 当前生效主题（'light' / 'dark'），供自定义绘制控件（如直方图）读取配色
current = "light"

_LIGHT_QSS = """
QWidget {
    background-color: #F7F5EF;
    color: #202124;
    font-family: "Microsoft YaHei UI";
}
QWidget#float_window {
    background-color: #F7F5EF;
    border: 1px solid #DDD8CD;
    border-radius: 12px;
}
QWidget#header {
    background-color: #FFFFFF;
    border: 1px solid #E6E0D6;
    border-radius: 8px;
}
QLabel, QCheckBox {
    background: transparent;
}
#card {
    background-color: #FFFFFF;
    border: 1px solid #E7E2D8;
    border-radius: 8px;
}
QLabel#title {
    font-size: 16px;
    font-weight: bold;
    color: #202124;
}
QLabel#subtitle {
    font-size: 12px;
    color: #6E716C;
    line-height: 150%;
}
QLabel#accent {
    color: #2563EB;
    font-weight: bold;
}
QLabel#pill, QLabel#pill_accent {
    border-radius: 6px;
    padding: 4px 8px;
    font-size: 12px;
}
QLabel#pill {
    background-color: #F0EDE6;
    color: #656A62;
}
QLabel#pill_accent {
    background-color: #E9F2FF;
    color: #1D4ED8;
    font-weight: bold;
}
QLabel#empty {
    background-color: #FBFAF7;
    border: 1px dashed #D8D1C4;
    border-radius: 8px;
    color: #77756E;
    font-size: 13px;
    padding: 22px 12px;
}
QLabel#big_time {
    font-size: 44px;
    font-weight: bold;
    color: #202124;
}
QLabel#status {
    font-size: 12px;
    color: #6E716C;
}
QLabel#stat_value {
    background-color: #F4F0E8;
    border: 1px solid #E5DED3;
    border-radius: 7px;
    color: #202124;
    font-size: 13px;
    font-weight: bold;
    padding: 7px 4px;
}
#accent_bar {
    background-color: #0F766E;
    border-radius: 2px;
}
QPushButton {
    background-color: #EFEAE1;
    border: 1px solid #DCD5C9;
    border-radius: 6px;
    padding: 5px 12px;
    font-size: 13px;
    color: #202124;
}
QPushButton:hover { background-color: #E7E1D8; }
QPushButton:pressed { background-color: #D9D1C5; }
QPushButton:disabled {
    background-color: #EEEAE3;
    border-color: #E1DCD2;
    color: #AAA59C;
}
QPushButton#primary {
    background-color: #2563EB;
    border: none;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#primary:hover { background-color: #1D4ED8; }
QPushButton#primary:disabled { background-color: #AFC7F7; color: #EEF4FF; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button {
    background-color: #FFFFFF;
    border: 1px solid #E2DBD0;
    color: #3D403B;
}
QPushButton#secondary:hover, QPushButton#preset:hover, QPushButton#header_button:hover {
    background-color: #F4F0E8;
    border-color: #CFC7BA;
}
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon {
    background-color: transparent;
    border: none;
    padding: 0;
    color: #83867F;
    font-size: 13px;
}
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover {
    background-color: #EEF3FF;
    color: #2563EB;
}
QPushButton#danger_icon:hover, QPushButton#close_icon:hover {
    background-color: #FEE2E2;
    color: #DC2626;
}
QPushButton#toggle_on {
    background-color: #0F766E;
    border: none;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#toggle_off {
    background-color: #EFEAE1;
    border: 1px solid #DCD5C9;
    color: #77756E;
}
QLineEdit, QTimeEdit, QSpinBox, QDateEdit {
    background-color: #FFFFFF;
    border: 1px solid #DCD5C9;
    border-radius: 6px;
    padding: 5px 8px;
    font-size: 13px;
    selection-background-color: #2563EB;
}
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus {
    border: 1px solid #2563EB;
    background-color: #FFFFFF;
}
QTabWidget::pane {
    border: none;
    background: transparent;
}
QTabBar::tab {
    background: #ECE7DD;
    color: #686B65;
    padding: 7px 13px;
    font-size: 13px;
    border: 1px solid #E0D8CC;
    border-right: none;
}
QTabBar::tab:first { border-top-left-radius: 7px; border-bottom-left-radius: 7px; }
QTabBar::tab:last { border-right: 1px solid #E0D8CC; border-top-right-radius: 7px; border-bottom-right-radius: 7px; }
QTabBar::tab:selected {
    background: #FFFFFF;
    color: #1D4ED8;
    border-color: #C7D7FE;
    font-weight: bold;
}
QTabBar::tab:hover { color: #202124; background: #F6F2EA; }
QCheckBox { font-size: 13px; spacing: 8px; }
QCheckBox::indicator {
    width: 18px; height: 18px;
    border: 2px solid #C3BBAE;
    border-radius: 4px;
    background: #FFFFFF;
}
QCheckBox::indicator:checked {
    background-color: #0F766E;
    border-color: #0F766E;
}
QListWidget {
    background: transparent;
    border: none;
    outline: 0;
    font-size: 13px;
}
QListWidget::item { padding: 3px 0; }
QListWidget::item:selected { background: transparent; }
QScrollBar:vertical {
    background: transparent; width: 8px; margin: 2px;
}
QScrollBar::handle:vertical { background: #CFC7BA; border-radius: 4px; min-height: 30px; }
QScrollBar::handle:vertical:hover { background: #BDB4A6; }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QProgressBar {
    background-color: #E5DED3;
    border: none;
    border-radius: 5px;
    height: 10px;
    text-align: center;
    font-size: 9px;
    color: #202124;
}
QProgressBar::chunk { background-color: #0F766E; border-radius: 5px; }
QMenu {
    background-color: #FFFFFF;
    border: 1px solid #E4DDD2;
    border-radius: 8px;
    padding: 4px;
    font-size: 13px;
}
QMenu::item { padding: 5px 18px; border-radius: 5px; }
QMenu::item:selected { background-color: #EEF3FF; color: #1D4ED8; }
QToolTip {
    background-color: #FFFFFF;
    color: #202124;
    border: 1px solid #E4DDD2;
    padding: 4px;
}
QWidget#chat_window {
    background-color: #FFFFFF;
    border: 1px solid #E7E2D8;
    border-radius: 12px;
}
QScrollArea#chat_scroll, QWidget#chat_host {
    background: transparent;
}
QLabel#chat_me {
    background-color: #2563EB;
    color: #FFFFFF;
    border-radius: 8px;
    padding: 6px 10px;
    font-size: 13px;
}
QLabel#chat_pet {
    background-color: #F0EDE6;
    color: #202124;
    border-radius: 8px;
    padding: 6px 10px;
    font-size: 13px;
}
QFrame#pet_bubble {
    background-color: #FFFFFF;
    border: 1px solid #E4DDD2;
    border-radius: 10px;
}
QLabel#pb_text {
    background: transparent;
    color: #202124;
    font-size: 12px;
}
QFrame#pet_status {
    background-color: rgba(219, 234, 254, 235);
    border: 1px solid #C3D9F7;
    border-radius: 11px;
}
QLabel#ps_text {
    background: transparent;
    color: #2E6BD6;
    font-size: 12px;
}
QFrame#pet_bar {
    background-color: rgba(255, 255, 255, 242);
    border: 1px solid #C9DBF5;
    border-radius: 16px;
}
QLineEdit#pet_input {
    background-color: rgba(255, 255, 255, 0.65);
    border: 1px solid #C9DBF5;
    border-radius: 9px;
    padding: 4px 8px;
    min-height: 18px;
    font-size: 12px;
    color: #202124;
    selection-background-color: #2563EB;
}
QLineEdit#pet_input:hover {
    background-color: #EAF2FE;
    border: 1px solid #A9C9F6;
}
QLineEdit#pet_input:focus {
    background-color: #FFFFFF;
    border: 1px solid #3B82F6;
}
"""

_DARK_QSS = """
QWidget {
    background-color: #18191D;
    color: #ECEFF3;
    font-family: "Microsoft YaHei UI";
}
QWidget#float_window {
    background-color: #18191D;
    border: 1px solid #32343B;
    border-radius: 12px;
}
QWidget#header {
    background-color: #23252B;
    border: 1px solid #343740;
    border-radius: 8px;
}
QLabel, QCheckBox {
    background: transparent;
}
#card {
    background-color: #23252B;
    border: 1px solid #343740;
    border-radius: 8px;
}
QLabel#title {
    font-size: 16px;
    font-weight: bold;
    color: #F4F6F8;
}
QLabel#subtitle {
    font-size: 12px;
    color: #A9ADB7;
}
QLabel#accent {
    color: #8AB4FF;
    font-weight: bold;
}
QLabel#pill, QLabel#pill_accent {
    border-radius: 6px;
    padding: 4px 8px;
    font-size: 12px;
}
QLabel#pill {
    background-color: #2D3036;
    color: #B3B7C0;
}
QLabel#pill_accent {
    background-color: #1F3153;
    color: #A8C7FF;
    font-weight: bold;
}
QLabel#empty {
    background-color: #202228;
    border: 1px dashed #3B3E47;
    border-radius: 8px;
    color: #A9ADB7;
    font-size: 13px;
    padding: 22px 12px;
}
QLabel#big_time {
    font-size: 44px;
    font-weight: bold;
    color: #F4F6F8;
}
QLabel#status {
    font-size: 12px;
    color: #A9ADB7;
}
QLabel#stat_value {
    background-color: #2B2E36;
    border: 1px solid #3C414C;
    border-radius: 7px;
    color: #F4F6F8;
    font-size: 13px;
    font-weight: bold;
    padding: 7px 4px;
}
#accent_bar {
    background-color: #34D399;
    border-radius: 2px;
}
QPushButton {
    background-color: #2C2F36;
    border: 1px solid #3D414B;
    border-radius: 6px;
    padding: 5px 12px;
    font-size: 13px;
    color: #ECEFF3;
}
QPushButton:hover { background-color: #353943; }
QPushButton:pressed { background-color: #262930; }
QPushButton:disabled {
    background-color: #272A31;
    border-color: #323640;
    color: #70757F;
}
QPushButton#primary {
    background-color: #5B8DEF;
    border: none;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#primary:hover { background-color: #78A2F5; }
QPushButton#primary:disabled { background-color: #3E4D67; color: #8793A8; }
QPushButton#secondary, QPushButton#preset, QPushButton#header_button {
    background-color: #23252B;
    border: 1px solid #3D414B;
    color: #DDE2EA;
}
QPushButton#secondary:hover, QPushButton#preset:hover, QPushButton#header_button:hover {
    background-color: #2E323B;
    border-color: #4B5362;
}
QPushButton#window_icon, QPushButton#ghost_icon, QPushButton#danger_icon, QPushButton#close_icon {
    background-color: transparent;
    border: none;
    padding: 0;
    color: #A9ADB7;
    font-size: 13px;
}
QPushButton#window_icon:hover, QPushButton#ghost_icon:hover {
    background-color: #263654;
    color: #A8C7FF;
}
QPushButton#danger_icon:hover, QPushButton#close_icon:hover {
    background-color: #4A2328;
    color: #FFB4AB;
}
QPushButton#toggle_on {
    background-color: #10A37F;
    border: none;
    color: #FFFFFF;
    font-weight: bold;
}
QPushButton#toggle_off {
    background-color: #2C2F36;
    border: 1px solid #3D414B;
    color: #A9ADB7;
}
QLineEdit, QTimeEdit, QSpinBox, QDateEdit {
    background-color: #202228;
    border: 1px solid #3D414B;
    border-radius: 6px;
    padding: 5px 8px;
    font-size: 13px;
    color: #ECEFF3;
    selection-background-color: #5B8DEF;
}
QLineEdit:focus, QTimeEdit:focus, QSpinBox:focus, QDateEdit:focus {
    border: 1px solid #8AB4FF;
}
QTabWidget::pane {
    border: none;
    background: transparent;
}
QTabBar::tab {
    background: #24272E;
    color: #A9ADB7;
    padding: 7px 13px;
    font-size: 13px;
    border: 1px solid #343740;
    border-right: none;
}
QTabBar::tab:first { border-top-left-radius: 7px; border-bottom-left-radius: 7px; }
QTabBar::tab:last { border-right: 1px solid #343740; border-top-right-radius: 7px; border-bottom-right-radius: 7px; }
QTabBar::tab:selected {
    background: #30343D;
    color: #A8C7FF;
    border-color: #465D89;
    font-weight: bold;
}
QTabBar::tab:hover { color: #ECEFF3; background: #2B2F37; }
QCheckBox { font-size: 13px; spacing: 8px; }
QCheckBox::indicator {
    width: 18px; height: 18px;
    border: 2px solid #555B66;
    border-radius: 4px;
    background: #202228;
}
QCheckBox::indicator:checked {
    background-color: #10A37F;
    border-color: #10A37F;
}
QListWidget {
    background: transparent;
    border: none;
    outline: 0;
    font-size: 13px;
}
QListWidget::item { padding: 3px 0; }
QListWidget::item:selected { background: transparent; }
QScrollBar:vertical {
    background: transparent; width: 8px; margin: 2px;
}
QScrollBar::handle:vertical { background: #454A55; border-radius: 4px; min-height: 30px; }
QScrollBar::handle:vertical:hover { background: #59606D; }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QProgressBar {
    background-color: #2C2F36;
    border: none;
    border-radius: 5px;
    height: 10px;
    text-align: center;
    font-size: 9px;
    color: #ECEFF3;
}
QProgressBar::chunk { background-color: #10A37F; border-radius: 5px; }
QMenu {
    background-color: #23252B;
    border: 1px solid #343740;
    border-radius: 8px;
    padding: 4px;
    font-size: 13px;
    color: #ECEFF3;
}
QMenu::item { padding: 5px 18px; border-radius: 5px; }
QMenu::item:selected { background-color: #263654; color: #A8C7FF; }
QToolTip {
    background-color: #23252B;
    color: #ECEFF3;
    border: 1px solid #343740;
    padding: 4px;
}
QWidget#chat_window {
    background-color: #23252B;
    border: 1px solid #343740;
    border-radius: 12px;
}
QScrollArea#chat_scroll, QWidget#chat_host {
    background: transparent;
}
QLabel#chat_me {
    background-color: #5B8DEF;
    color: #FFFFFF;
    border-radius: 8px;
    padding: 6px 10px;
    font-size: 13px;
}
QLabel#chat_pet {
    background-color: #2C2F36;
    color: #ECEFF3;
    border-radius: 8px;
    padding: 6px 10px;
    font-size: 13px;
}
QFrame#pet_bubble {
    background-color: #23252B;
    border: 1px solid #343740;
    border-radius: 10px;
}
QLabel#pb_text {
    background: transparent;
    color: #ECEFF3;
    font-size: 12px;
}
QFrame#pet_status {
    background-color: rgba(31, 49, 83, 240);
    border: 1px solid #33507E;
    border-radius: 11px;
}
QLabel#ps_text {
    background: transparent;
    color: #A8C7FF;
    font-size: 12px;
}
QFrame#pet_bar {
    background-color: rgba(35, 37, 43, 245);
    border: 1px solid #3C414C;
    border-radius: 16px;
}
QLineEdit#pet_input {
    background-color: rgba(255, 255, 255, 0.08);
    border: 1px solid #46516B;
    border-radius: 9px;
    padding: 4px 8px;
    min-height: 18px;
    font-size: 12px;
    color: #ECEFF3;
    selection-background-color: #5B8DEF;
}
QLineEdit#pet_input:hover {
    background-color: rgba(139, 168, 255, 0.16);
    border: 1px solid #5B7BC0;
}
QLineEdit#pet_input:focus {
    background-color: #23252B;
    border: 1px solid #8AB4FF;
}
"""


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
    qss = _LIGHT_QSS if theme == "light" else _DARK_QSS
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
