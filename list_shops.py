"""列出所有商店 — 带完整信息表格"""
import sqlite3, os, json
from collections import Counter

cache = os.path.join(os.environ['LOCALAPPDATA'], '..', 'LocalLow', 'Tempo Storm', 'The Bazaar', 'prod', 'cache', 'GameData.db')
# 翻译
trans_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'translations_zh_cn.json')
trans = {}
if os.path.exists(trans_path):
    with open(trans_path, 'r', encoding='utf-8') as f:
        trans = json.load(f).get('by_name', {})
def t(en):
    return trans.get(en, en)

conn = sqlite3.connect(cache)
c = conn.cursor()
c.execute('SELECT Id, Data FROM cards')
id_map = {}
name_map = {}
for (cid, blob) in c:
    j = json.loads(blob)
    id_map[cid] = j
    if j.get('$type') in ('TCardItem','TCardSkill'):
        name_map[j.get('InternalName','')] = j

def resolve(card_id, visited=None, depth=0):
    if visited is None: visited = set()
    if depth > 5 or card_id in visited: return set()
    visited.add(card_id)
    j = id_map.get(card_id)
    if not j: return set()
    tp = j.get('$type','')
    if tp in ('TCardItem','TCardSkill'):
        name = j.get('InternalName','')
        return {name} if name else set()
    items = set()
    sections = []
    sel = j.get('SelectionContext')
    if sel:
        sc = sel.get('SpawnContext')
        if sc: sections.append(sc)
    for ak, av in j.get('Abilities',{}).items():
        if isinstance(av, dict):
            act = av.get('Action',{})
            if isinstance(act, dict):
                sc = act.get('SpawnContext')
                if sc: sections.append(sc)
    for section in sections:
        for g in section.get('Groups',[]):
            for f in g.get('Filters',[]):
                for iid in f.get('Ids',[]):
                    if isinstance(iid, str):
                        items |= resolve(iid, visited, depth+1)
    return items

results = []
for cid, j in id_map.items():
    if j.get('$type') != 'TCardEncounterEvent': continue
    name = j.get('InternalName','')
    if 'DEBUG' in name: continue

    title = j.get('Localization',{}).get('Title',{}).get('Text', name)
    # 去掉 " - Tutorial..." 后缀
    import re
    title = re.sub(r' - Tutorial.*$', '', title)
    desc = j.get('Localization',{}).get('Description',{})
    desc_text = t(desc.get('Text','')) if desc else ''
    flav = j.get('Localization',{}).get('FlavorText',{})
    flav_text = t(flav.get('Text','')) if flav else ''
    heroes = j.get('Heroes',[])
    htags = j.get('HiddenTags',[])
    tags = j.get('Tags',[])
    tier = j.get('StartingTier','')
    etype = j.get('Type','')
    size = j.get('Size','')
    is_tut = 'Tutorial' in name
    items = sorted(resolve(cid))
    is_merchant = 'Merchant' in tags

    # 只保留有物品的或有Merchant标签的
    if not items and not is_merchant:
        continue

    # 英雄分类
    if 'Common' in heroes or len(heroes) == 0:
        hero_cat = '通用'
    else:
        hero_cat = '/'.join(heroes)

    results.append({
        'title': title, 'desc': desc_text, 'flavor': flav_text,
        'hero_cat': hero_cat, 'heroes': heroes,
        'tags': tags, 'hidden_tags': htags,
        'tier': tier, 'size': size, 'type': etype,
        'tutorial': is_tut, 'items': items
    })

# 按title合并
merged = {}
for r in results:
    key = r['title'].lower()
    if key in merged:
        m = merged[key]
        m['items'] = sorted(set(m['items'] + r['items']))
        m['tutorial'] = m['tutorial'] and r['tutorial']
    else:
        merged[key] = dict(r)

sorted_shops = sorted(merged.values(), key=lambda x: (x['hero_cat'] != '通用', -len(x['items'])))

print(f'共 {len(sorted_shops)} 个商店\n')
i = 1
for s in sorted_shops:
    name = s['title']
    items = s['items']
    is_tut = ' [教程]' if s['tutorial'] else ''
    print(f'{i}. 【{name}】{is_tut}')
    print(f'   分类: {s["hero_cat"]} | 等级: {s["tier"]} | 尺寸: {s["size"]} | 类型: {s["type"]}')
    if s['tags']: print(f'   标签: {", ".join(s["tags"])}')
    if s['hidden_tags']: print(f'   隐藏标签: {", ".join(s["hidden_tags"])}')
    if s['desc']: print(f'   介绍: {s["desc"]}')
    if s['flavor']: print(f'   备注: {s["flavor"]}')
    if items:
        item_zh = [t(it) for it in items[:8]]
        more = f' ...等{len(items)}件' if len(items) > 8 else ''
        print(f'   物品({len(items)}): {", ".join(item_zh)}{more}')
    else:
        print(f'   物品: (动态生成)')
    print()
    i += 1

conn.close()
