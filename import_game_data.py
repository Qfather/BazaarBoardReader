#!/usr/bin/env python3
"""从游戏缓存(GameData.db + zh-CN.bytes)生成所有数据文件"""
import json, sqlite3, sys
from pathlib import Path

GAME_CACHE = Path.home() / "AppData" / "LocalLow" / "Tempo Storm" / "The Bazaar" / "prod" / "cache"
GAME_DB = GAME_CACHE / "GameData.db"
TRANS_DB = GAME_CACHE / "translations" / "zh-CN.bytes"
OUT_DIR = Path(__file__).resolve().parent / "data"

def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # === 1. 加载翻译表 (TranslationKey → 中文) ===
    zh_map = {}
    with sqlite3.connect(str(TRANS_DB)) as conn:
        for key, text in conn.execute("SELECT hash, text FROM translation").fetchall():
            if text:
                zh_map[str(key)] = str(text)
    print(f"[1/4] 翻译条目: {len(zh_map)}")

    # === 2. 读取游戏数据 ===
    cards_generated = {}     # cards_generated.json
    cards_compact = {}       # cards.json (shop_browser 用的格式)
    by_name = {}             # translations  by_name
    by_id = {}               # translations  by_id

    with sqlite3.connect(str(GAME_DB)) as conn:
        for table in ["cards", "monsters"]:
            try:
                rows = conn.execute(f"SELECT Id, Data FROM [{table}]").fetchall()
            except:
                continue
            for record_id, raw in rows:
                try:
                    d = json.loads(raw)
                except:
                    continue

                tid = str(d.get("TemplateId") or d.get("Id", record_id))
                iname = d.get("InternalName", "")
                if not iname:
                    continue

                card_type = d.get("Type", "")
                heroes = d.get("Heroes", [])
                tiers = list((d.get("Tiers") or {}).keys())
                starting_tier = d.get("StartingTier", tiers[0] if tiers else "")

                # 翻译
                loc = d.get("Localization") or {}
                title = loc.get("Title") or {}
                key = title.get("Key") or d.get("TranslationKey")
                zh = zh_map.get(str(key)) if key else None
                if zh and iname:
                    by_name[iname] = zh
                    by_id[tid] = zh

                # === cards_generated.json 格式 ===
                cards_generated[iname] = {
                    "template_id": tid,
                    "internal_name": iname,
                    "name": d.get("Name", iname),
                    "type": card_type,
                    "size": d.get("Size", ""),
                    "hero": d.get("Hero", ""),
                    "heroes": heroes,
                    "tiers": tiers,
                    "tags": d.get("Tags", []),
                }

                # === cards.json 格式 (shop_browser 用) ===
                cards_compact[iname] = {
                    "internal_name": iname,
                    "display_name": zh or iname,
                    "type": card_type,
                    "heroes": heroes,
                    "tags": d.get("Tags", []),
                    "hidden_tags": d.get("HiddenTags", []),
                    "size": d.get("Size", ""),
                    "starting_tier": str(starting_tier),
                    "tiers": tiers,
                }

        print(f"[2/4] 数据: {len(cards_generated)} 条 (cards + monsters)")

    # === 3. 写入 ===
    # cards_generated.json
    with open(OUT_DIR / "cards_generated.json", "w", encoding="utf-8") as f:
        json.dump(cards_generated, f, ensure_ascii=False, indent=2)

    # cards.json (shop_browser 兼容)
    with open(OUT_DIR / "cards.json", "w", encoding="utf-8") as f:
        json.dump({"cards": cards_compact}, f, ensure_ascii=False, indent=2)

    # translations_zh_cn.json
    with open(OUT_DIR / "translations_zh_cn.json", "w", encoding="utf-8") as f:
        json.dump({
            "source_game_db": str(GAME_DB),
            "source_translation_db": str(TRANS_DB),
            "locale": "zh-CN",
            "by_name": dict(sorted(by_name.items())),
            "by_id": dict(sorted(by_id.items())),
        }, f, ensure_ascii=False, indent=2)

    print(f"[3/4] cards_generated.json: {len(cards_generated)} 张")
    print(f"     cards.json: {len(cards_compact)} 张")
    print(f"     translations_zh_cn.json: by_name={len(by_name)} by_id={len(by_id)}")

    # === 4. 摘要 ===
    remaining = sorted([f.name for f in OUT_DIR.iterdir()])
    print(f"[4/4] data/ 目录: {remaining}")

if __name__ == "__main__":
    main()
