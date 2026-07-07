"""按用户定义重新分类所有事件（不含 shops, skill_shops）"""
import json, os

os.chdir(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
with open('data/events.json','r',encoding='utf-8') as f:
    ev = json.load(f)
with open('data/translations_zh_cn.json','r',encoding='utf-8') as f:
    tr = json.load(f)['by_name']
def t(n):
    return tr.get(n, n)

# 用户定义的分类规则（按具体名称/特征匹配）
RULES = {
    "获取战利品": [
        "Extract", "Extract ", "Salvage", "Scrap", "Craftsmanship", "Salesmanship",
        "Get Bronze Loot", "Get Silver Loot", "Get Gold Loot", "Get Diamond Loot",
        "Get Gold or Diamond Loot",
        "Reagent Harvesting", "Reagent Shelf",
    ],
    "选择事件": [
        # 有 followup_options 的
    ],
    "获取对应物品": [
        # 有 reward_tags 或 exact_names 且不是上面分类的
        "Abandoned Property", "Alchemy Lab", "Ammo Cache", "Armory",
        "Arms Locker", "Battlefield", "BeFriend", "Block Party",
        "Botanical Gardens", "Cabin Fishing", "Candy Bowl", "Cinder of Chaos",
        "Covetous Thief", "Deep Sea Fishing", "Double Extract",
        "Freezer", "Furnace", "Go Fishing", "Golden or Diamond Loot Item",
        "Guard Locker", "Gunpowder Cache", "Hospital", "House Party",
        "Inheritance", "Load Up", "Lost and Found", "Mag Storage",
        "Medicine Cabinet", "Medium Rare", "Obstacle Course",
        "Pearl's Dig Site", "Potion Rack", "Power Up", "Procure Medkit",
        "Pyre", "Racetrack", "Rageforge", "Rare Loot Item",
        "Reagent Harvesting", "Reagent Shelf", "Scrap Salvage",
        "Security Center", "Sharpening Kit",
        "Supply Drop", "Take Flight", "Tech Salvage",
        "Thanks!", "Tool Up", "Toxic Spoils", "Treasure Chest",
        "Trophy Hunter", "Utility Box", "Workshop",
    ],
    "临时增益": [
        "Jules' Cafe", "Jules's Cafe", "Regenerative Tincture",
        "Borrow", "Invest in Yourself", "Finn's Big Bite", "Likit",
        "Snack Time", "Run", "Run a Race", "Cache of Riches",
        "Track", "Lost Wallet",
        "Economic Seminar",
    ],
    "特殊商店": [
        "Likit", "Pearl's Dig Site", "The Travel Agent",
        "Candy Serpent Likit", "糖果蛇",
    ],
    "特殊专属": [
    ],
    "技能事件": [
    ],
    "强化物品": [
        "Advanced Training", "Aldric", "Artisan Dunes",
        "B1&B2", "Botul", "D'flek", "Flambe", "Form",
        "Forja", "Mad Maddie", "Mandala", "Sterling",
        "Wink", "Upgrade an item",
    ],
    "其他": [
    ],
}

# 实际分类：遍历每个事件
exclude = {"shops", "skill_shops"}
results = {k: [] for k in RULES}

for cat, items in ev.items():
    if cat in exclude:
        continue
    for e in items:
        name = e.get('name','')
        zh = t(name)
        heroes = e.get('event_heroes',[])
        tags = e.get('reward_tags',[]) or []
        followups = e.get('followup_options',[])
        effect = e.get('effect','')
        exact = e.get('exact_names',[]) or []
        etype = e.get('event_type','')
        notes = e.get('notes','')
        res = e.get('resource_rewards',{}) or {}

        classified = False

        # 选择事件：有 followup_options
        if followups:
            results["选择事件"].append(e)
            classified = True

        # 强化物品
        if effect in ("upgrade_items","improve_items","transform_items",
                       "enchant_items","enhance_offensive_items"):
            if not classified:
                results["强化物品"].append(e)
                classified = True
            else:
                # 有分支+效果 → 放选择事件
                pass

        if classified:
            continue

        # 按名称特征匹配
        name_lower = name.lower()
        for rule_cat, keywords in RULES.items():
            if rule_cat in ("选择事件", "强化物品"):
                continue  # 已处理
            for kw in keywords:
                if kw.lower() in name_lower:
                    results[rule_cat].append(e)
                    classified = True
                    break
            if classified:
                break

        if classified:
            continue

        # 获取对应物品：有标签或精确物品名
        if tags or exact:
            results["获取对应物品"].append(e)
            continue

        # 临时增益：resource_event 且无物品
        if etype == "resource_event":
            results["临时增益"].append(e)
            continue

        # 特殊商店
        if etype in ("utility_event","unknown_event") and "shop" in notes.lower():
            results["特殊商店"].append(e)
            continue

        # 特殊专属：非Common
        if heroes and "Common" not in heroes:
            results["特殊专属"].append(e)
            continue

        # 其余
        results["其他"].append(e)

# 输出
total = sum(len(v) for v in results.values())
print(f"总事件数: {total} (不含shops/skill_shops)\n")
for cat, items in results.items():
    if not items:
        continue
    print(f"## {cat} ({len(items)}个)")
    for e in items:
        zh = t(e['name'])
        heroes = e.get('event_heroes',[])
        hero_str = ','.join(heroes[:3])
        if len(heroes) > 3: hero_str += f'+{len(heroes)-3}'
        tags = e.get('reward_tags',[]) or []
        tag_str = ','.join(tags[:4]) if tags else ''
        followups = len(e.get('followup_options',[]))
        effect = e.get('effect','')
        extra = ''
        if followups: extra += f' [{followups}分支]'
        if effect: extra += f' [{effect}]'
        exact = e.get('exact_names',[])
        if exact: extra += f' [精确{len(exact)}]'
        print(f"  {zh} | {hero_str} | {tag_str}{extra}")
    print()
