"""从 GameData.db 导出商店推荐数据"""
import sqlite3, json, os, sys

LOCALLOW = os.path.join(os.environ['LOCALAPPDATA'], '..', 'LocalLow',
    'Tempo Storm', 'The Bazaar', 'prod', 'cache')
GDATA = os.path.join(LOCALLOW, 'GameData.db')
DIR = os.path.dirname(os.path.abspath(__file__))
DATA_DIR = os.path.join(DIR, 'data')

# 加载翻译
TRANS_PATH = os.path.join(DIR, 'translations_zh_cn.json')
if os.path.exists(TRANS_PATH):
    with open(TRANS_PATH, 'r', encoding='utf-8') as f:
        trans = json.load(f).get('by_name', {})
else:
    trans = {}

def t(en):
    return trans.get(en, en)

# ─── 1. 加载所有卡牌 ───
conn = sqlite3.connect(GDATA)
c = conn.cursor()
c.execute('SELECT Id, Data FROM cards')

id_map = {}       # guid → parsed JSON
type_map = {}     # guid → $type
name_map = {}     # InternalName → parsed JSON (仅 TCardItem/TCardSkill)

for (cid, blob) in c:
    j = json.loads(blob)
    id_map[cid] = j
    tp = j.get('$type', '')
    type_map[cid] = tp
    if tp in ('TCardItem', 'TCardSkill'):
        iname = j.get('InternalName', '')
        if iname:
            name_map[iname] = j

print(f'加载: {len(id_map)} 卡牌 ({len(name_map)} 物品/技能)')
conn.close()

# ─── 2. 递归解析 ID 链，提取最终物品/技能名 ───
def resolve_items(card_id, visited=None, depth=0):
    """递归追踪一个卡牌 ID，收集其引用的所有 TCardItem/TCardSkill InternalName"""
    if visited is None:
        visited = set()
    if depth > 5 or card_id in visited:
        return set()
    visited.add(card_id)

    j = id_map.get(card_id)
    if j is None:
        return set()

    tp = j.get('$type', '')

    # 直接是物品/技能
    if tp in ('TCardItem', 'TCardSkill'):
        name = j.get('InternalName', '')
        return {name} if name else set()

    # EncounterEvent 或 EncounterStep：追踪其 SpawnContext 的 Groups
    items = set()

    # 尝试从 SelectionContext 获取
    sc = j.get('SelectionContext')
    if sc:
        spawn = sc.get('SpawnContext')
        if spawn:
            items |= resolve_spawn_context(spawn, visited, depth+1)

    # 尝试从 Abilities 获取
    abilities = j.get('Abilities', {})
    for ak, av in abilities.items():
        if isinstance(av, dict):
            action = av.get('Action', {})
            spawn = action.get('SpawnContext')
            if spawn:
                items |= resolve_spawn_context(spawn, visited, depth+1)
            # 有些 Action 包含 RewardCardPools 或直接有 card lists
            for f in ('RewardCardPools', 'Cards', 'Items'):
                arr = action.get(f, [])
                for entry in arr:
                    if isinstance(entry, dict):
                        eid = entry.get('TemplateId') or entry.get('Id')
                        if eid:
                            items |= resolve_items(eid, visited, depth+1)

    return items

def resolve_spawn_context(spawn, visited, depth):
    """解析 TSpawnContextQuery，提取 Groups 中的 ID"""
    items = set()
    groups = spawn.get('Groups', [])
    for g in groups:
        if not isinstance(g, dict):
            continue
        filters = g.get('Filters', [])
        for f in filters:
            if not isinstance(f, dict):
                continue
            ids = f.get('Ids', [])
            for iid in ids:
                if isinstance(iid, str):
                    items |= resolve_items(iid, visited, depth)
    return items

# ─── 3. 导出 cards.json ───
cards_out = {}
for iname, j in sorted(name_map.items()):
    tp = j.get('$type', '')
    entry = {
        'internal_name': iname,
        'display_name': t(iname),
        'type': 'Skill' if tp == 'TCardSkill' else 'Item',
        'heroes': j.get('Heroes', []),
        'tags': j.get('Tags', []),
        'hidden_tags': j.get('HiddenTags', []),
        'size': j.get('Size', ''),
        'starting_tier': j.get('StartingTier', ''),
    }
    # 收集所有品质
    tiers = j.get('Tiers', {})
    entry['tiers'] = sorted(tiers.keys())
    cards_out[iname] = entry

with open(os.path.join(DATA_DIR, 'cards.json'), 'w', encoding='utf-8') as f:
    json.dump({'cards': cards_out}, f, ensure_ascii=False, indent=2)
print(f'cards.json: {len(cards_out)} 条')

# ─── 4. 导出 shops.json ───
# 从教程物品推断商店特征规则（size + tags），导出规则用于动态构建物品池
from collections import Counter

def extract_rules(item_pool_names):
    """从物品名列表提取公共 size（商人店的核心规则是size，不是tags）"""
    sizes = Counter()
    for iname in item_pool_names:
        card = name_map.get(iname, {})
        sz = card.get('Size', '')
        if sz: sizes[sz] += 1
    rules = {}
    total = len(item_pool_names)
    if sizes and sizes.most_common(1)[0][1] > total * 0.5:
        rules['size'] = sizes.most_common(1)[0][0]
    # tags 仅用于无法判断size时补充参考
    return rules

merged = {}  # name_en → rule

for cid, j in id_map.items():
    tp = j.get('$type', '')
    if tp != 'TCardEncounterEvent':
        continue

    iname = j.get('InternalName', '')
    if 'DEBUG' in iname:
        continue

    title = j.get('Localization', {}).get('Title', {}).get('Text', iname)
    if not title:
        continue

    heroes = j.get('Heroes', [])
    item_pool = resolve_items(cid)
    if not item_pool:
        continue

    # 判断是否技能商店
    sample = next(iter(item_pool))
    sj = name_map.get(sample, {})
    is_skill = sj.get('$type', '') == 'TCardSkill'

    # 提取规则（至少要有size或tags才算有效商店）
    rules = extract_rules(item_pool)
    if not rules.get('size') and not rules.get('tags'):
        continue  # 跳过无规则事件（如Free Item等混杂事件）

    key = title.lower()
    if key in merged:
        # 合并规则
        existing = merged[key]
        if 'size' not in existing and 'size' in rules:
            existing['size'] = rules['size']
        if 'tags' in rules:
            for tag in rules['tags']:
                if tag not in existing.get('tags', []):
                    existing.setdefault('tags', []).append(tag)
    else:
        merged[key] = {
            'name': iname,
            'name_en': title,
            'display_name': t(title),
            'heroes': heroes,
            'is_skill': is_skill,
            **rules
        }

shops = []
skill_shops = []
for key, m in merged.items():
    entry = {k: m[k] for k in ['name','name_en','display_name','heroes'] if k in m}
    entry['size'] = m.get('size', '')
    entry['tags'] = m.get('tags', [])
    if m['is_skill']:
        skill_shops.append(entry)
    else:
        shops.append(entry)

with open(os.path.join(DATA_DIR, 'shops.json'), 'w', encoding='utf-8') as f:
    json.dump({'shops': shops, 'skill_shops': skill_shops}, f, ensure_ascii=False, indent=2)
print(f'shops.json: {len(shops)} 物品店 + {len(skill_shops)} 技能店')
for s in shops[:10]:
    print('  {}: size={} tags={}'.format(s['name_en'], s.get('size','?'), s.get('tags',[])))
print('完成!')
