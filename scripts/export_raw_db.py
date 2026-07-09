"""从 GameData.db 直接生成 data/ 下的数据文件"""
import sqlite3, json, os, re, hashlib
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
raw_cards = []
raw_cards_by_id = {}
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

        cid = str(d.get("Id", rid))
        raw_cards.append(d)
        raw_cards_by_id[cid] = d

        stats["total"] += 1
        stats[card_type] = stats.get(card_type, 0) + 1

        tid = str(d.get("TemplateId") or cid)
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


# 2.6 归纳技能事件/教练规则，供预览器使用
TIER_ORDER = ["Bronze", "Silver", "Gold", "Diamond", "Legendary"]

def _type_name(value):
    return str(value or "").split(",")[0].split(".")[-1]

def _title_text(card):
    loc = card.get("Localization") or {}
    title = loc.get("Title") or {}
    return title.get("Text") or card.get("InternalName", "")

def _translated_title(card):
    loc = card.get("Localization") or {}
    title = loc.get("Title") or {}
    key = title.get("Key") or card.get("TranslationKey")
    en = _title_text(card)
    return zh_map.get(str(key), by_name.get(en, en)) if key else by_name.get(en, en)

def _translated_desc(card):
    desc = (card.get("Localization") or {}).get("Description") or {}
    text = desc.get("Text", "")
    key = desc.get("Key")
    if key and str(key) in zh_map:
        return zh_map[str(key)]
    if text:
        return zh_map.get(hashlib.md5(text.encode("utf-8")).hexdigest(), by_name.get(text, text))
    return ""

def _fixed_value(value):
    if isinstance(value, dict) and "Value" in value:
        raw = value.get("Value")
        try:
            f = float(raw)
            return int(f) if f.is_integer() else f
        except Exception:
            return raw
    return value

def _uniq(seq):
    out = []
    seen = set()
    for item in seq or []:
        if item is None:
            continue
        key = str(item).lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(item)
    return out

def _card_ref(cid):
    card = raw_cards_by_id.get(str(cid), {})
    tiers = list((card.get("Tiers") or {}).keys())
    return {
        "id": str(cid),
        "name": card.get("InternalName", str(cid)),
        "display_name": _translated_title(card) if card else str(cid),
        "type": card.get("Type", ""),
        "heroes": card.get("Heroes", []) or [],
        "tiers": tiers,
        "starting_tier": card.get("StartingTier", tiers[0] if tiers else ""),
        "tags": card.get("Tags", []) or [],
        "hidden_tags": card.get("HiddenTags", []) or [],
    }

def _empty_rules():
    return {
        "card_types": [],
        "include_tiers": [],
        "exclude_tiers": [],
        "include_tags": [],
        "exclude_tags": [],
        "include_hidden_tags": [],
        "exclude_hidden_tags": [],
        "include_heroes": [],
        "exclude_heroes": [],
        "only_heroes": [],
        "fixed_ids": [],
        "upgrade_owned": False,
    }

def _extract_constraint_rules(node, rules):
    if isinstance(node, dict):
        typ = _type_name(node.get("$type"))
        is_not = bool(node.get("IsNot", False))
        if typ == "ConstraintCardType":
            rules["card_types"].extend(node.get("Types") or [])
        elif typ == "ConstraintTier":
            rules["exclude_tiers" if is_not else "include_tiers"].extend(node.get("Tiers") or [])
        elif typ == "ConstraintTag":
            rules["exclude_tags" if is_not else "include_tags"].extend(node.get("Tags") or [])
        elif typ == "ConstraintHiddenTag":
            rules["exclude_hidden_tags" if is_not else "include_hidden_tags"].extend(node.get("HiddenTags") or [])
        elif typ == "ConstraintHero":
            rules["exclude_heroes" if is_not else "include_heroes"].extend(node.get("Heroes") or [])
        elif typ == "ConstraintIsOnlyHero":
            rules["only_heroes"].extend(node.get("Heroes") or [])
        for value in node.values():
            _extract_constraint_rules(value, rules)
    elif isinstance(node, list):
        for item in node:
            _extract_constraint_rules(item, rules)

def _filter_rules(filters):
    rules = _empty_rules()
    for filt in filters or []:
        typ = _type_name(filt.get("$type")) if isinstance(filt, dict) else ""
        if typ == "TSpawnFilterIdList":
            rules["fixed_ids"].extend(str(i) for i in (filt.get("Ids") or filt.get("CardIds") or []))
        if typ == "TSpawnFilterUpgrade" and str(filt.get("CardType", "")).lower() == "skill":
            rules["upgrade_owned"] = True
            rules["card_types"].append("Skill")
        if isinstance(filt, dict) and str(filt.get("CardType", "")).lower() == "skill":
            rules["card_types"].append("Skill")
        if isinstance(filt, dict):
            _extract_constraint_rules(filt.get("Constraints"), rules)
    for key in list(rules.keys()):
        if key != "upgrade_owned":
            rules[key] = _uniq(rules[key])
    return rules

def _behavior_rules(behaviors):
    result = {
        "fixed_tiers": [],
        "ignore_hero": False,
        "ignore_tier_table": False,
        "downshift_tier": False,
        "allow_duplicates": False,
    }
    for behavior in behaviors or []:
        if not isinstance(behavior, dict):
            continue
        typ = _type_name(behavior.get("$type"))
        if typ == "TSpawnBehaviorTier":
            result["fixed_tiers"].extend(behavior.get("Tiers") or [])
        elif typ == "TSpawnBehaviorIgnoreHero":
            result["ignore_hero"] = bool(behavior.get("IgnoreHero", True))
        elif typ == "TSpawnBehaviorIgnoreTierTable":
            result["ignore_tier_table"] = bool(behavior.get("IgnoreTierTable", True))
        elif typ == "TSpawnBehaviorDownShiftTier":
            result["downshift_tier"] = True
        elif typ == "TSpawnBehaviorAllowDuplicates":
            result["allow_duplicates"] = bool(behavior.get("AllowDuplicates", True))
    result["fixed_tiers"] = _uniq(result["fixed_tiers"])
    return result

def _find_spawn_contexts(card):
    contexts = []
    sel_ctx = (card.get("SelectionContext") or {}).get("SpawnContext")
    if isinstance(sel_ctx, dict):
        contexts.append(("SelectionContext.SpawnContext", sel_ctx))
    abilities = card.get("Abilities") or []
    if isinstance(abilities, dict):
        ability_iter = abilities.items()
    else:
        ability_iter = enumerate(abilities)
    for idx, ability in ability_iter:
        action = ability.get("Action") if isinstance(ability, dict) else None
        spawn_ctx = action.get("SpawnContext") if isinstance(action, dict) else None
        if isinstance(spawn_ctx, dict):
            contexts.append((f"Abilities[{idx}].Action.SpawnContext", spawn_ctx))
    return contexts

def _context_has_skill_output(spawn_ctx, depth=0):
    if not isinstance(spawn_ctx, dict) or depth > 2:
        return False
    for group in spawn_ctx.get("Groups") or []:
        if _group_has_skill_output(group, depth):
            return True
    return False

def _group_has_skill_output(group, depth=0):
    rules = _filter_rules(group.get("Filters") or [])
    fixed_ids = rules.get("fixed_ids") or []
    if "Skill" in rules.get("card_types", []) or rules.get("upgrade_owned"):
        return True
    if any(raw_cards_by_id.get(fid, {}).get("Type") == "Skill" for fid in fixed_ids):
        return True
    if depth > 1:
        return False
    for fid in fixed_ids:
        ref = raw_cards_by_id.get(fid)
        if not ref:
            continue
        for _, nested in _find_spawn_contexts(ref):
            if _context_has_skill_output(nested, depth + 1):
                return True
    return False

def _summarize_nested_skill_source(card, depth=0):
    summaries = []
    if depth > 1:
        return summaries
    for _, spawn_ctx in _find_spawn_contexts(card):
        for group in spawn_ctx.get("Groups") or []:
            if not _group_has_skill_output(group, depth):
                continue
            rules = _filter_rules(group.get("Filters") or [])
            behavior = _behavior_rules((spawn_ctx.get("Behaviors") or []) + (group.get("Behaviors") or []))
            fixed_skills = [
                _card_ref(fid) for fid in rules.get("fixed_ids", [])
                if raw_cards_by_id.get(fid, {}).get("Type") == "Skill"
            ]
            nested_choices = []
            for fid in rules.get("fixed_ids", []):
                ref = raw_cards_by_id.get(fid)
                if ref and ref.get("Type") != "Skill":
                    nested = _summarize_nested_skill_source(ref, depth + 1)
                    if nested:
                        nested_choices.append({
                            "id": fid,
                            "name": ref.get("InternalName", fid),
                            "display_name": _translated_title(ref),
                            "type": ref.get("Type", ""),
                            "skill_summary": nested,
                        })
            summaries.append({
                "mode": "fixed" if fixed_skills else ("query" if "Skill" in rules.get("card_types", []) else "choices"),
                "rules": rules,
                "behavior": behavior,
                "fixed_skills": fixed_skills,
                "choices": nested_choices,
            })
    return summaries

def _classify_skill_event(card):
    name = card.get("InternalName", "")
    if "DEBUG" in name.upper():
        return "debug"
    if "(Level Up)" in name or name in {"Nonna"}:
        return "level_up"
    if "Start Skill" in name:
        return "start_skill"
    if name.startswith("Haddy - ") and "Skill" in name:
        return "haddy"
    if name == "Library" or "Library" in name:
        return "library"
    if "Tutorial" in name:
        return "tutorial"
    if "Training" in name:
        return "training"
    if "Restricted Skill List" in name:
        return "fixed_skill"
    return "skill"

def _quality_rule(rules, behavior):
    if behavior.get("fixed_tiers"):
        return "fixed"
    if rules.get("upgrade_owned"):
        return "owned_upgrade"
    if behavior.get("downshift_tier"):
        return "downshift"
    if rules.get("include_tiers"):
        return "fixed"
    return "normal_shop_by_day"

def _build_skill_events():
    events = []
    seen = set()
    for card in raw_cards:
        if card.get("$type") not in ("TCardEncounterEvent", "TCardEncounterStep"):
            continue
        groups_out = []
        for path, spawn_ctx in _find_spawn_contexts(card):
            if not _context_has_skill_output(spawn_ctx):
                continue
            ctx_limit = _fixed_value(spawn_ctx.get("Limit"))
            for idx, group in enumerate(spawn_ctx.get("Groups") or []):
                if not _group_has_skill_output(group):
                    continue
                rules = _filter_rules(group.get("Filters") or [])
                behavior = _behavior_rules((spawn_ctx.get("Behaviors") or []) + (group.get("Behaviors") or []))
                fixed_ids = rules.get("fixed_ids") or []
                fixed_skills = [_card_ref(fid) for fid in fixed_ids if raw_cards_by_id.get(fid, {}).get("Type") == "Skill"]
                choices = []
                for fid in fixed_ids:
                    ref = raw_cards_by_id.get(fid)
                    if not ref or ref.get("Type") == "Skill":
                        continue
                    nested = _summarize_nested_skill_source(ref)
                    choices.append({
                        "id": fid,
                        "name": ref.get("InternalName", fid),
                        "display_name": _translated_title(ref),
                        "type": ref.get("Type", ""),
                        "heroes": ref.get("Heroes", []) or [],
                        "starting_tier": ref.get("StartingTier", ""),
                        "skill_summary": nested,
                    })
                if not fixed_skills and not choices and "Skill" not in rules.get("card_types", []) and not rules.get("upgrade_owned"):
                    continue
                prereqs = group.get("Prerequisites") or []
                prereq_blob = json.dumps(prereqs, ensure_ascii=False)
                groups_out.append({
                    "context_path": path,
                    "group_index": idx,
                    "selection_method": group.get("SelectionMethod") or spawn_ctx.get("SelectionMethod", ""),
                    "limit": _fixed_value(group.get("Limit")) or ctx_limit,
                    "random_weight": group.get("RandomWeight", 0),
                    "rules": rules,
                    "behavior": behavior,
                    "fixed_skills": fixed_skills,
                    "choices": choices,
                    "prerequisite_count": len(prereqs),
                    "requires_owned_skill": "AbsolutePlayerSkills" in prereq_blob,
                    "quality_rule": _quality_rule(rules, behavior),
                })
        if not groups_out:
            continue
        name = card.get("InternalName", "")
        key = (str(card.get("Id", "")), name)
        if key in seen:
            continue
        seen.add(key)
        all_fixed = []
        all_choices = []
        any_upgrade = False
        for group in groups_out:
            all_fixed.extend(group.get("fixed_skills") or [])
            all_choices.extend(group.get("choices") or [])
            any_upgrade = any_upgrade or bool((group.get("rules") or {}).get("upgrade_owned"))
        if all_choices and not all_fixed and not any_upgrade:
            pool_mode = "choices"
        elif all_fixed and not any_upgrade:
            pool_mode = "fixed"
        elif any_upgrade:
            pool_mode = "upgrade_owned"
        else:
            pool_mode = "query"
        events.append({
            "id": str(card.get("Id", "")),
            "name": name,
            "display_name": _translated_title(card),
            "source_type": card.get("$type", ""),
            "card_type": card.get("Type", ""),
            "category": _classify_skill_event(card),
            "pool_mode": pool_mode,
            "heroes": card.get("Heroes", []) or [],
            "starting_tier": card.get("StartingTier", ""),
            "spawning_eligibility": card.get("SpawningEligibility", ""),
            "selection_method": (card.get("SelectionContext") or {}).get("SpawnContext", {}).get("SelectionMethod", ""),
            "limit": _fixed_value((card.get("SelectionContext") or {}).get("SpawnContext", {}).get("Limit")),
            "notes": _translated_desc(card),
            "groups": groups_out,
        })
    order = {
        "skill": 0, "library": 1, "haddy": 2, "start_skill": 3, "fixed_skill": 4,
        "tutorial": 5, "level_up": 6, "training": 7, "debug": 8,
    }
    return sorted(events, key=lambda e: (order.get(e["category"], 99), e["display_name"], e["name"]))

skill_events = _build_skill_events()
print(f"  技能事件: {len(skill_events)} 条")
# ═══════════════════════════════════════════════
# 3. 写入 data/ 主文件
# ═══════════════════════════════════════════════
with open(DATA_DIR / "cards_generated.json", "w", encoding="utf-8") as f:
    json.dump(cards_generated, f, ensure_ascii=False, indent=2)
with open(DATA_DIR / "cards.json", "w", encoding="utf-8") as f:
    json.dump({"cards": cards_compact}, f, ensure_ascii=False, indent=2)
with open(DATA_DIR / "translations_zh_cn.json", "w", encoding="utf-8") as f:
    json.dump({"locale": "zh-CN", "by_name": dict(sorted(by_name.items())), "by_id": dict(sorted(by_id.items()))}, f, ensure_ascii=False, indent=2)
with open(DATA_DIR / "skill_events.json", "w", encoding="utf-8") as f:
    json.dump({"events": skill_events}, f, ensure_ascii=False, indent=2)

print(f"[3/3] cards.json={len(cards_compact)} cards_generated={len(cards_generated)} skill_events={len(skill_events)} translations=by_name:{len(by_name)} by_id:{len(by_id)}")

# 验证
for name in ["Unibou", "Wolverine", "Wild Boar", "Worry Wart"]:
    c = cards_compact.get(name, {})
    s = "OK" if c.get("type") and c.get("heroes") else "MISS"
    print(f"  {s} {name}: {c.get('type','?')} {c.get('heroes',[])} {c.get('tiers',[])} {c.get('tags',[])}")

conn.close()
