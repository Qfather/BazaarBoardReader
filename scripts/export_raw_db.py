"""从 GameData.db 直接生成 data/ 下的数据文件"""
import sqlite3, json, os, re
from pathlib import Path

BASE = Path(__file__).resolve().parent.parent
DB = Path.home() / "AppData" / "LocalLow" / "Tempo Storm" / "The Bazaar" / "prod" / "cache" / "GameData.db"
TRANS_DB = DB.parent / "translations" / "zh-CN.bytes"
DATA_DIR = BASE / "data"

DATA_DIR.mkdir(parents=True, exist_ok=True)

conn = sqlite3.connect(str(DB))

# ═══════════════════════════════════════════════
# 1. 加载翻译
# ═══════════════════════════════════════════════
zh_map = {}
try:
    with sqlite3.connect(str(TRANS_DB)) as conn2:
        for key, text in conn2.execute("SELECT hash, text FROM translation").fetchall():
            if text:
                zh_map[str(key)] = str(text)
    print(f"[1/3] 翻译: {len(zh_map)} 条")
except Exception as e:
    print(f"[1/3] 翻译加载失败 ({e})")

# ═══════════════════════════════════════════════
# 2. 处理 cards 表 → 生成数据 + 原始导出
# ═══════════════════════════════════════════════
cards_generated = {}
cards_compact = {}
by_name = {}
by_id = {}
stats = {"total": 0, "Item": 0, "Skill": 0, "CombatEncounter": 0, "EventEncounter": 0, "EncounterStep": 0, "other": 0}

for table in ["cards", "monsters", "challenges", "collectibles", "game_modes", "level_ups", "seasons", "tooltips"]:
    try:
        rows = conn.execute(f"SELECT Id, Data FROM [{table}]").fetchall()
    except:
        continue

    row_count = 0
    for rid, raw in rows:
        try:
            d = json.loads(raw) if isinstance(raw, (str, bytes)) else raw
            if isinstance(raw, bytes):
                d = json.loads(raw.decode("utf-8"))
        except:
            continue

        row_count += 1

        # 只从 cards 表生成卡牌数据
        if table != "cards":
            continue

        iname = d.get("InternalName", "")
        if not iname:
            continue
        card_type = d.get("Type", "")
        if not card_type:
            continue  # 跳过 monster 空条目

        stats["total"] += 1
        stats[card_type] = stats.get(card_type, 0) + 1

        tid = str(d.get("TemplateId") or d.get("Id", rid))
        heroes = d.get("Heroes", [])
        tiers = list((d.get("Tiers") or {}).keys())
        starting_tier = d.get("StartingTier", tiers[0] if tiers else "")
        tags = d.get("Tags", [])
        hidden_tags = d.get("HiddenTags", [])
        size = d.get("Size", "")

        # 翻译
        loc = d.get("Localization") or {}
        title = loc.get("Title") or {}
        tkey = title.get("Key") or d.get("TranslationKey")
        zh = zh_map.get(str(tkey)) if tkey else None
        if zh:
            by_name[iname] = zh
            by_id[tid] = zh
            # 同时用去掉等级后缀的基础名索引（方便事件查找）
            base = re.sub(r'\s*\((?:Bronze|Silver|Gold|Diamond|Legendary|Level Up|Day\s+\d+[\s\-–]+\d+|Day\s+\d+\s*(?:to\s+\d+)?|Start Run|Encounter|Tutorial)\)\s*$', '', iname).strip()
            if base != iname and base not in by_name:
                by_name[base] = zh

        # Item优先：已有Item条目时不覆盖
        existing = cards_compact.get(iname, {})
        if existing.get("type") == "Item" and card_type != "Item":
            continue
        existing_gen = cards_generated.get(iname, {})
        if existing_gen.get("type") == "Item" and card_type != "Item":
            continue

        # cards_generated.json
        cards_generated[iname] = {
            "template_id": tid,
            "internal_name": iname,
            "name": d.get("Name", iname),
            "type": card_type,
            "size": size,
            "hero": d.get("Hero", ""),
            "heroes": heroes,
            "tiers": tiers,
            "tags": tags,
        }

        # cards.json
        cards_compact[iname] = {
            "internal_name": iname,
            "display_name": zh or iname,
            "type": card_type,
            "heroes": heroes,
            "tags": tags,
            "hidden_tags": hidden_tags,
            "size": size,
            "starting_tier": str(starting_tier),
            "tiers": tiers,
        }

    print(f"  [{table}]: {row_count} 行")

print(f"[2/3] 卡牌: {stats}")

# 2.5 补充事件 notes 翻译
import hashlib
try:
    events_path = DATA_DIR / "events.json"
    if events_path.exists():
        with open(events_path, "r", encoding="utf-8") as f:
            events_data = json.load(f)
        notes_added = 0
        for cat, items in events_data.items():
            for evt in items:
                notes = evt.get("notes", "")
                if notes and notes not in by_name:
                    h = hashlib.md5(notes.encode("utf-8")).hexdigest()
                    zh = zh_map.get(h)
                    if zh:
                        by_name[notes] = zh
                        notes_added += 1
        print(f"  事件notes翻译补充: {notes_added} 条")
except Exception as e:
    print(f"  事件notes翻译: {e}")

# ═══════════════════════════════════════════════
# 3. 写入 data/ 主文件
# ═══════════════════════════════════════════════
with open(DATA_DIR / "cards_generated.json", "w", encoding="utf-8") as f:
    json.dump(cards_generated, f, ensure_ascii=False, indent=2)
with open(DATA_DIR / "cards.json", "w", encoding="utf-8") as f:
    json.dump({"cards": cards_compact}, f, ensure_ascii=False, indent=2)
with open(DATA_DIR / "translations_zh_cn.json", "w", encoding="utf-8") as f:
    json.dump({"locale": "zh-CN", "by_name": dict(sorted(by_name.items())), "by_id": dict(sorted(by_id.items()))}, f, ensure_ascii=False, indent=2)

print(f"[3/3] cards.json={len(cards_compact)} cards_generated={len(cards_generated)} translations=by_name:{len(by_name)} by_id:{len(by_id)}")

# 验证
for name in ["Unibou", "Wolverine", "Wild Boar", "Worry Wart"]:
    c = cards_compact.get(name, {})
    s = "OK" if c.get("type") and c.get("heroes") else "MISS"
    print(f"  {s} {name}: {c.get('type','?')} {c.get('heroes',[])} {c.get('tiers',[])} {c.get('tags',[])}")

conn.close()
