#!/usr/bin/env python3
"""读取 game_state.json，输出带中文名的完整 JSON。"""
import json
import sys
import os
from pathlib import Path

# === 路径 ===
SCRIPT_DIR = Path(__file__).resolve().parent
GAME_STATE = SCRIPT_DIR.parent / "BoardData" / "game_state.json"
CARDS_DB = SCRIPT_DIR / "data" / "cards_generated.json"
TRANS_ZH = SCRIPT_DIR / "data" / "translations_zh_cn.json"
OUTPUT = SCRIPT_DIR.parent / "BoardData" / "game_state_zh.json"

# === 加载翻译映射 ===
def load_name_map():
    """返回 template_id → 中文名 的映射字典"""
    tid_to_zh = {}

    # 1. 加载 cards_generated.json (key=internal_name, value={template_id,...})
    if CARDS_DB.exists():
        with open(CARDS_DB, "r", encoding="utf-8") as f:
            cards = json.load(f)
        # 构建 template_id → internal_name
        tid_to_en = {}
        for name, info in cards.items():
            if isinstance(info, dict):
                tid = info.get("template_id", "")
                if tid:
                    tid_to_en[tid] = name

    # 2. 加载中文翻译 (by_name: internal_name → 中文)
    zh_by_name = {}
    if TRANS_ZH.exists():
        with open(TRANS_ZH, "r", encoding="utf-8") as f:
            zh = json.load(f)
        zh_by_name = zh.get("by_name", {})

    # 3. 合并: template_id → 中文
    for tid, en_name in tid_to_en.items():
        tid_to_zh[tid] = zh_by_name.get(en_name, en_name)

    return tid_to_zh

# === 转换单张卡牌 ===
def fmt_card(card, name_map):
    tid = card.get("template_id", "")
    return {
        "id": card.get("id", ""),
        "template_id": tid,
        "name": name_map.get(tid, "???"),
        "rarity": card.get("rarity", ""),
        "section": card.get("section", ""),
        "card_type": card.get("card_type", ""),
        "enchantments": card.get("enchantments", []),
    }

# === 主逻辑 ===
def main():
    if not GAME_STATE.exists():
        print(f"[ERROR] 找不到 {GAME_STATE}")
        sys.exit(1)

    with open(GAME_STATE, "r", encoding="utf-8") as f:
        state = json.load(f)

    name_map = load_name_map()
    print(f"[INFO] 名称映射: {len(name_map)} 条")

    # 事件名称
    event_names = []
    for evt in state.get("event_options_detailed", []):
        eid = evt.get("id", "")
        etid = evt.get("template_id", "")
        event_names.append(name_map.get(etid, etid[:30] if etid else eid))

    # 当前商店
    shop = state.get("current_shop")
    shop_zh = None
    if shop:
        shop_zh = {
            "refresh_available": shop.get("refresh_available"),
            "refresh_cost": shop.get("refresh_cost"),
            "refreshes_remaining": shop.get("refreshes_remaining"),
            "visible_items": [fmt_card(c, name_map) for c in shop.get("visible_items", [])],
        }

    result = {
        "source": "BazaarStateExporter (我们的)",
        "updated_at_utc": state.get("updated_at_utc", ""),
        # 基础信息
        "hero": name_map.get(state.get("hero", ""), state.get("hero", "")),
        "day": state.get("day"),
        "gold": state.get("gold"),
        "health": state.get("health"),
        "combat_health": state.get("combat_health"),
        "income": state.get("income"),
        "prestige": state.get("prestige"),
        "max_prestige": state.get("max_prestige"),
        "level": state.get("level"),
        "xp": state.get("xp"),
        "inventory_slots_used": state.get("inventory_slots_used"),
        "inventory_slots_total": state.get("inventory_slots_total"),
        # 面板物品
        "board_items": [fmt_card(c, name_map) for c in state.get("board_items", [])],
        "board_items_count": len(state.get("board_items", [])),
        # 背包物品
        "stash_items": [fmt_card(c, name_map) for c in state.get("stash_items", [])],
        "stash_items_count": len(state.get("stash_items", [])),
        # 技能
        "skills": [fmt_card(c, name_map) for c in state.get("skills", [])],
        "skills_count": len(state.get("skills", [])),
        # 事件选项
        "event_options": {
            "ids": state.get("event_option_ids", []),
            "names": event_names,
        },
        # 商店
        "current_shop": shop_zh,
        # 当前奖励
        "current_reward_options": [fmt_card(c, name_map) for c in state.get("current_reward_options", [])],
    }

    os.makedirs(OUTPUT.parent, exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print(f"[OK] 输出: {OUTPUT}")
    print(f"     英雄={result['hero']} 天数={result['day']} 金币={result['gold']} 血量={result['health']}")
    print(f"     面板={result['board_items_count']} 背包={result['stash_items_count']} 技能={result['skills_count']}")
    print(f"     事件={len(event_names)} 个")

if __name__ == "__main__":
    main()
