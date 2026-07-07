"""导出商人规则 — 两分类(通用/专属)"""
import sqlite3, os, json

DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
trans_path = os.path.join(DIR, 'data', 'translations_zh_cn.json')
trans = {}
if os.path.exists(trans_path):
    with open(trans_path, 'r', encoding='utf-8') as f:
        trans = json.load(f).get('by_name', {})
def t(en): return trans.get(en, en)

conn = sqlite3.connect(os.path.join(os.environ['LOCALAPPDATA'], '..', 'LocalLow', 'Tempo Storm', 'The Bazaar', 'prod', 'cache', 'GameData.db'))
c = conn.cursor()
c.execute('SELECT Data FROM cards')
id_map = {}
for (blob,) in c:
    j = json.loads(blob)
    id_map[j.get('Id','')] = j

# 完整手动规则
RULES = {
    # ── 通用 ──
    'aila':       {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Weapon']},
    'ande':       {'cat':'通用', 'heroes':['Common'], 'size':'Small','tags':[]},
    'barkun':     {'cat':'通用', 'heroes':['Common'], 'size':'Medium,Large','tags':[]},
    'chronos':    {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Haste']},
    'cobweb':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Slow']},
    'curio':      {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Neutral']},
    'freiya':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Freeze']},
    'gaseo':      {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Apparel'], 'cross_hero':True},
    'goldie':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    'hef':        {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Burn']},
    'jay jay':    {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    'kina':       {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[], 'exclude_tags':['Weapon']},
    'knightshade':{'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Poison']},
    'luxe':       {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    'midsworth':  {'cat':'通用', 'heroes':['Common'], 'size':'Small,Large','tags':[]},
    'mittel':     {'cat':'通用', 'heroes':['Common'], 'size':'Medium','tags':[]},
    'orion':      {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Tools']},
    'pol':        {'cat':'通用', 'heroes':['Common'], 'size':'Large','tags':[]},
    'quixel':     {'cat':'通用', 'heroes':['Common'], 'size':'Small,Medium','tags':[]},
    'serafina':   {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    'silvia':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    'valpak':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':[]},
    # 多英雄→通用
    'aimbot':     {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Crit'], 'cross_hero':True},
    'herma':      {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Heal','Regen']},
    'kev\'s armory':{'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Shield','Health']},
    'pinfeather': {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Flying'], 'cross_hero':True},
    'private pitchfork':{'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Neutral']},
    'tatiana':    {'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Toys'], 'cross_hero':True},
    'the antiquarian':{'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Relic'], 'cross_hero':True},
    'tok\'s clocks':{'cat':'通用', 'heroes':['Common'], 'size':'','tags':['Haste','Slow','Cooldown']},
    # ── 专属 ──
    'aero':       {'cat':'专属', 'heroes':['Stelle'], 'size':'','tags':['Vehicle','Drone']},
    'colt':       {'cat':'专属', 'heroes':['Vanessa'], 'size':'','tags':['Ammo']},
    'eli':        {'cat':'专属', 'heroes':['Mak'], 'size':'','tags':['Potion']},
    'flex':       {'cat':'专属', 'heroes':['Pygmalien','Karnok'], 'size':'','tags':['MaxHealth']},
    'gastro':     {'cat':'专属', 'heroes':['Jules'], 'size':'','tags':['Food']},
    'mr. morland':{'cat':'专属', 'heroes':['Pygmalien'], 'size':'','tags':['Property']},
    'nautica':    {'cat':'专属', 'heroes':['Vanessa'], 'size':'','tags':['Aquatic']},
    'prospero':   {'cat':'专属', 'heroes':['Pygmalien'], 'size':'','tags':['Economic']},
    'shelter shelby':{'cat':'专属', 'heroes':['Karnok','Vanessa'], 'size':'','tags':['Friend']},
    'the tester': {'cat':'专属', 'heroes':['Dooley','Stelle'], 'size':'','tags':['Tech']},
    'tinker':     {'cat':'专属', 'heroes':['Dooley'], 'size':'','tags':['Friend']},
    # stickybeans 已删除
}

merchants = []
for cid, j in id_map.items():
    if j.get('$type') != 'TCardEncounterEvent': continue
    name = j.get('InternalName','')
    if 'DEBUG' in name: continue
    tags = j.get('Tags',[])
    if 'Merchant' not in tags: continue

    title = j.get('Localization',{}).get('Title',{}).get('Text', name)
    desc = j.get('Localization',{}).get('Description',{})
    desc_text = desc.get('Text','') if desc else ''
    tier = j.get('StartingTier','')

    key = title.lower()
    rule = RULES.get(key)
    if not rule: continue  # stickybeans等已删除

    merchants.append({
        'name': title, 'desc': desc_text, 'desc_zh': t(desc_text),
        'category': rule['cat'],
        'heroes': rule['heroes'],
        'tier': tier,
        'tags': rule.get('tags', []),
        'exclude_tags': rule.get('exclude_tags', []),
        'size': rule.get('size', ''),
        'cross_hero': rule.get('cross_hero', False),
    })

# 去重
merged = {}
for m in merchants:
    key = m['name'].lower()
    if key not in merged: merged[key] = m

cats = {'通用': [], '专属': []}
for m in sorted(merged.values(), key=lambda x: (x['category']!='通用', x['name'])):
    cats[m['category']].append(m)

# 标签翻译
TAG_ZH = {'Weapon':'武器','Burn':'燃烧','Poison':'剧毒','Freeze':'冻结','Haste':'急速','Slow':'减速',
    'Aquatic':'水系','Vehicle':'载具','Drone':'无人机','Friend':'伙伴','Tools':'工具','Property':'房产',
    'Food':'食物','Potion':'药水','Toys':'玩具','Apparel':'服装','Relic':'遗物','Tech':'科技',
    'Shield':'护盾','Health':'生命','Heal':'治疗','Regen':'回复','Ammo':'弹药','Crit':'暴击',
    'Flying':'飞行','Economic':'经济','Cooldown':'冷却','Neutral':'中立','MaxHealth':'最大生命值',
    'Small':'小型','Medium':'中型','Large':'大型','Enchanted':'附魔'}

for cat in ['通用', '专属']:
    items = cats[cat]
    print(f'\n=== {cat} ({len(items)}个) ===')
    print(f'{"名字":<20s} {"英雄范围":<15s} {"售卖标签":<30s} 介绍')
    print('-' * 95)
    for m in items:
        name_zh = t(m['name'])
        if m['cross_hero']:
            hero_str = 'ALL'
        elif len(m['heroes']) == 1 and m['heroes'][0] == 'Common':
            hero_str = '当前英雄'
        else:
            hero_str = ','.join(m['heroes'])
        parts = []
        if m['size']:
            sizes_zh = [TAG_ZH.get(s, s) for s in m['size'].split(',')]
            parts.append(','.join(sizes_zh))
        if m['tags']:
            tags_zh = [TAG_ZH.get(t, t) for t in m['tags']]
            parts.append(','.join(tags_zh))
        if m['exclude_tags']:
            ex_zh = [TAG_ZH.get(t, t) for t in m['exclude_tags']]
            parts.append('排除:' + ','.join(ex_zh))
        tag_str = '+'.join(parts) if parts else 'ALL'
        desc = m['desc_zh'][:35] if m['desc_zh'] else m['desc'][:35]
        print(f'{name_zh[:19]:<20s} {hero_str:<15s} {tag_str:<30s} {desc}')

out = []
for m in merged.values():
    out.append({k: m[k] for k in ['name','category','heroes','tier','tags','exclude_tags','size','cross_hero','desc','desc_zh']})
with open(os.path.join(DIR, 'data', 'merchants.json'), 'w', encoding='utf-8') as f:
    json.dump(out, f, ensure_ascii=False, indent=2)
print(f'\n\n保存: data/merchants.json ({len(out)}条)')
conn.close()
