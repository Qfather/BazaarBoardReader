#!/usr/bin/env python3
"""
The Bazaar 游戏数据热键测试脚本
-----------------------------------
运行游戏时，按 F8 即可读取并显示：
  英雄、天数、收入、金币、对阵区物品、背包物品、血量、声望

数据来源（自动选择）：
  1. BazaarStateExporter 插件 -> game_state.json（优先）
  2. BazaarBoardReader  插件 -> board_latest.json（回退）

启动方式: python test_hotkey.py
"""

import json
import os
import sys
import time
from pathlib import Path

# --- 修复 Windows 控制台 GBK 编码 ---
if sys.platform == "win32":
    os.system("chcp 65001 > nul 2>&1")
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

try:
    import keyboard
except ImportError:
    print("[INFO] Installing keyboard library...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "keyboard"])
    import keyboard
    print("[INFO] Done!")

# ======================== 配置 ========================
# 自动检测游戏根目录（从脚本位置向上查找 TheBazaar.exe）
_script_dir = Path(__file__).resolve().parent
GAME_DIR = _script_dir
while GAME_DIR != GAME_DIR.parent and not (GAME_DIR / "TheBazaar.exe").exists():
    GAME_DIR = GAME_DIR.parent

if not (GAME_DIR / "TheBazaar.exe").exists():
    print(f"[ERROR] 找不到游戏根目录（未找到 TheBazaar.exe）")
    print(f"       脚本位置: {_script_dir}")
    sys.exit(1)

# 主数据源：我们自己的 StateExporter 实时数据
STATE_JSON = GAME_DIR / "BoardData" / "game_state.json"
# 名称映射源：BazaarBoardReader 导出的中文名
BOARD_JSON = GAME_DIR / "BoardData" / "board_latest.json"
HOTKEY = "F8"
print(f"[INFO] 游戏目录: {GAME_DIR}")
print(f"[INFO] 数据源: {STATE_JSON}")
# =====================================================

OK = "[OK]"
MISS = "[--]"
SEP = "=" * 60
SEP2 = "-" * 60


def read_json(path: Path) -> dict | None:
    if not path.exists():
        return None
    try:
        age = time.time() - path.stat().st_mtime
        with open(path, "r", encoding="utf-8-sig") as f:
            data = json.load(f)
        return {"data": data, "age": age}
    except Exception:
        return None


def fmt_card(card: dict | str) -> str:
    """格式化单张卡牌"""
    if isinstance(card, str):
        return card
    name = card.get("name", "???")
    rarity = card.get("rarity", "") or card.get("tier", "")
    ench = card.get("enchantments", [])
    if isinstance(ench, list) and ench:
        name = f"{name}[{','.join(ench)}]"
    price = card.get("price")
    parts = [name]
    if rarity:
        parts.append(f"({rarity})")
    if price is not None:
        parts.append(f"${price}")
    return " ".join(str(p) for p in parts)


def fmt_list(items: list | None) -> list[str]:
    if not items or not isinstance(items, list):
        return []
    return [fmt_card(c) for c in items]


def print_row(label: str, value: str, status: str = OK):
    print(f"  {status} {label:20s} {value}")


def build_name_map(board_data: dict | None) -> dict:
    """从 board_latest.json 构建 template_id/instance_id -> 中文名 映射"""
    name_map = {}
    if not board_data:
        return name_map
    for section in ["BoardItems", "StorageItems", "SkillCards", "OpponentItems"]:
        cards = board_data.get(section, [])
        if not isinstance(cards, list):
            continue
        for c in cards:
            if not isinstance(c, dict):
                continue
            name = c.get("Name", "")
            tid = c.get("TemplateId", "")
            iid = c.get("InstanceId", "")
            if name and name != "???":
                if tid:
                    name_map[tid] = name
                if iid:
                    name_map[iid] = name
    return name_map


def resolve_name(card: dict, name_map: dict) -> str:
    """解析卡牌名称：优先用映射表，否则显示 ??? """
    tid = card.get("template_id", "")
    iid = card.get("id", "")
    # 先查映射表
    if tid and tid in name_map:
        return name_map[tid]
    if iid and iid in name_map:
        return name_map[iid]
    # 直接有 name 字段
    n = card.get("name", "")
    if n and n != "???":
        return n
    return "???"


def display(state_result: dict | None, board_result: dict | None):
    os.system("cls" if os.name == "nt" else "clear")
    print(SEP)
    print("       The Bazaar - Game State Test")
    print(SEP)

    # 合并数据源：优先 game_state.json 的数值 + board_latest.json 的名称
    state_data = state_result["data"] if state_result else None
    board_data = board_result["data"] if board_result else None

    if state_data is None and board_data is None:
        print()
        print("  [!!] No data file found!")
        print(f"       game_state.json: {STATE_JSON}")
        print(f"       board_latest.json: {BOARD_JSON}")
        print()
        print("  Make sure the game is running and in a run.")
        print(SEP)
        return

    name_map = build_name_map(board_data)

    # 来源信息
    sources = []
    if state_result:
        sources.append(f"game_state ({state_result['age']:.0f}s)")
    if board_result:
        sources.append(f"board_latest ({board_result['age']:.0f}s)")
    print(f"  Source : {' + '.join(sources)}")
    print(f"  Names  : {len(name_map)} mapped from board_latest.json")

    ts = (state_data or board_data).get("updated_at_utc", (state_data or board_data).get("Timestamp", "?"))
    print(f"  Time   : {ts}")
    print(SEP2)

    # === 数值字段：优先 game_state.json ===
    sd = state_data or {}
    bd = board_data or {}

    hero = sd.get("hero") or bd.get("Hero")
    print_row("Hero", str(hero) if hero else "NOT FOUND", OK if hero else MISS)

    day = sd.get("day") or bd.get("Day")
    print_row("Day", str(day) if day else "?", OK if day else MISS)

    gold = sd.get("gold") or bd.get("Gold")
    print_row("Gold", str(gold) if gold is not None else "?", OK if gold else MISS)

    income = sd.get("income") or bd.get("Income")
    print_row("Income", f"{income}/day" if income is not None else "?", OK if income is not None else MISS)

    health = sd.get("health") or sd.get("combat_health") or bd.get("Health")
    print_row("Health", str(health) if health else "?", OK if health else MISS)

    prestige = sd.get("prestige") or bd.get("Prestige")
    max_p = sd.get("max_prestige") or 20
    print_row("Prestige", f"{prestige}/{max_p}" if prestige is not None else "?", OK if prestige else MISS)

    level = sd.get("level") or bd.get("Level")
    xp = sd.get("xp") or bd.get("XP")
    if level or xp:
        lv = f"Lv.{level}" if level else "Lv.?"
        xp_s = f" ({xp} XP)" if xp else ""
        print_row("Level", f"{lv}{xp_s}", OK)

    print(SEP2)

    # === 物品：优先 game_state.json（更全），名称用 name_map 解析 ===
    def print_cards(label: str, cards: list | None):
        if not cards or not isinstance(cards, list):
            print(f"  [--] {label}: (empty)")
            return
        resolved = []
        for c in cards:
            if not isinstance(c, dict):
                resolved.append(str(c))
            else:
                name = resolve_name(c, name_map)
                rarity = c.get("rarity", "") or c.get("tier", "")
                ench = c.get("enchantments", [])
                if isinstance(ench, list) and ench:
                    name = f"{name}[{','.join(ench)}]"
                price = c.get("price")
                parts = [name]
                if rarity:
                    parts.append(f"({rarity})")
                if price is not None:
                    parts.append(f"${price}")
                resolved.append(" ".join(str(p) for p in parts))

        print(f"  [OK] {label} ({len(resolved)}):")
        for i, item in enumerate(resolved, 1):
            print(f"        [{i}] {item}")

    # Board
    board = sd.get("board_items") or bd.get("BoardItems")
    print_cards("Board Items", board)

    print()

    # Stash
    stash = sd.get("stash_items") or bd.get("StorageItems")
    print_cards("Stash/Backpack", stash)

    # Skills
    skills = sd.get("skills") or bd.get("SkillCards")
    if skills:
        print()
        print_cards("Skills", skills)

    # Events
    events = sd.get("event_option_ids") or sd.get("event_options") or bd.get("EventOptions")
    if events and isinstance(events, list) and len(events) > 0:
        print(f"\n  [OK] Current Options ({len(events)}):")
        for i, e in enumerate(events, 1):
            print(f"        [{i}] {e}")

    # Shop
    shop = sd.get("current_shop") or bd.get("CurrentShop")
    if shop and isinstance(shop, dict):
        shop_items = shop.get("visible_items", shop.get("Items", []))
        if shop_items:
            print(f"\n  [OK] Current Shop:")
            for si in shop_items:
                if isinstance(si, dict):
                    n = si.get("Name", si.get("name", "???"))
                    print(f"        - {n}")
                else:
                    print(f"        - {si}")
            rr = shop.get("refreshes_remaining", shop.get("RefreshesRemaining"))
            rc = shop.get("refresh_cost", shop.get("RefreshCost"))
            if rr is not None:
                extra = f" (cost ${rc})" if rc else ""
                print(f"        Refreshes left: {rr}{extra}")

    print(SEP)
    print(f"  Press {HOTKEY} to refresh | Ctrl+C to exit")
    print(SEP)


def main():
    print(SEP)
    print("       The Bazaar - Game State Hotkey Test")
    print(SEP)
    print(f"\n  Hotkey: {HOTKEY}")
    print(f"  Source 1: {STATE_JSON}")
    print(f"  Source 2: {BOARD_JSON} (fallback)")
    print(f"\n  Start the game, enter a run, then press {HOTKEY}.")
    print("  Ctrl+C to exit.")
    print(SEP)

    # Initial display
    display(read_json(STATE_JSON), read_json(BOARD_JSON))

    # Hotkey
    def on_hotkey():
        display(read_json(STATE_JSON), read_json(BOARD_JSON))

    keyboard.add_hotkey(HOTKEY, on_hotkey)
    print(f"\n  Listening for {HOTKEY}...\n")

    try:
        keyboard.wait()
    except KeyboardInterrupt:
        print("\n\nExiting.")
        sys.exit(0)


if __name__ == "__main__":
    if "--once" in sys.argv:
        display(read_json(STATE_JSON), read_json(BOARD_JSON))
    else:
        main()
