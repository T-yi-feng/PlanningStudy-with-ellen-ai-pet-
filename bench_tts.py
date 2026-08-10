# -*- coding: utf-8 -*-
"""艾莲语音播报·性能分析

本地 GPT-SoVITS 推理服务的性能占用实测。用法（服务起没起都行）：

    python bench_tts.py [--url http://127.0.0.1:9880] [--cmd "启动命令"] [--ref 参考音频.wav] [--prompt "转写文字"] [--plang zh]

--ref 是参考音频路径（GPT-SoVITS 合成必需，音色来源）；没给 --ref 就只测服务加载与占用。
什么都不传、服务已手动启动 → 只测推理性能；
带 --cmd 且服务没起 → 先测「模型加载耗时」，再测推理性能；
服务没起也没给命令 → 输出「未就绪」并对比说明（正好证明关闭即零占用）。
"""
import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request
import wave

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

SAMPLE = "累…不过，你还在学的话，我就陪你一会儿吧。就这？还行。"
N_REPEAT = 3


def arg_parse():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://127.0.0.1:9880")
    p.add_argument("--cmd", default="", help="服务启动命令（可空）")
    p.add_argument("--ref", default="", help="参考音频路径（GPT-SoVITS 合成必需）")
    p.add_argument("--prompt", default="", help="参考音频对应的转写文字（可空）")
    p.add_argument("--plang", default="zh", help="参考音频语言（默认 zh）")
    return p.parse_args()


def post_tts(url, text, ref, prompt, plang, timeout=60):
    payload = json.dumps({
        "text": text, "text_lang": "auto",
        "ref_audio_path": ref,
        "prompt_lang": plang,
        "prompt_text": prompt,
        "text_split_method": "cut5", "speed": 1.0,
    }).encode("utf-8")
    req = urllib.request.Request(
        url.rstrip("/") + "/tts", data=payload,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def reachable(url, timeout=3):
    """服务在监听即视为可达：不带参考音频的探测会被拒（400），但能收到响应就说明服务活着。"""
    import urllib.error
    payload = json.dumps({"text": "ping", "text_lang": "auto"}).encode("utf-8")
    req = urllib.request.Request(
        url.rstrip("/") + "/tts", data=payload,
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            resp.read(16)
        return True
    except urllib.error.HTTPError:
        return True   # 服务回了 400/422 → 在
    except Exception:
        return False  # 连接失败/超时 → 不在


def gpu_mem():
    """返回 (used_MB, total_MB) 或 None。"""
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,nounits,noheader"],
            capture_output=True, text=True, timeout=10).stdout
        used, total = [int(x.strip()) for x in out.split(",")]
        return used, total
    except Exception:
        return None


def proc_info(url):
    """按 TTS 端口找到真正跑模型的服务进程，返回其 CPU/内存/线程数。"""
    try:
        import urllib.parse
        port = urllib.parse.urlparse(url).port or 9880
        import psutil
        pid = None
        for conn in psutil.net_connections("tcp"):
            if conn.status == "LISTEN" and conn.laddr.port == port:
                pid = conn.pid
                break
        if pid is None:
            return None
        p = psutil.Process(pid)
        return {"cpu%": p.cpu_percent(interval=0.3), "rss_MB": round(p.memory_info().rss / 1048576, 1),
                "threads": p.num_threads()}
    except Exception:
        return None


def wav_duration(data):
    try:
        with wave.open(__import__("io").BytesIO(data)) as w:
            return w.getnframes() / float(w.getframerate())
    except Exception:
        return 0.0


def bench_inference(url, ref, prompt, plang):
    print("== 推理性能（%d 次取均值）==" % N_REPEAT)
    lat = []
    rtfs = []
    durations = []
    for i in range(N_REPEAT):
        t0 = time.time()
        data = post_tts(url, SAMPLE, ref, prompt, plang)
        dt = time.time() - t0
        dur = wav_duration(data)
        lat.append(dt)
        durations.append(dur)
        rtfs.append(dt / dur if dur else 0.0)
        print("  #%d 合成 %.2fs / 音频 %.2fs / RTF %.3f" % (i + 1, dt, dur, rtfs[-1]))
    print("  平均：延迟 %.2fs，音频 %.2fs，RTF %.3f" % (
        sum(lat) / N_REPEAT, sum(durations) / N_REPEAT, sum(rtfs) / N_REPEAT))


def main():
    args = arg_parse()
    url = args.url
    ref = args.ref
    prompt = args.prompt
    plang = args.plang or "zh"
    started_pid = None
    t_load = None

    print("目标服务：%s" % url)
    if reachable(url):
        print("服务已在运行 ✓")
    elif args.cmd:
        print("服务未就绪，用 --cmd 拉起并计时模型加载…")
        t0 = time.time()
        try:
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            proc = subprocess.Popen(
                args.cmd, shell=True, creationflags=flags,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except Exception as exc:
            print("启动失败：%s" % exc)
            sys.exit(1)
        started_pid = proc.pid
        deadline = t0 + 180
        while time.time() < deadline:
            if proc.poll() is not None:
                print("进程退出（命令可能有误），退出码=%s" % proc.poll())
                break
            if reachable(url):
                t_load = time.time() - t0
                print("模型加载完成，耗时 %.1f 秒 ✓" % t_load)
                break
            time.sleep(1.0)
        if not reachable(url):
            print("等待超时，服务没起来。")
            proc.terminate()
            sys.exit(1)
    else:
        print("服务未就绪，且没给 --cmd → 什么都不跑，正好验证「关闭即零占用」。")
        g = gpu_mem()
        print("此时显存占用：%s" % ("%d/%d MB" % g if g else "nvidia-smi 不可用"))
        sys.exit(0)

    if not ref:
        print("注：未给 --ref 参考音频，跳过合成性能测试（GPT-SoVITS 必须带参考音频才能合成）。")
        g = gpu_mem()
        if g:
            print("服务加载后显存占用：%d MB（总 %d MB）" % (g[0], g[1]))
        if started_pid:
            info = proc_info(url)
            if info:
                print("服务进程：%s" % info)
        return

    # 显存
    g = gpu_mem()
    if g:
        print("推理时显存占用：%d MB（总 %d MB）" % (g[0], g[1]))

    # 服务进程资源（后台线程里盯住，加载会吃掉峰值）
    if started_pid:
        info = proc_info(url)
        if info:
            print("服务进程（含加载后稳态）：%s" % info)

    bench_inference(url, ref, prompt, plang)

    # 关闭后占用（把刚才拉起的进程杀掉 → 验证零占用）
    if started_pid:
        print("\n== 终止服务进程，验证关闭即零占用 ==")
        try:
            subprocess.run(["taskkill", "/PID", str(started_pid), "/T", "/F"],
                           capture_output=True)
        except Exception:
            pass
        time.sleep(1.5)
        g = gpu_mem()
        print("服务停止后显存占用：%s" % ("%d/%d MB" % g if g else "nvidia-smi 不可用"))
        print("服务停止后是否可达：%s" % ("仍在（外部另有进程）" if reachable(url) else "不可达 ✓（模型已释放）"))


if __name__ == "__main__":
    main()
