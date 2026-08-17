# -*- coding: utf-8 -*-
"""玻璃质感 + 分栏 + 一键清理修复 + 每日学习统计 冒烟测试（真实显示、自动退出）。"""
import os
import sys
import time
import tempfile

import storage
storage.DATA_FILE = os.path.join(
    tempfile.gettempdir(), "kaoyan_smoke_%d.json" % int(time.time() * 1000)
)

from PyQt5.QtCore import QTimer
from PyQt5.QtGui import QColor, QImage, QPainter
from PyQt5.QtWidgets import QApplication, QLabel
import theme
import widgets


def _save_shot(win, name):
    """透明窗口截图合成到渐变背景后存 JPEG（透明度查看器不支持）。"""
    pm = win.grab()
    out = QImage(pm.size(), QImage.Format_RGB32)
    p = QPainter(out)
    for y in range(out.height()):
        t = y / max(1, out.height() - 1)
        c = QColor(int(120 + 60 * (1 - t)), int(150 + 50 * t), int(200 + 30 * t))
        p.fillRect(0, y, out.width(), 1, c)
    p.drawPixmap(0, 0, pm)
    p.end()
    out.save(os.path.join(tempfile.gettempdir(), name + ".jpg"), "JPG", 92)

app = QApplication(sys.argv)
theme.apply_theme(app, theme.detect_system_theme())

failures = []


def check(name, cond):
    print(("PASS  " if cond else "FAIL  ") + name)
    if not cond:
        failures.append(name)


# ---------- 纯函数测试 ----------
from widgets import fmt_minutes, fixed_completed

check("fmt 90 -> 90 分钟", fmt_minutes(90) == "90 分钟")
check("fmt 100 -> 100 分钟", fmt_minutes(100) == "100 分钟")
check("fmt 101 -> 1 小时 41 分钟", fmt_minutes(101) == "1 小时 41 分钟")
check("fmt 120 -> 2 小时", fmt_minutes(120) == "2 小时")
check("fmt 125 -> 2 小时 5 分钟", fmt_minutes(125) == "2 小时 5 分钟")
check("fixed_completed 满条=True", fixed_completed({"done": False, "progress": 5, "target_days": 5}))
check("fixed_completed done=True", fixed_completed({"done": True, "progress": 2, "target_days": 5}))
check("fixed_completed 未满=False", not fixed_completed({"done": False, "progress": 4, "target_days": 5}))

# ---------- 构建主窗口 ----------
win = widgets.FloatWindow()
win.show()
check("窗口 objectName 已设为玻璃样式",
      win.objectName() in ("float_window", "float_window_acrylic"))

# 一次性任务：1 未完成 + 1 完成
day = storage.ensure_today(win.data)
win.data["daily"][day].append({"text": "背单词", "done": False})
win.data["daily"][day].append({"text": "刷题", "done": True})
# 固定任务：1 天即完成 + 30 天长跑
storage.add_fixed(win.data, "背单词A", "每天50个", 1)
storage.add_fixed(win.data, "跑步", "每天3公里", 30)
win.plan_tab.refresh()


def find_headers():
    found = []
    for i in range(win.plan_tab.list.count()):
        it = win.plan_tab.list.item(i)
        w = win.plan_tab.list.itemWidget(it)
        if w is None:
            continue
        for lbl in w.findChildren(QLabel):
            if lbl.objectName() in ("section_header", "section_header_fixed"):
                found.append(lbl.objectName())
                break
    return found


def run_asserts():
    pt = win.plan_tab

    # 分栏：两个标题 + 4 个任务行
    headers = find_headers()
    check("两个分栏标题", headers.count("section_header") == 1 and headers.count("section_header_fixed") == 1)
    check("列表共 6 项(2标题+2一次+2固定)", pt.list.count() == 6)

    # 底部进度：一次性 done=1，固定全未完成 → 已完成 1/4；有 1 项完成 → 清理按钮应可用
    check("初始进度 1/4", pt.progress_lbl.text() == "已完成 1/4")
    check("初始清理按钮可用(有1项完成)", pt.clear_done_btn.isEnabled())

    # 打卡「背单词A」（target=1）→ 立即完成 → 清理按钮可用，进度 2/4
    f1 = storage.find_fixed(win.data, win.data["tasks"][0]["id"])
    storage.punch_fixed(win.data, f1["id"])
    pt.refresh()
    check("完成固定任务后清理按钮可用", pt.clear_done_btn.isEnabled())
    check("进度 2/4", pt.progress_lbl.text() == "已完成 2/4")

    # 打卡「跑步」（30 天，只打一次）→ 未完成，不应计入进度条（修复核心）
    f2 = storage.find_fixed(win.data, win.data["tasks"][1]["id"])
    storage.punch_fixed(win.data, f2["id"])
    pt.refresh()
    check("只打卡未完成不计入", pt.progress_lbl.text() == "已完成 2/4")

    # 手工把固定任务进度条填满但 done 标记未同步 → 仍算已完成，清理可用
    tid3 = storage.add_fixed(win.data, "手工满条", "", 5)
    t3 = storage.find_fixed(win.data, tid3)
    t3["progress"] = 5
    pt.refresh()
    check("满条固定任务清理可用", pt.clear_done_btn.isEnabled())

    # 一键清理：应清掉 刷题(done) + 背单词A(done) + 手工满条(满条)
    before_tasks = len(win.data["tasks"])
    before_once = len(win.data["daily"][day])
    win.data["daily"][day] = [t for t in win.data["daily"][day] if not t.get("done")]
    win.data["tasks"] = [t for t in win.data["tasks"] if not widgets.fixed_completed(t)]
    win.save()
    pt.refresh()
    check("清理后一次性剩 1 项", len(win.data["daily"][day]) == before_once - 1)
    check("清理后固定任务剩 1 项(跑步)", len(win.data["tasks"]) == before_tasks - 2)

    # 专注统计：写入近几日历史
    hist = win.data.setdefault("focus_history", {})
    hist[day] = {"9": 15.0, "10": 20.0}                     # 今天 35 分钟
    import datetime
    yd = (datetime.date.today() - datetime.timedelta(days=1)).isoformat()
    hist[yd] = {"14": 125.0, "15": 60.0}                     # 昨天 185 分钟 → 3 小时 5 分
    win.stats_tab.refresh()
    check("每日统计 10 行", win.stats_tab.daily_list.count() == 10)
    check("汇总含共/日均/连续", "共" in win.stats_tab.daily_summary_lbl.text()
          and "日均" in win.stats_tab.daily_summary_lbl.text()
          and "连续" in win.stats_tab.daily_summary_lbl.text())
    check("直方图摘要转小时", "3 小时 5 分钟" in win.stats_tab.summary_lbl.text()
          or "185" not in win.stats_tab.summary_lbl.text())

    # 布局诊断 + 截图（截图本机无法预览，用几何坐标核对是否溢出）
    win.tabs.setCurrentIndex(3)   # 专注统计
    app.processEvents()
    st = win.stats_tab
    print("  [info] window=%dx%d tabs=%dx%d stats_page=%dx%d"
          % (win.width(), win.height(), win.tabs.width(), win.tabs.height(),
             st.width(), st.height()))
    for child in (st.histogram, st.summary_lbl, st.daily_card):
        b = child.mapTo(st, child.rect().bottomLeft()).y()
        r = child.mapTo(st, child.rect().bottomRight()).x()
        ok = b <= st.height() - 2 and r <= st.width() - 2
        check("布局未溢出: %s" % child.objectName(), ok)
        print("    %s bottom=%d right=%d" % (child.objectName(), b, r))
    _save_shot(win, "kaoyan_shot_stats")
    win.tabs.setCurrentIndex(0)   # 今日计划
    app.processEvents()
    pt = win.plan_tab
    b = pt.list.mapTo(pt, pt.list.rect().bottomLeft()).y()
    print("  [info] plan_list bottom=%d plan_tab h=%d" % (b, pt.height()))
    _save_shot(win, "kaoyan_shot_plan")

    # 对话框可渲染（玻璃渐变背景 + 半透明控件）
    dlg = widgets.SettingsDialog(win)
    dlg.show()
    app.processEvents()
    check("设置对话框可渲染", dlg.isVisible())
    dlg.close()
    fd = widgets.FixedTaskDialog(win)
    fd.show()
    app.processEvents()
    check("固定任务对话框可渲染", fd.isVisible())
    fd.close()

    # 深色主题冒烟：换主题后重建窗口，确认玻璃样式与布局不崩
    theme.apply_theme(app, "dark")
    win2 = widgets.FloatWindow()
    win2.show()
    app.processEvents()
    check("深色窗口 objectName", win2.objectName() in ("float_window", "float_window_acrylic"))
    win2.tabs.setCurrentIndex(3)
    app.processEvents()
    check("深色统计页可渲染", win2.stats_tab.daily_list.count() == 10)
    win2.close()
    theme.apply_theme(app, theme.detect_system_theme())

    print("\n=== 结果：%d 项失败 ===" % len(failures))
    app.exit(1 if failures else 0)


QTimer.singleShot(900, run_asserts)
QTimer.singleShot(2500, lambda: (app.exit(1 if failures else 0), None))
app.exec_()
sys.exit(0 if not failures else 1)
