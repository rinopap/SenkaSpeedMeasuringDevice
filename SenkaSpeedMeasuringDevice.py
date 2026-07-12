# -*- coding: utf-8 -*-
import os
import json
import csv
import glob
import queue
import threading
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
from datetime import datetime, timedelta
import sys
import urllib.parse
import winreg

from watchdog.observers import Observer
from watchdog.events import FileSystemEventHandler
import pystray
from PIL import Image, ImageDraw

# ------------------------------------------------------------------ #
# パス解決
# ------------------------------------------------------------------ #
if getattr(sys, 'frozen', False):
    BASE_DIR  = os.path.dirname(sys.executable)
    SELF_PATH = sys.executable
else:
    BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
    SELF_PATH = os.path.abspath(__file__)

DATA_DIR      = os.path.join(BASE_DIR, "74en_responses")
CSV_FILE      = os.path.join(BASE_DIR, "exp_log.csv")
SHIP_CSV_FILE = os.path.join(BASE_DIR, "ship_exp_log.csv")
SETUP_FLAG    = os.path.join(BASE_DIR, "first_setup_done.flag")
CLEANUP_FLAG  = os.path.join(BASE_DIR, "last_cleanup.flag")
UI_CONFIG     = os.path.join(BASE_DIR, "ui_config.json")

_STARTUP_REG_KEY  = r"Software\Microsoft\Windows\CurrentVersion\Run"
_STARTUP_REG_NAME = "SenkaKeisoku"

# ------------------------------------------------------------------ #
# UI設定
# ------------------------------------------------------------------ #
AVAILABLE_FONTS = ["Yu Gothic", "メイリオ", "MS Gothic", "Arial", "Courier New"]

DEFAULT_UI = {
    "dark_mode":     False,
    "font_family":   "Yu Gothic",
    "font_size":     12,
    "always_on_top": True,
}

THEME = {
    "light": {
        "bg":        "#f0f0f0",
        "fg":        "#000000",
        "entry_bg":  "#ffffff",
        "entry_fg":  "#000000",
        "label_bg":  "#f0f0f0",
        "frame_bg":  "#f0f0f0",
        "select_bg": "#0078d7",
        "select_fg": "#ffffff",
        "btn_bg":    "#e0e0e0",
        "sub_fg":    "#555555",
    },
    "dark": {
        "bg":        "#202124",
        "fg":        "#E8EAED",
        "entry_bg":  "#2d2d2d",
        "entry_fg":  "#E8EAED",
        "label_bg":  "#202124",
        "frame_bg":  "#202124",
        "select_bg": "#264f78",
        "select_fg": "#ffffff",
        "btn_bg":    "#3a3a3a",
        "sub_fg":    "#9AA0A6",
    },
}

def load_ui_config() -> dict:
    if not os.path.exists(UI_CONFIG):
        return dict(DEFAULT_UI)
    try:
        with open(UI_CONFIG, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        out = dict(DEFAULT_UI)
        out.update(cfg)
        return out
    except Exception:
        return dict(DEFAULT_UI)

def save_ui_config(cfg: dict):
    with open(UI_CONFIG, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)

# ------------------------------------------------------------------ #
# Windowsスタートアップ
# ------------------------------------------------------------------ #
def _get_launch_cmd() -> str:
    if getattr(sys, 'frozen', False):
        return f'"{SELF_PATH}"'
    return f'"{sys.executable}" "{SELF_PATH}"'

def is_startup_registered() -> bool:
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _STARTUP_REG_KEY, 0, winreg.KEY_READ)
        winreg.QueryValueEx(key, _STARTUP_REG_NAME)
        winreg.CloseKey(key)
        return True
    except Exception:
        return False

def register_startup() -> bool:
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _STARTUP_REG_KEY, 0, winreg.KEY_SET_VALUE)
        winreg.SetValueEx(key, _STARTUP_REG_NAME, 0, winreg.REG_SZ, _get_launch_cmd())
        winreg.CloseKey(key)
        return True
    except Exception as e:
        messagebox.showerror("登録エラー", f"スタートアップ登録に失敗しました\n{e}")
        return False

def unregister_startup():
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, _STARTUP_REG_KEY, 0, winreg.KEY_SET_VALUE)
        winreg.DeleteValue(key, _STARTUP_REG_NAME)
        winreg.CloseKey(key)
    except Exception:
        pass

# ------------------------------------------------------------------ #
# メモリキャッシュ
# ------------------------------------------------------------------ #
_exp_cache: list = []

def _load_cache_from_csv():
    global _exp_cache
    if not os.path.exists(CSV_FILE):
        _exp_cache = []
        return
    out = []
    with open(CSV_FILE, "r", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            try:
                out.append((datetime.fromisoformat(row["timestamp"]), int(row["admiral_exp"])))
            except Exception:
                pass
    out.sort(key=lambda x: x[0])
    _exp_cache = out

def _append_cache(timestamp: datetime, admiral_exp: int):
    new_file = not os.path.exists(CSV_FILE)
    with open(CSV_FILE, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new_file:
            w.writerow(["timestamp", "admiral_exp"])
        w.writerow([timestamp.isoformat(), admiral_exp])
    _exp_cache.append((timestamp, admiral_exp))
    _exp_cache.sort(key=lambda x: x[0])

# ------------------------------------------------------------------ #
# 初期設定
# ------------------------------------------------------------------ #
def ensure_initial_setup():
    if os.path.exists(SETUP_FLAG):
        return
    if not os.path.exists(DATA_DIR):
        os.makedirs(DATA_DIR)
    messagebox.showinfo(
        "初期設定",
        "以下の設定を行ってください：\n\n"
        "1. 74en を起動\n"
        "2. ファイル → 設定 → 通信 タブ\n"
        "3. 『通信内容を保存する』にチェック\n"
        "4. 『response』にチェック\n"
        f"5. 保存先フォルダとして以下を指定\n\n{os.path.abspath(DATA_DIR)}"
    )
    with open(SETUP_FLAG, "w", encoding="utf-8") as f:
        f.write("ok")

# ------------------------------------------------------------------ #
# watchdog
# ------------------------------------------------------------------ #
_file_event_queue: queue.Queue = queue.Queue()
_processed_sigs: dict = {}

class _ApiFileHandler(FileSystemEventHandler):
    def _handle(self, path: str):
        if "api_port@port" not in os.path.basename(path):
            return
        try:
            s = os.stat(path)
            sig = (s.st_mtime_ns, s.st_size)
        except OSError:
            return
        if _processed_sigs.get(path) == sig:
            return
        _processed_sigs[path] = sig
        _file_event_queue.put(path)

    def on_created(self, event):
        if not event.is_directory:
            self._handle(event.src_path)

    def on_modified(self, event):
        if not event.is_directory:
            self._handle(event.src_path)

def _start_watchdog():
    if not os.path.exists(DATA_DIR):
        os.makedirs(DATA_DIR)
    observer = Observer()
    observer.schedule(_ApiFileHandler(), DATA_DIR, recursive=False)
    observer.daemon = True
    observer.start()

# ------------------------------------------------------------------ #
# JSONパース
# ------------------------------------------------------------------ #
def parse_api_file(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        content = f.read().strip()
    if content.startswith("svdata="):
        content = content[len("svdata="):]
    content = urllib.parse.unquote(content)
    idx = content.find("{")
    if idx == -1:
        raise ValueError("JSON not found")
    return json.loads(content[idx:])

def file_timestamp(path):
    try:
        return datetime.fromtimestamp(os.path.getmtime(path))
    except OSError:
        return datetime.now()

def extract_exp(data):
    return data.get("api_data", {}).get("api_basic", {}).get("api_experience")

def extract_ship_total_exp(data):
    """全艦娘のapi_exp[0]を合計して艦娘総経験値を返す"""
    ships = data.get("api_data", {}).get("api_ship", [])
    if not ships:
        return None
    return sum(s.get("api_exp", [0])[0] for s in ships)

# ------------------------------------------------------------------ #
# 艦娘経験値キャッシュ
# ------------------------------------------------------------------ #
_ship_exp_cache: list = []

def _load_ship_cache_from_csv():
    global _ship_exp_cache
    if not os.path.exists(SHIP_CSV_FILE):
        _ship_exp_cache = []
        return
    out = []
    with open(SHIP_CSV_FILE, "r", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            try:
                out.append((datetime.fromisoformat(row["timestamp"]), int(row["ship_total_exp"])))
            except Exception:
                pass
    out.sort(key=lambda x: x[0])
    _ship_exp_cache = out

def _append_ship_cache(timestamp: datetime, ship_total_exp: int):
    new_file = not os.path.exists(SHIP_CSV_FILE)
    with open(SHIP_CSV_FILE, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new_file:
            w.writerow(["timestamp", "ship_total_exp"])
        w.writerow([timestamp.isoformat(), ship_total_exp])
    _ship_exp_cache.append((timestamp, ship_total_exp))
    _ship_exp_cache.sort(key=lambda x: x[0])

# ------------------------------------------------------------------ #
# 古いJSON削除（1時間に1回）
# ------------------------------------------------------------------ #
def cleanup_old_json_hourly(now):
    if not os.path.exists(CLEANUP_FLAG):
        with open(CLEANUP_FLAG, "w", encoding="utf-8") as f:
            f.write(now.isoformat())
        return
    try:
        with open(CLEANUP_FLAG, "r", encoding="utf-8") as f:
            last_cleanup = datetime.fromisoformat(f.read().strip())
    except Exception:
        last_cleanup = None
    if last_cleanup and (now - last_cleanup) < timedelta(hours=1):
        return
    threshold = now - timedelta(days=2)
    deleted = 0
    for path in glob.glob(os.path.join(DATA_DIR, "*.json")):
        try:
            if datetime.fromtimestamp(os.path.getmtime(path)) < threshold:
                os.remove(path)
                deleted += 1
        except Exception as e:
            print("Delete error:", path, e)
    print(f"[Cleanup] Deleted: {deleted}")
    with open(CLEANUP_FLAG, "w", encoding="utf-8") as f:
        f.write(now.isoformat())
    global _processed_sigs
    _processed_sigs = {p: sig for p, sig in _processed_sigs.items() if os.path.exists(p)}


# ------------------------------------------------------------------ #
# 戦果計算
# ------------------------------------------------------------------ #
def get_first_record_in_hour(data, hour_start):
    hour_end = hour_start + timedelta(hours=1)
    cands = [(ts, exp) for ts, exp in data if hour_start <= ts < hour_end]
    return min(cands, key=lambda x: x[0]) if cands else None

def calc_senka_speed(rec1, rec2):
    if rec1 is None or rec2 is None:
        return None
    t1, e1 = rec1
    t2, e2 = rec2
    if t2 <= t1 or e2 < e1:
        return None
    hours = (t2 - t1).total_seconds() / 3600
    return (e2 - e1) * 7 / 10000 / hours if hours else None


# ------------------------------------------------------------------ #
# キュー処理
# ------------------------------------------------------------------ #
def drain_file_queue():
    updated = False
    while not _file_event_queue.empty():
        try:
            path = _file_event_queue.get_nowait()
        except queue.Empty:
            break
        try:
            data     = parse_api_file(path)
            exp      = extract_exp(data)
            ship_exp = extract_ship_total_exp(data)
            ts       = file_timestamp(path)
            if exp is not None:
                _append_cache(ts, exp)
                updated = True
            if ship_exp is not None:
                _append_ship_cache(ts, ship_exp)
        except Exception as e:
            print("Error:", path, e)
    if updated:
        recalc_all_display()
    cleanup_old_json_hourly(datetime.now())
    root.after(500, drain_file_queue)

# ------------------------------------------------------------------ #
# テーマ
# ------------------------------------------------------------------ #
def get_current_theme() -> dict:
    return THEME["dark"] if ui_cfg["dark_mode"] else THEME["light"]

def get_font(bold=False, size_delta=0) -> tuple:
    return (ui_cfg["font_family"], ui_cfg["font_size"] + size_delta, "bold" if bold else "normal")

def apply_theme(target_root=None):
    """メインウィンドウまたは指定ウィンドウにテーマを適用する"""
    t = get_current_theme()
    r = target_root or root
    r.configure(bg=t["bg"])

    style = ttk.Style()
    style.theme_use("default")
    style.configure("TNotebook",     background=t["bg"], borderwidth=0)
    style.configure("TNotebook.Tab", background=t["bg"], foreground=t["fg"],
                    padding=[6, 2], font=get_font())
    style.map("TNotebook.Tab",
              background=[("selected", t["select_bg"])],
              foreground=[("selected", t["select_fg"])])
    style.configure("TFrame",    background=t["frame_bg"])
    style.configure("TEntry",    fieldbackground=t["entry_bg"],
                    foreground=t["entry_fg"], insertcolor=t["entry_fg"], font=get_font())
    style.configure("TSpinbox",  fieldbackground=t["entry_bg"],
                    foreground=t["entry_fg"], font=get_font())
    style.configure("TCombobox", fieldbackground=t["entry_bg"],
                    foreground=t["entry_fg"], font=get_font())

    for w in all_themed_widgets:
        cls = w.winfo_class()
        try:
            if cls == "Label":
                w.configure(bg=t["label_bg"], fg=t["fg"], font=get_font(bold=True))
            elif cls == "Checkbutton":
                w.configure(bg=t["bg"], fg=t["fg"],
                            activebackground=t["bg"], activeforeground=t["fg"],
                            selectcolor=t["bg"], font=get_font())
        except tk.TclError:
            pass

    try:
        label_speed.configure(bg=t["label_bg"], fg=t["fg"], font=get_font(bold=True, size_delta=2))
        label_ship_speed.configure(bg=t["label_bg"], fg=t["fg"], font=get_font(bold=True, size_delta=2))
        btn_settings.configure(bg=t["btn_bg"], fg=t["fg"],
                               activebackground=t["select_bg"], activeforeground=t["select_fg"])
    except Exception:
        pass

# ------------------------------------------------------------------ #
# 表示更新
# ------------------------------------------------------------------ #
def recalc_all_display():
    now = datetime.now()
    h3 = now.replace(minute=0, second=0, microsecond=0)
    h2 = h3 - timedelta(hours=1)
    h1 = h3 - timedelta(hours=2)
    show = lambda v: "記録不足" if v is None else f"{v:.2f}"

    # ---- 戦果時速 ----
    data = _exp_cache
    if not data:
        label_speed.config(text="データなし")
    else:
        r1 = get_first_record_in_hour(data, h1)
        r2 = get_first_record_in_hour(data, h2)
        r3 = get_first_record_in_hour(data, h3)
        latest = data[-1]
        s1    = calc_senka_speed(r1, r2)
        s2    = calc_senka_speed(r2, r3)
        s_now = calc_senka_speed(r3, latest)
        label_speed.config(text=(
            f"・{h1.strftime('%H:%M')}~{h2.strftime('%H:%M')} 戦果時速 {show(s1)}\n"
            f"・{h2.strftime('%H:%M')}~{h3.strftime('%H:%M')} 戦果時速 {show(s2)}\n"
            f"・{h3.strftime('%H:%M')}~{now.strftime('%H:%M')} 戦果時速 {show(s_now)}"
        ))

    # ---- 艦娘経験値時速 ----
    ship_data = _ship_exp_cache
    if not ship_data:
        label_ship_speed.config(text="データなし")
    else:
        sr1 = get_first_record_in_hour(ship_data, h1)
        sr2 = get_first_record_in_hour(ship_data, h2)
        sr3 = get_first_record_in_hour(ship_data, h3)
        s_latest = ship_data[-1]

        def calc_ship_speed(rec1, rec2):
            if rec1 is None or rec2 is None:
                return None
            t1, e1 = rec1
            t2, e2 = rec2
            if t2 <= t1 or e2 < e1:
                return None
            hours = (t2 - t1).total_seconds() / 3600
            return (e2 - e1) / hours if hours else None

        ss1    = calc_ship_speed(sr1, sr2)
        ss2    = calc_ship_speed(sr2, sr3)
        ss_now = calc_ship_speed(sr3, s_latest)

        show_ship = lambda v: "記録不足" if v is None else f"{v:,.0f}"
        label_ship_speed.config(text=(
            f"・{h1.strftime('%H:%M')}~{h2.strftime('%H:%M')} Exp時速 {show_ship(ss1)}\n"
            f"・{h2.strftime('%H:%M')}~{h3.strftime('%H:%M')} Exp時速 {show_ship(ss2)}\n"
            f"・{h3.strftime('%H:%M')}~{now.strftime('%H:%M')} Exp時速 {show_ship(ss_now)}"
        ))


# ------------------------------------------------------------------ #
# 設定ウィンドウ
# ------------------------------------------------------------------ #
_settings_win = None  # 多重起動防止用

def open_settings():
    global _settings_win
    if _settings_win and tk.Toplevel.winfo_exists(_settings_win):
        _settings_win.lift()
        _settings_win.focus_force()
        return

    win = tk.Toplevel(root)
    win.title("設定")
    win.resizable(False, False)
    win.attributes("-topmost", True)
    _settings_win = win

    t = get_current_theme()
    win.configure(bg=t["bg"])

    spad = {"padx": 10, "pady": 4}
    row  = 0

    # ---- ローカルなウィジェットリスト（設定ウィンドウ専用） ----
    sw: list = []  # apply_theme_sub で一括塗り替えるリスト

    def mk_label(text, **kw):
        l = tk.Label(win, text=text, bg=t["bg"], fg=t["fg"], font=get_font())
        sw.append(l)
        return l

    def apply_theme_sub():
        t2 = get_current_theme()
        win.configure(bg=t2["bg"])
        for w in sw:
            cls = w.winfo_class()
            try:
                if cls == "Label":
                    w.configure(bg=t2["label_bg"], fg=t2["fg"], font=get_font())
                elif cls == "Checkbutton":
                    w.configure(bg=t2["bg"], fg=t2["fg"],
                                activebackground=t2["bg"], activeforeground=t2["fg"],
                                selectcolor=t2["bg"], font=get_font())
            except tk.TclError:
                pass
        lbl_preview.configure(bg=t2["label_bg"], fg=t2["fg"],
                               font=(font_var_s.get(), size_var_s.get(), "bold"))

    # ── 外観 ──────────────────────────────────
    sec1 = mk_label("【外観】")
    sec1.grid(row=row, column=0, columnspan=3, sticky="w", padx=10, pady=(8, 0))
    row += 1

    mk_label("ダークモード").grid(row=row, column=0, sticky="w", **spad)
    dark_var_s = tk.BooleanVar(value=ui_cfg["dark_mode"])
    chk = tk.Checkbutton(win, variable=dark_var_s, text="有効",
                         bg=t["bg"], fg=t["fg"],
                         activebackground=t["bg"], activeforeground=t["fg"],
                         selectcolor=t["bg"], font=get_font())
    chk.grid(row=row, column=1, sticky="w", **spad)
    sw.append(chk)
    row += 1

    mk_label("フォント").grid(row=row, column=0, sticky="w", **spad)
    font_var_s = tk.StringVar(value=ui_cfg["font_family"])
    combo = ttk.Combobox(win, textvariable=font_var_s,
                         values=AVAILABLE_FONTS, state="readonly", width=14)
    combo.grid(row=row, column=1, sticky="w", **spad)
    row += 1

    mk_label("フォントサイズ").grid(row=row, column=0, sticky="w", **spad)
    size_var_s = tk.IntVar(value=ui_cfg["font_size"])
    spin = ttk.Spinbox(win, from_=8, to=24, textvariable=size_var_s, width=5)
    spin.grid(row=row, column=1, sticky="w", **spad)
    row += 1

    lbl_preview = tk.Label(win, text="プレビュー：戦果時速 12.34",
                            bg=t["bg"], fg=t["fg"],
                            font=(ui_cfg["font_family"], ui_cfg["font_size"], "bold"))
    lbl_preview.grid(row=row, column=0, columnspan=2, sticky="w", padx=10, pady=(0, 4))
    row += 1

    def on_preview(*_):
        try:
            lbl_preview.configure(font=(font_var_s.get(), size_var_s.get(), "bold"))
        except Exception:
            pass

    font_var_s.trace_add("write", on_preview)
    size_var_s.trace_add("write", on_preview)

    # ── 起動・表示 ──────────────────────────────
    sep = ttk.Separator(win, orient="horizontal")
    sep.grid(row=row, column=0, columnspan=3, sticky="ew", padx=8, pady=4)
    row += 1

    sec2 = mk_label("【表示】")
    sec2.grid(row=row, column=0, columnspan=3, sticky="w", padx=10, pady=(0, 0))
    row += 1

    mk_label("常に最前面に表示\n(74enへの取り込み時はOFF)").grid(
        row=row, column=0, sticky="w", **spad)
    topmost_var_s = tk.BooleanVar(value=ui_cfg.get("always_on_top", True))
    chk_top = tk.Checkbutton(win, variable=topmost_var_s, text="有効",
                              bg=t["bg"], fg=t["fg"],
                              activebackground=t["bg"], activeforeground=t["fg"],
                              selectcolor=t["bg"], font=get_font())
    chk_top.grid(row=row, column=1, sticky="w", **spad)
    sw.append(chk_top)
    row += 1

    # ── スタートアップ ──────────────────────────
    sep2 = ttk.Separator(win, orient="horizontal")
    sep2.grid(row=row, column=0, columnspan=3, sticky="ew", padx=8, pady=4)
    row += 1

    sec3 = mk_label("【起動】")
    sec3.grid(row=row, column=0, columnspan=3, sticky="w", padx=10, pady=(0, 0))
    row += 1

    mk_label("Windowsログオン時に\nこのソフトを自動起動").grid(
        row=row, column=0, sticky="w", **spad)
    auto_var_s = tk.BooleanVar(value=is_startup_registered())
    chk_auto = tk.Checkbutton(win, variable=auto_var_s, text="有効",
                               bg=t["bg"], fg=t["fg"],
                               activebackground=t["bg"], activeforeground=t["fg"],
                               selectcolor=t["bg"], font=get_font())
    chk_auto.grid(row=row, column=1, sticky="w", **spad)
    sw.append(chk_auto)
    row += 1

    # ── 適用・閉じるボタン ──────────────────────
    sep_last = ttk.Separator(win, orient="horizontal")
    sep_last.grid(row=row, column=0, columnspan=3, sticky="ew", padx=8, pady=4)
    row += 1

    def on_apply():
        ui_cfg["dark_mode"]     = dark_var_s.get()
        ui_cfg["font_family"]   = font_var_s.get()
        ui_cfg["font_size"]     = size_var_s.get()
        ui_cfg["always_on_top"] = topmost_var_s.get()
        save_ui_config(ui_cfg)

        # 常に最前面を即座に反映
        root.attributes("-topmost", ui_cfg["always_on_top"])

        if auto_var_s.get():
            if not register_startup():
                auto_var_s.set(False)
        else:
            unregister_startup()

        apply_theme()
        apply_theme_sub()
        root.after(50, _fix_window_size)  # フォント変更後にサイズ再固定

    btn_frame = tk.Frame(win, bg=t["bg"])
    btn_frame.grid(row=row, column=0, columnspan=3, pady=(0, 8))
    ttk.Button(btn_frame, text="適用",   command=on_apply).pack(side="left", padx=6)
    ttk.Button(btn_frame, text="閉じる", command=win.destroy).pack(side="left", padx=6)

# ------------------------------------------------------------------ #
# タスクトレイ
# ------------------------------------------------------------------ #
def _make_tray_icon():
    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([4, 4, size - 4, size - 4], fill=(70, 130, 180, 255))
    return img

def _build_tray():
    def on_show(icon, item):
        root.after(0, lambda: (root.deiconify(), root.lift(), root.focus_force()))

    def on_settings(icon, item):
        root.after(0, open_settings)

    def on_quit(icon, item):
        icon.stop()
        root.after(0, root.destroy)

    menu = pystray.Menu(
        pystray.MenuItem("ウィンドウを開く", on_show, default=True),
        pystray.MenuItem("設定",             on_settings),
        pystray.MenuItem("終了",             on_quit),
    )
    return pystray.Icon("senka", _make_tray_icon(), "戦果時速計測機", menu)

def _on_close():
    root.withdraw()

# ------------------------------------------------------------------ #
# メインGUI構築
# ------------------------------------------------------------------ #
ui_cfg = load_ui_config()

root = tk.Tk()
root.title("戦果時速計測機")
root.attributes("-topmost", ui_cfg.get("always_on_top", True))
root.protocol("WM_DELETE_WINDOW", _on_close)
root.resizable(False, False)

all_themed_widgets: list = []

# ---- ノートブック（タブ2枚）----
notebook = ttk.Notebook(root)
notebook.pack(side="top", fill="both", expand=True)

# タブ1：戦果時速
frame1 = ttk.Frame(notebook)
notebook.add(frame1, text="戦果時速")

label_speed = tk.Label(frame1, justify="left")
label_speed.pack(padx=10, pady=8)
all_themed_widgets.append(label_speed)

# タブ2：艦娘経験値時速
frame2 = ttk.Frame(notebook)
notebook.add(frame2, text="艦娘Exp時速")

label_ship_speed = tk.Label(frame2, justify="left")
label_ship_speed.pack(padx=10, pady=8)
all_themed_widgets.append(label_ship_speed)

# ダミーリスト
goal_entries   = []
landing_labels = []

# ---- ⚙ 設定ボタン（右下に小さく配置）----
btn_settings = tk.Button(
    root,
    text="⚙ 設定",
    command=open_settings,
    relief="flat",
    padx=4, pady=1,
    font=("Yu Gothic", 9),
    cursor="hand2",
)
btn_settings.pack(side="right", anchor="se", padx=4, pady=2)

def _fix_window_size():
    fam  = ui_cfg["font_family"]
    size = ui_cfg["font_size"]
    # 最長パターン：艦娘Exp時速（数値が大きくカンマ付きになる）
    dummy = (
        "・00:00~00:00 Exp時速 記録不足\n"
        "・00:00~00:00 Exp時速 記録不足\n"
        "・00:00~00:00 Exp時速 記録不足"
    )
    label_speed.config(text=dummy, font=(fam, size + 2, "bold"))
    label_ship_speed.config(text=dummy, font=(fam, size + 2, "bold"))
    root.update_idletasks()
    w = root.winfo_reqwidth()
    h = root.winfo_reqheight()
    root.geometry(f"{w}x{h}")
    root.resizable(False, False)
    recalc_all_display()

# ------------------------------------------------------------------ #
# 起動処理
# ------------------------------------------------------------------ #
ensure_initial_setup()
_load_cache_from_csv()
_load_ship_cache_from_csv()
apply_theme()
_fix_window_size()

for _p in glob.glob(os.path.join(DATA_DIR, "*api_port@port.json")):
    try:
        _s = os.stat(_p)
        _processed_sigs[_p] = (_s.st_mtime_ns, _s.st_size)
    except OSError:
        pass

_start_watchdog()
root.after(500, drain_file_queue)

_tray = _build_tray()
threading.Thread(target=_tray.run, daemon=True).start()

root.mainloop()
