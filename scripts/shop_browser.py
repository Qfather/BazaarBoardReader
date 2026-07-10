#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""The Bazaar 预览器 — 商店/事件/技能/教练/怪物"""
import json, os, re, tkinter as tk
from pypinyin import lazy_pinyin
from tkinter import ttk

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(BASE_DIR, "data")

HEROES = ["Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle", "Jules", "Karnok"]
HERO_ABBREV = {"Vanessa":"VAN","Pygmalien":"PYG","Dooley":"DOO","Mak":"MAK","Stelle":"STE","Jules":"JUL","Karnok":"KAR"}
HERO_COLORS = {"Vanessa":"#e04040","Pygmalien":"#4080e0","Dooley":"#e0a000","Mak":"#30a030","Stelle":"#e0e040","Jules":"#a040d0","Karnok":"#00b0b0"}

# ─── 数据加载 ───
def load_json(filename):
    path = os.path.join(DATA_DIR, filename)
    if not os.path.exists(path): path = os.path.join(BASE_DIR, filename)
    with open(path,"r",encoding="utf-8") as f: return json.load(f)

def load_data():
    merchants_raw = load_json("merchants.json")
    cards_raw = load_json("cards.json")
    trans_raw = load_json("translations_zh_cn.json")
    translations = trans_raw.get("by_name", {})
    cards = {}
    for key, card in cards_raw.get("cards", {}).items():
        cards[card.get("internal_name", key).lower()] = card
    BLACKLIST = {'assembly line','augment reagents','armored core','companion core','critical core',
        'focused core','ignition core','launcher core','the core','weaponized core','oblivion core',
        'unused card',"magician's top hat",'blue gumball','green gumball','red gumball','yellow gumball'}
    filtered = {}
    def has_trans(name):
        zh = translations.get(name,'')
        return zh and zh != name
    for k, v in cards.items():
        if v.get('type') != 'Item': continue
        if 'DEBUG' in k.upper() or 'TEMPLATE' in k.upper(): continue
        if 'COMMUNITY TEAM' in k.upper(): continue
        if 'Ship [OnBuy]' in k: continue
        name = v.get('internal_name','')
        if name.lower() in BLACKLIST: continue
        if "'s Package" in name: continue
        if re.search(r' [A-LR]$', name): continue
        tags = v.get('tags',[]) or []
        if 'Loot' in tags and 'Crystal' in name: continue
        hidden = v.get('hidden_tags',[]) or []
        if 'Package' in hidden: continue
        heroes = v.get('heroes',[]) or []
        if heroes == ['Common'] and not has_trans(name): continue
        filtered[k] = v
    return merchants_raw, filtered, translations

def load_tier_data():
    analysis = load_json("game_data_analysis.json")
    raw = analysis.get("item_skill_tier_by_day", {})
    return {int(k): v for k, v in raw.items()}

def get_available_tiers(day, tier_probs):
    keys = sorted(tier_probs.keys())
    if not keys: return set()
    best_key = keys[0]
    for k in keys:
        if k <= day: best_key = k
        else: break
    prob = tier_probs.get(best_key, {})
    return {t for t, p in prob.items() if p > 0}

def tr(text, translations):
    result = translations.get(text)
    if result is not None and result != text: return result
    result = translations.get(f"{text} (Merchant)")
    if result is not None: return result
    for s in [" (Item Reward)"," (Resource Event)"," (Item Event)"," (Enchant Event)"," (Combat Event)"," (Utility Event)"]:
        result = translations.get(f"{text}{s}")
        if result is not None: return result
    return text

def card_matches_tags(card_tags, card_desc, merchant_tags):
    if not merchant_tags: return True
    tags_lower = [t.lower() for t in card_tags] if card_tags else []
    for mt in merchant_tags:
        mt_lower = mt.lower()
        if mt_lower in tags_lower: return True
        if (mt_lower + "reference") in tags_lower: return True
        if mt_lower.endswith('s'):
            singular = mt_lower[:-1]
            if singular in tags_lower or (singular+"reference") in tags_lower: return True
        if mt_lower == "maxhealth":
            for t in tags_lower:
                if t.startswith("health"): return True
            if card_desc and "max health" in card_desc.lower(): return True
    return False

def has_exclude_tag(card_tags, exclude_tags):
    if not exclude_tags or not card_tags: return False
    tags_lower = set(t.lower() for t in card_tags)
    return any(et.lower() in tags_lower for et in exclude_tags)

def build_shop_pool(hero, merchant, cards, available_tiers=None):
    pool = []
    merchant_heroes = [h.lower() for h in merchant.get("heroes", [])]
    cross_hero = merchant.get("cross_hero", False)
    merchant_tags = merchant.get("tags", [])
    exclude_tags = merchant.get("exclude_tags", [])
    size_filter = [s.strip().lower() for s in merchant.get("size","").split(",") if s.strip()]
    is_neutral_shop = any(t.lower()=="neutral" for t in merchant_tags) if merchant_tags else False
    if merchant.get("name","") == "The Tester": cross_hero = True
    antiquarian_heroes = {"vanessa","pygmalien","dooley","mak","karnok"}
    for name_lower, card in cards.items():
        card_heroes = [h.lower() for h in card.get("heroes",[])]
        hero_lower = hero.lower()
        card_is_neutral = (len(card_heroes)==1 and card_heroes[0]=="common")
        tiers = card.get("tiers",[]) or []
        if "Legendary" in tiers: continue
        if merchant.get("name","") == "The Antiquarian":
            if hero_lower not in antiquarian_heroes: continue
        if card_is_neutral and not is_neutral_shop and not cross_hero: continue
        if is_neutral_shop:
            if not card_is_neutral: continue
            if merchant.get("tier","") == "Silver":
                if "Bronze" not in (card.get("tiers",[]) or []): continue
        if not card_is_neutral and not is_neutral_shop:
            if cross_hero: pass
            elif len(merchant_heroes)==1 and merchant_heroes[0]=="common":
                if hero_lower not in card_heroes: continue
            else:
                if hero_lower not in card_heroes: continue
                if hero_lower not in merchant_heroes: continue
        TIER_SHOPS = {"silvia":"silver","goldie":"gold","luxe":"diamond"}
        if not is_neutral_shop:
            sn = merchant.get("name","").lower()
            if sn in TIER_SHOPS:
                if TIER_SHOPS[sn] not in [t.lower() for t in (card.get("tiers",[]) or [])]: continue
        card_size = card.get("size","").lower()
        if size_filter and card_size not in size_filter: continue
        if not is_neutral_shop:
            all_tags = list(card.get("tags",[]) or [])
            hidden = card.get("hidden_tags",[]) or []
            all_tags.extend(hidden)
            if merchant_tags and not card_matches_tags(all_tags, card.get("description",""), merchant_tags): continue
        if not is_neutral_shop and exclude_tags:
            all_tags2 = list(card.get("tags",[]) or [])
            hidden2 = card.get("hidden_tags",[]) or []
            all_tags2.extend(hidden2)
            if has_exclude_tag(all_tags2, exclude_tags): continue
        if available_tiers is not None:
            ct = set(card.get("tiers",[]) or [])
            ct.discard("Legendary")
            if not (ct & available_tiers): continue
            card["_available_tiers_today"] = sorted(ct & available_tiers)
        pool.append(card)
    return pool

def load_builds():
    path = os.path.join(DATA_DIR, "community_builds.json")
    if not os.path.exists(path): return []
    with open(path, "r", encoding="utf-8") as f:
        raw = json.load(f)
    return list(raw.values()) if isinstance(raw, dict) else raw

# ─── GUI ───
class App:
    def __init__(self, root):
        self.root = root
        self.root.title("The Bazaar 预览器")
        self.root.geometry("900x720")
        self.root.configure(bg="#1a1a2e")

        self.merchants, self.cards, self.translations = load_data()
        self.tier_probs = load_tier_data()
        self.events_data = load_json("events.json")
        self.cg_data = load_json("cards_generated.json")
        self.event_rewards = load_json("event_rewards.json") if os.path.exists(os.path.join(DATA_DIR, "event_rewards.json")) else {}
        self._precompute_build_index()

        self.selected_hero = None
        self.mode = "shop"
        self.pool = []
        self.current_day = 1
        self.available_tiers = get_available_tiers(1, self.tier_probs)
        self.com_hero = "__common__"

        # CSV 事件分类定义
        self.csv_categories = [
            ("抉择事件", "进入选择获取物品或金币", "#c080e0"),
            ("战利品获取", "铜银金钻四种品质", "#e0c040"),
            ("获取一个物品", "银金钻三种品质", "#4ec94e"),
            ("强化", "升级、附魔、增强", "#e08040"),
            ("其他", "各种各样", "#808080"),
            ("专属事件", "英雄专属", "#40c0c0"),
            ("永久增益", "永久增益", "#e040e0"),
            ("特殊商店", "花钱的商店", "#e0c080"),
            ("获取技能", "技能事件及教练", "#4ea0e0"),
            ("临时增益", "一天的增益", "#c0c040"),
            ("删除", "没有选择的余地", "#666666"),
        ]
        self.event_cat_vars = {}
        self._build_ui()

    # ═══════════ UI 构建 ═══════════
    def _build_ui(self):
        main = tk.Frame(self.root, bg="#1a1a2e")
        main.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        # 标题
        tf = tk.Frame(main, bg="#1a1a2e")
        tf.pack(fill=tk.X, pady=(0,6))
        tk.Label(tf, text="The Bazaar 预览器", font=("微软雅黑",16,"bold"), fg="#e0c080", bg="#1a1a2e").pack(side=tk.LEFT)
        self.total_label = tk.Label(tf, text="", font=("微软雅黑",12), fg="#80c0ff", bg="#1a1a2e")
        self.total_label.pack(side=tk.RIGHT)

        # 模式切换
        mf = tk.Frame(main, bg="#1a1a2e")
        mf.pack(fill=tk.X, pady=(0,4))
        self.mode_btns = {}
        for i, (key, label) in enumerate([("shop","商店"),("event","事件"),("skill","技能"),("coach","教练"),("monster","怪物")]):
            btn = tk.Button(mf, text=label, font=("微软雅黑",11,"bold"),
                           bg="#333333", fg="#aaaaaa", relief=tk.FLAT, padx=16, pady=4,
                           command=lambda k=key: self._switch_mode(k))
            btn.pack(side=tk.LEFT, padx=(0,4))
            self.mode_btns[key] = btn
        self.mode_btns["shop"].configure(bg="#606060", fg="#e0c080", relief=tk.SUNKEN)

        # 英雄
        hf = tk.Frame(main, bg="#1a1a2e")
        hf.pack(fill=tk.X, pady=(0,4))
        tk.Label(hf, text="英雄:", font=("微软雅黑",11), fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0,8))
        self.hero_btns = {}
        all_btn = tk.Button(hf, text="全部", font=("微软雅黑",9,"bold"), bg="#555555", fg="#ffffff", relief=tk.FLAT, padx=8,
                            command=lambda: self._select_hero(None))
        all_btn.pack(side=tk.LEFT, padx=2)
        self.hero_btns[None] = all_btn
        com_btn = tk.Button(hf, text="COM", font=("微软雅黑",9,"bold"), bg="#333333", fg="#80c080", relief=tk.FLAT, padx=8,
                            command=lambda: self._select_hero(self.com_hero))
        com_btn.pack(side=tk.LEFT, padx=2)
        self.hero_btns[self.com_hero] = com_btn
        for h in HEROES:
            btn = tk.Button(hf, text=HERO_ABBREV.get(h,h[:3]), font=("微软雅黑",9,"bold"),
                           bg="#333333", fg=HERO_COLORS.get(h,"#888"), relief=tk.FLAT, padx=8,
                           command=lambda hero=h: self._select_hero(hero))
            btn.pack(side=tk.LEFT, padx=2)
            self.hero_btns[h] = btn

        # 天数
        df = tk.Frame(main, bg="#1a1a2e")
        df.pack(fill=tk.X, pady=(0,4))
        tk.Label(df, text="天数:", font=("微软雅黑",11), fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0,8))
        self.day_var = tk.IntVar(value=1)
        self.day_scale = tk.Scale(df, from_=1, to=20, orient=tk.HORIZONTAL, variable=self.day_var, length=300,
                                  bg="#2a2a3e", fg="#e0c080", troughcolor="#1a1a2e", highlightthickness=0, bd=0,
                                  command=self._on_day_change)
        self.day_scale.pack(side=tk.LEFT)
        self.day_label = tk.Label(df, text="Day 1", font=("微软雅黑",11,"bold"), fg="#e0c080", bg="#1a1a2e")
        self.day_label.pack(side=tk.LEFT, padx=(10,20))
        TIER_ABB = {"Bronze":"B","Silver":"S","Gold":"G","Diamond":"D"}
        ts = "/".join(TIER_ABB.get(t,t[0]) for t in sorted(self.available_tiers))
        self.tier_info_label = tk.Label(df, text=f"可用: {ts}", font=("微软雅黑",9), fg="#808080", bg="#1a1a2e")
        self.tier_info_label.pack(side=tk.LEFT)

        ttk.Separator(main, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=4)

        # ── 事件分类按钮（仅事件模式可见，在选择栏上方）──
        self.cat_frame = tk.Frame(main, bg="#1a1a2e")
        self.cat_btns = {}
        for i, (cat_name, cat_desc, cat_color) in enumerate(self.csv_categories):
            active = (i == 0)  # 默认只激活第一个
            self.event_cat_vars[cat_name] = active
            btn = tk.Button(self.cat_frame, text=cat_name, font=("微软雅黑",9,"bold"),
                           bg="#606060" if active else "#333333",
                           fg=cat_color, relief=tk.SUNKEN if active else tk.FLAT, padx=6, pady=2,
                           command=lambda cn=cat_name: self._toggle_cat(cn))
            btn.pack(side=tk.LEFT, padx=1, pady=1)
            self.cat_btns[cat_name] = btn

        # ── 选择栏（商店/事件/技能下拉）──
        self.sel_frame = tk.Frame(main, bg="#1a1a2e")
        self.sel_frame.pack(fill=tk.X, pady=(4,2))
        # cat_frame 在 sel_frame 之前插入
        self.cat_frame.pack(fill=tk.X, pady=(2,2), before=self.sel_frame)
        self.cat_frame.pack_forget()  # 初始隐藏
        tk.Label(self.sel_frame, text="选择:", font=("微软雅黑",11), fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0,8))
        self.sel_var = tk.StringVar()
        self.sel_combo = ttk.Combobox(self.sel_frame, textvariable=self.sel_var, font=("微软雅黑",10),
                                       state="readonly", width=40)
        self.sel_combo.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.sel_combo.bind("<<ComboboxSelected>>", self._on_sel_change)

        # 介绍
        self.desc_frame = tk.Frame(main, bg="#2a2a3e", height=28)
        self.desc_frame.pack(fill=tk.X, pady=(2,4))
        self.desc_frame.pack_propagate(False)
        self.desc_label = tk.Label(self.desc_frame, text="", font=("微软雅黑",10), fg="#b0b0b0", bg="#2a2a3e",
                                    anchor=tk.W, justify=tk.LEFT, wraplength=860)
        self.desc_label.pack(fill=tk.BOTH, padx=10, pady=4)

        ttk.Separator(main, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=4)

        # Canvas
        # ── 底部备注区（仅事件模式）──
        self.note_frame = tk.Frame(main, bg="#1a1a2e")
        tk.Label(self.note_frame, text="备注:", font=("微软雅黑",11), fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0,8))
        self.note_text = tk.Text(self.note_frame, font=("微软雅黑",10), fg="#c0c0c0", bg="#2a2a3e",
                                  height=4, wrap=tk.WORD, insertbackground="#c0c0c0")
        self.note_text.pack(side=tk.LEFT, fill=tk.X, expand=True)
        self.note_text.bind("<KeyRelease>", lambda e: self._save_note())

        # 加载持久备注
        self.note_path = os.path.join(DATA_DIR, "event_notes.json")
        self._notes_data = {}
        if os.path.exists(self.note_path):
            try:
                with open(self.note_path, "r", encoding="utf-8") as f:
                    self._notes_data = json.load(f)
            except: pass

        lc = tk.Frame(main, bg="#1a1a2e")
        lc.pack(fill=tk.BOTH, expand=True)
        self.canvas = tk.Canvas(lc, bg="#1e1e32", highlightthickness=0)
        sb = ttk.Scrollbar(lc, orient=tk.VERTICAL, command=self.canvas.yview)
        self.items_frame = tk.Frame(self.canvas, bg="#1e1e32")
        self.items_frame.bind("<Configure>", lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.create_window((0,0), window=self.items_frame, anchor=tk.NW, tags="items_window")
        self.canvas.configure(yscrollcommand=sb.set)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sb.pack(side=tk.RIGHT, fill=tk.Y)
        self.canvas.bind("<Enter>", lambda e: self.canvas.bind_all("<MouseWheel>", self._on_mousewheel))
        self.canvas.bind("<Leave>", lambda e: self.canvas.unbind_all("<MouseWheel>"))
        self.canvas.bind("<Configure>", self._on_canvas_resize)

        self._switch_mode("shop")

    def _on_mousewheel(self, event):
        self.canvas.yview_scroll(int(-1*(event.delta/120)), "units")
    def _on_canvas_resize(self, event):
        self.canvas.itemconfig("items_window", width=event.width)

    def _precompute_build_index(self):
        builds = load_builds()
        index = {}
        for b in builds:
            hero = b.get("hero", "").lower()
            if not hero: continue
            dr = b.get("day_range", [])
            if not dr or len(dr) < 1: continue
            start_day = int(dr[0]) if dr[0] is not None else 1
            end_day = int(dr[1]) if len(dr) > 1 and dr[1] is not None else 20
            for day in range(start_day, end_day + 1):
                key = (hero, day)
                if key not in index: index[key] = {}
                for cn in b.get("core_cards", []):
                    c = cn.lower()
                    if c not in index[key] or index[key][c] != "core":
                        index[key][c] = "core"
                for cn in b.get("transition_cards", []):
                    c = cn.lower()
                    if c not in index[key]: index[key][c] = "transition"
                for cn in b.get("optional_cards", []):
                    c = cn.lower()
                    if c not in index[key]: index[key][c] = "optional"
        self.build_index = index

    # ═══════════ 模式切换 ═══════════
    def _switch_mode(self, mode):
        self.mode = mode
        for k, btn in self.mode_btns.items():
            if k == mode: btn.configure(bg="#606060", fg="#e0c080", relief=tk.SUNKEN)
            else: btn.configure(bg="#333333", fg="#aaaaaa", relief=tk.FLAT)
        # 显示/隐藏分类筛选、推荐和备注
        if mode == "event":
            self.cat_frame.pack(fill=tk.X, pady=(2,2), before=self.sel_frame)
            self.note_frame.pack(fill=tk.X, pady=(4,0))
        else:
            self.cat_frame.pack_forget()
            self.note_frame.pack_forget()
        self._refresh_list()

    # ═══════════ 英雄/天数 ═══════════
    def _select_hero(self, hero):
        self.selected_hero = hero
        for h, btn in self.hero_btns.items():
            btn.configure(bg="#606060" if h==hero else "#333333", relief=tk.SUNKEN if h==hero else tk.FLAT)
        self._refresh_list()

    def _on_day_change(self, val):
        self.current_day = int(float(val))
        self.available_tiers = get_available_tiers(self.current_day, self.tier_probs)
        TIER_ABB = {"Bronze":"B","Silver":"S","Gold":"G","Diamond":"D"}
        self.tier_info_label.configure(text=f"可用: {'/'.join(TIER_ABB.get(t,t[0]) for t in sorted(self.available_tiers))}")
        self.day_label.configure(text=f"Day {self.current_day}")
        self._refresh_list()

    def _toggle_cat(self, cat_name):
        # 单选：取消所有其他，选中当前
        for cn in self.event_cat_vars:
            self.event_cat_vars[cn] = (cn == cat_name)
        for cn, btn in self.cat_btns.items():
            active = self.event_cat_vars[cn]
            btn.configure(bg="#606060" if active else "#333333", relief=tk.SUNKEN if active else tk.FLAT)
        self._refresh_list()

    def _translate_notes(self, notes):
        """用游戏翻译文件翻译事件描述"""
        if not notes: return ""
        return tr(notes, self.translations)

    def _build_reward_pool(self, hero, rewards_config, evt_tier=""):
        """根据专属奖励配置构建物品池"""
        pool = []
        for r in rewards_config:
            rh_list = [h.strip().upper() for h in r['hero'].split()]
            # 处理缩写：VAN→Vanessa 等
            rh_full = set()
            for rh in rh_list:
                rh_full.add(rh)
                # 查缩写对应的全名
                for h_name, abbrev in HERO_ABBREV.items():
                    if rh == abbrev.upper():
                        rh_full.add(h_name.upper())
            if hero.upper() not in rh_full and r['hero'].upper() != hero.upper() and r['hero'] != hero:
                continue
            reward = r['reward']
            tags, tier, size, is_hero = self._parse_reward(reward)
            if evt_tier: tier = evt_tier
            if is_hero:
                # 英雄专属物品：品质与天数挂钩
                use_tiers = {tier} if tier else self.available_tiers
                for k, card in self.cards.items():
                    ch = [h.lower() for h in (card.get("heroes",[]) or [])]
                    if hero.lower() not in ch and "common" not in ch: continue
                    if size:
                        cs = card.get("size","").lower()
                        if cs != size: continue
                    ct = set(t.lower() for t in (card.get("tiers",[]) or []))
                    ct.discard("legendary")
                    if not (ct & {t.lower() for t in use_tiers}): continue
                    pool.append(card)
            elif tags or tier:
                for k, card in self.cards.items():
                    ch = [h.lower() for h in (card.get("heroes",[]) or [])]
                    if hero.lower() not in ch and "common" not in ch: continue
                    ct = card.get("tiers",[]) or []
                    if tier and tier.lower() not in [t.lower() for t in ct]: continue
                    if size:
                        cs = card.get("size","").lower()
                        if cs != size: continue
                    if tags:
                        ct_all = [t.lower() for t in (card.get("tags",[]) or [])]
                        ct_all += [t.lower() for t in (card.get("hidden_tags",[]) or [])]
                        if not any(t in ct_all for t in tags): continue
                    pool.append(card)
        return pool

    def _parse_reward(self, reward):
        """解析奖励文本返回 (tags, tier, size, is_hero_items)"""
        tags, tier, size = [], None, None
        is_hero_items = False
        r = reward
        # 专属物品
        if "专属" in r and "物品" in r: is_hero_items = True
        # 等级
        TIER_MAP = {
            "青铜": "Bronze", "铜": "Bronze",
            "白银": "Silver", "银": "Silver",
            "黄金": "Gold", "金": "Gold",
            "钻石": "Diamond", "钻": "Diamond",
            "Bronze": "Bronze", "Silver": "Silver", "Gold": "Gold", "Diamond": "Diamond",
        }
        for cn, en in TIER_MAP.items():
            if cn in r: tier = en; break
        # 尺寸
        if "小型" in r: size = "small"
        elif "中型" in r: size = "medium"
        elif "大型" in r: size = "large"
        # 标签
        TAG_MAP = {"武器":"weapon","护盾":"shield","伙伴":"friend","水系":"aquatic",
            "灼烧":"burn","毒药":"poison","治疗":"heal","回复":"regen","弹药":"ammo",
            "冻结":"freeze","加速":"haste","减速":"slow","科技":"tech","工具":"tool",
            "地产":"property","药水":"potion","飞行":"flying","载具":"vehicle",
            "食物":"food","玩具":"toy","暴击":"crit","伤害":"damage","遗物":"relic",
            "服饰":"apparel","经济":"economic","原料":"reagent","试剂":"reagent"}
        for cn, en in TAG_MAP.items():
            if cn in r: tags.append(en)
        return tags, tier, size, is_hero_items

    def _on_filter_change(self):
        self._refresh_list()

    # ═══════════ 下拉刷新 ═══════════
    def _refresh_list(self):
        hero = self.selected_hero
        hero_lower = hero.lower() if hero and hero != self.com_hero else None
        is_com = (hero == self.com_hero)

        values = []
        self._sel_lookup = {}

        if self.mode == "shop":
            for m in self.merchants:
                mh = m.get("heroes",[])
                if hero is None: pass
                else:
                    cross = m.get("cross_hero",False)
                    hl = [h.lower() for h in mh]
                    if not cross and "common" not in hl and hero.lower() not in hl: continue
                name_zh = tr(m["name"], self.translations)
                desc = m.get("desc_zh","") or m.get("desc","")
                display = f"{name_zh}  [{desc}]" if desc else name_zh
                values.append(display)
                self._sel_lookup[display] = ("shop", m)

        elif self.mode == "event":
            active_cats = {cn for cn,_,_ in self.csv_categories if self.event_cat_vars.get(cn, False)}
            for cat_name, cat_desc, cat_color in self.csv_categories:
                if cat_name not in active_cats: continue
                evs = self._get_events_by_csv_cat(cat_name)
                for evt in evs:
                    eh = [h.lower() for h in evt.get("event_heroes",[])]
                    if is_com:
                        if "common" not in eh: continue
                    elif hero_lower:
                        if hero_lower not in eh and "common" not in eh: continue
                    zh = tr(evt.get("name",""), self.translations)
                    display = f"[{cat_name}] {zh}"
                    values.append(display)
                    self._sel_lookup[display] = ("event", evt)

        elif self.mode == "skill":
            skill_shops = self.events_data.get("skill_shops", [])
            for evt in skill_shops:
                eh = [h.lower() for h in evt.get("event_heroes",[])]
                if is_com:
                    if "common" not in eh: continue
                elif hero_lower:
                    if hero_lower not in eh and "common" not in eh: continue
                zh = tr(evt.get("name",""), self.translations)
                values.append(zh)
                self._sel_lookup[zh] = ("skill", evt)

        elif self.mode == "coach":
            # 教练从 cards_generated 中提取 Skill 类型且 heroes 非空的
            coaches = {}
            for k, v in self.cg_data.items():
                if v.get("type") != "Skill": continue
                skill_name = v.get("internal_name","")
                heroes = v.get("heroes",[]) or []
                if not heroes or heroes == ["Common"]: continue
                for h in heroes:
                    if h not in coaches: coaches[h] = []
                    coaches[h].append(skill_name)
            for h in sorted(coaches.keys()):
                if is_com: continue
                if hero_lower and hero_lower != h.lower(): continue
                zh = tr(h, self.translations)
                display = f"{zh} ({len(coaches[h])}个技能)"
                values.append(display)
                self._sel_lookup[display] = ("coach", {"hero": h, "skills": coaches[h]})

        elif self.mode == "monster":
            # 从 cards_generated 提取 CombatEncounter
            monsters = [v for v in self.cg_data.values() if v.get("type")=="CombatEncounter"]
            for m in monsters:
                name = m.get("internal_name","")
                if "(monster)" not in name.lower(): continue
                heroes = m.get("heroes",[]) or []
                if is_com:
                    if "common" not in [h.lower() for h in heroes]: continue
                elif hero_lower:
                    if hero_lower not in [h.lower() for h in heroes]: continue
                zh = tr(name, self.translations)
                values.append(zh)
                self._sel_lookup[zh] = ("monster", m)

        self.sel_combo["values"] = values
        if values:
            old = self.sel_var.get()
            if old in self._sel_lookup:
                self.sel_var.set(old)
            else:
                self.sel_var.set(values[0])
            self._on_sel_change()
        else:
            self.sel_var.set('')
            self.desc_label.configure(text='')
            self._render_items(None)

    def _load_csv_map(self):
        """从CSV构建分类→事件中文名列表的映射"""
        csv_names = {
            "抉择事件": "丛林遗迹,奇异蘑菇,松露种植,地下抵抗组织,码头,茸茸小怪兽,失落宝箱,大巴扎嘉年华,达波拉,长袍怪商",
            "战利品获取": "双重提纯,废品回收,打磨套组,月光草甸,逐烬之石,采购医疗包",
            "永久增益": "复元酊剂,自我投资,工匠沙丘,芬恩饱餐餐厅,药房",
            "其他": "曼荼罗,灵光一闪,航空站,街头庆典,许愿喷泉,贝克斯,哈迪,拆卸场,教团,盗贼行会,进阶特训",
            "获取一个物品": "丰硕收获,军械库,冷库,医院,原料收成,厨柜,回收中心,多功能箱,失物招领,守卫储物柜,安保中心,家中派对,工坊,恐龙陷阱捕捉,战场,挖掘行动,植物园,海边垂钓,深海捕捞,火葬堆,炼金实验室,熔炉,磁能储仓,科技废品,荒废地产,药品柜,藏宝箱,街区派对,赛道,障碍赛道",
            "特殊商店": "口香糖球贩售机,糖果蛇丽基特,大胃王竞赛,珍珠的考古发掘场,旅行代理人",
            "临时增益": "朱尔斯的咖啡店",
            "专属事件": "经济研讨会,隐秘之湖,杜利的小屋,倒影池,先祖墓,劳雷尔的梦魇,家庭团聚,布罗林大厨,理财推销",
            "获取技能": "图书馆,宗师",
            "强化": "农贸集市,发射塔,弗尔姆,德弗莱克,机器人工厂,极光穹顶,波图,烈焰尔,疯狂麦蒂,阿尔德里科",
            "删除": "指挥中枢,水晶培养室,神庙宝库,货舱,赏金猎人,坠落地点探险,神庙圣物匣,神庙探险",
        }
        result = {}
        for cat, names_str in csv_names.items():
            names = set()
            for n in names_str.split(","):
                n = n.strip()
                if n: names.add(n)
            result[cat] = names
        return result

    def _get_events_by_csv_cat(self, cat_name):
        """根据CSV分类名返回事件列表（含品质变体），用CSV中文名精确匹配"""
        csv_map = self._load_csv_map()
        target_names = csv_map.get(cat_name, set())
        if not target_names:
            return []

        tiers = []
        if cat_name == "战利品获取":
            tiers = ["Bronze", "Silver", "Gold", "Diamond"]
        elif cat_name == "获取一个物品":
            tiers = ["Silver", "Gold", "Diamond"]

        result = []
        seen = set()
        TIER_ZH = {"Bronze":"铜","Silver":"银","Gold":"金","Diamond":"钻"}
        for sc, items in self.events_data.items():
            if sc in ("shops",): continue
            for evt in items:
                zh = tr(evt.get("name",""), self.translations)
                en = evt.get("name","")
                if zh not in target_names and en not in target_names:
                    continue
                # 去重：同中文名只保留第一个
                if zh in seen: continue
                seen.add(zh)
                if tiers:
                    for t in tiers:
                        variant = dict(evt)
                        variant["name"] = f"{zh} ({TIER_ZH.get(t,t)})"
                        variant["_tier"] = t
                        result.append(variant)
                else:
                    result.append(evt)
        return result

    def _on_sel_change(self, event=None):
        display = self.sel_var.get()
        if display in self._sel_lookup:
            sel_type, data = self._sel_lookup[display]
            if sel_type == "shop":
                desc = data.get("desc_zh","") or data.get("desc","")
                self.desc_label.configure(text=desc)
            elif sel_type == "event":
                notes = data.get("notes","")
                desc_zh = self._translate_notes(notes)
                if not desc_zh: desc_zh = tr(data.get("name",""), self.translations)
                tags = data.get("reward_tags",[]) or []
                TAG_ZH = {"weapon":"武器","shield":"护盾","friend":"伙伴","aquatic":"水系","burn":"灼烧","poison":"毒药","heal":"治疗","regen":"回复","ammo":"弹药","freeze":"冻结","haste":"加速","slow":"减速","tech":"科技","tool":"工具","property":"地产","potion":"药水","flying":"飞行","vehicle":"载具","food":"食物","toy":"玩具","crit":"暴击","damage":"伤害","relic":"遗物","apparel":"服饰","economic":"经济","loot":"战利品","value":"价值","income":"收入","exp":"经验","health":"生命","gold":"金币","maxhealth":"最大生命","cooldown":"冷却","rage":"怒气"}
                if tags: desc_zh += f"  标签: {', '.join(TAG_ZH.get(t,t) for t in tags)}"
                self.desc_label.configure(text=desc_zh)
            elif sel_type == "skill":
                notes = data.get("notes","")
                self.desc_label.configure(text=notes)
            elif sel_type == "coach":
                skills_zh = [tr(s, self.translations) for s in data["skills"]]
                self.desc_label.configure(text=f"可教技能: {', '.join(skills_zh)}")
            elif sel_type == "monster":
                self.desc_label.configure(text=f"怪物 - 英雄: {', '.join(data.get('heroes',[]))}")
            self._render_items((sel_type, data))
            # 加载事件备注（用中文基础名，所有英雄通用）
            if sel_type == "event":
                note_key = tr(data.get("name",""), self.translations)
                # 去品质/天数后缀
                note_key = re.sub(r'\s*\((?:铜|银|金|钻|Bronze|Silver|Gold|Diamond|Day[^)]+)\)\s*$', '', note_key).strip()
                saved = self._notes_data.get(note_key, "")
                self.note_text.delete("1.0", tk.END)
                if saved:
                    self.note_text.insert("1.0", saved)
                else:
                    rewards_config = self.event_rewards.get(note_key)
                    if rewards_config:
                        lines = []
                        for r in rewards_config:
                            cond = r['hero']
                            reward = r['reward']
                            # 标注条件类型
                            if ' ' in cond and not any(c.isdigit() for c in cond):
                                lines.append(f"[英雄] {cond}: {reward}")
                            elif any(c.isdigit() for c in cond):
                                lines.append(f"[计数] {cond}: {reward}")
                            elif cond:
                                lines.append(f"[拥有] {cond}: {reward}")
                        self.note_text.insert("1.0", '\n'.join(lines))
            else:
                self.note_text.delete("1.0", tk.END)
        else:
            self.desc_label.configure(text='')
            self._render_items(None)

    # ═══════════ 物品渲染 ═══════════
    def _render_items(self, sel_data):
        for w in self.items_frame.winfo_children():
            w.destroy()

        if sel_data is None:
            self.total_label.configure(text="")
            return

        sel_type, data = sel_data
        hero = self.selected_hero
        pool = []

        if sel_type == "shop":
            if hero is None:
                tk.Label(self.items_frame, text="请先选择英雄", font=("微软雅黑",11), fg="#666", bg="#1e1e32").pack(pady=20)
                self.total_label.configure(text="")
                return
            pool = build_shop_pool(hero, data, self.cards, self.available_tiers)
            self.total_label.configure(text=f"总计: {len(pool)} 件")

        elif sel_type == "event":
            evt_name = data.get("name","")
            evt_tier = data.get("_tier","")
            # 去掉品质后缀匹配（中文+英文）
            base_name = re.sub(r'\s*\((?:铜|银|金|钻|Bronze|Silver|Gold|Diamond)\)\s*$', '', evt_name).strip()
            zh_name = tr(base_name, self.translations) if base_name else ""
            rewards_config = (self.event_rewards.get(evt_name) or self.event_rewards.get(base_name)
                              or self.event_rewards.get(zh_name))
            # 事件固定品质：用事件自身的等级而非天数等级
            event_tiers = {evt_tier} if evt_tier else self.available_tiers
            if rewards_config:
                if hero is None:
                    tk.Label(self.items_frame, text="请先选择英雄", font=("微软雅黑",11), fg="#666", bg="#1e1e32").pack(pady=20)
                    self.total_label.configure(text="")
                    return
                pool = self._build_reward_pool(hero, rewards_config, evt_tier)
                self.total_label.configure(text=f"奖励物品: {len(pool)} 件")
                if not pool:
                    for r in rewards_config:
                        rh = [h.strip().upper() for h in r['hero'].split()]
                        if hero.upper() in rh or r['hero'] == hero or r['hero'].upper() == hero.upper():
                            tk.Label(self.items_frame, text=f"奖励: {r['reward']}", font=("微软雅黑",11),
                                     fg="#e0c080", bg="#1e1e32").pack(pady=10)
                    return
            else:
                if hero is None:
                    tk.Label(self.items_frame, text="请先选择英雄", font=("微软雅黑",11), fg="#666", bg="#1e1e32").pack(pady=20)
                    self.total_label.configure(text="")
                    return
                card_reward = data.get("card_reward",{}) or {}
                reward_tags = data.get("reward_tags",[]) or card_reward.get("reward_tags",[])
                exact_names = data.get("exact_names",[]) or []
                if exact_names:
                    for ename in exact_names:
                        card = self.cards.get(ename.lower())
                        if card:
                            ch = [h.lower() for h in card.get("heroes",[])]
                            if hero.lower() in ch or "common" in ch:
                                ct = card.get("tiers",[]) or []
                                if not evt_tier or evt_tier in ct:
                                    pool.append(card)
                elif reward_tags or card_reward:
                    # 分离排除标签 (!weapon) 和包含标签
                    tags_all = reward_tags or card_reward.get("reward_tags",[])
                    exclude_set = {t[1:].lower() for t in tags_all if t.startswith('!')}
                    include_tags = [t.lower() for t in tags_all if not t.startswith('!')]
                    # 直接用简易过滤（与C#插件一致）
                    for k, card in self.cards.items():
                        ct = card.get("tiers",[]) or []
                        if event_tiers and not any(t.lower() in {x.lower() for x in event_tiers} for t in ct): continue
                        ch = [h.lower() for h in (card.get("heroes",[]) or [])]
                        if hero.lower() not in ch and "common" not in ch: continue
                        all_tags = [t.lower() for t in (card.get("tags",[])+card.get("hidden_tags",[]))]
                        if include_tags and not any(t in all_tags for t in include_tags): continue
                        if exclude_set and any(t in all_tags for t in exclude_set): continue
                        pool.append(card)
            self.total_label.configure(text=f"物品: {len(pool)} 件")

        elif sel_type == "skill":
            if hero is None:
                tk.Label(self.items_frame, text="请先选择英雄", font=("微软雅黑",11), fg="#666", bg="#1e1e32").pack(pady=20)
                self.total_label.configure(text="")
                return
            card_reward = data.get("card_reward",{}) or data.get("shop_pool",{}) or {}
            reward_tags = data.get("reward_tags",[]) or card_reward.get("reward_tags",[]) or data.get("skill_tags",[])
            if reward_tags:
                fake = {"name":data.get("name",""),"heroes":[hero],"tags":reward_tags,"cross_hero":False,"size":"","exclude_tags":[]}
                pool = build_shop_pool(hero, fake, self.cards, self.available_tiers)
            # 也查 Skills
            skill_cards = {k.lower():v for k,v in self.cg_data.items() if v.get("type")=="Skill"}
            for k, v in skill_cards.items():
                sk_heroes = [h.lower() for h in (v.get("heroes",[]) or [])]
                if hero.lower() in sk_heroes or "common" in sk_heroes:
                    pool.append({"internal_name":k,"size":"Medium","_is_skill":True})
            self.total_label.configure(text=f"技能: {len(pool)} 件")

        elif sel_type == "coach":
            skills = data.get("skills",[])
            for s in skills:
                pool.append({"internal_name":s,"size":"Medium","_is_skill":True})
            self.total_label.configure(text=f"技能: {len(pool)} 个")

        elif sel_type == "monster":
            name = data.get("internal_name","")
            pool.append({"internal_name":name,"size":"Medium","_is_monster":True})
            self.total_label.configure(text="怪物")

        if not pool:
            tk.Label(self.items_frame, text="(无可用物品)", font=("微软雅黑",11), fg="#666", bg="#1e1e32").pack(pady=20)
            return

        # 拼音排序
        pool.sort(key=lambda c: lazy_pinyin(tr(c.get("internal_name",""), self.translations)))

        # 分组渲染
        groups = {"small":[],"medium":[],"large":[],"other":[]}
        size_labels = {"small":"小型","medium":"中型","large":"大型"}
        for card in pool:
            sz = card.get("size","").lower()
            if sz in groups: groups[sz].append(card)
            else: groups["other"].append(card)
        for g in groups.values():
            g.sort(key=lambda c: lazy_pinyin(tr(c.get("internal_name",""), self.translations)))

        for key in ["small","medium","large","other"]:
            items = groups[key]
            if not items: continue
            label = size_labels.get(key, key.capitalize())
            header = tk.Frame(self.items_frame, bg="#2a2a40")
            header.pack(fill=tk.X, pady=(8,2) if key!="small" else (2,2))
            tk.Label(header, text=f"■ {label} ({len(items)}件)", font=("微软雅黑",11,"bold"), fg="#e0c080", bg="#2a2a40").pack(side=tk.LEFT, padx=10, pady=2)
            row_frame = None
            for i, card in enumerate(items):
                if i % 5 == 0:
                    row_frame = tk.Frame(self.items_frame, bg="#1e1e32")
                    row_frame.pack(fill=tk.X, padx=10)
                name_en = card.get("internal_name","???")
                name_zh = tr(name_en, self.translations)
                display = name_zh if name_zh != name_en else name_en
                if card.get("_is_skill"): display = f"★{display}"
                if card.get("_is_monster"): display = f"☠{display}"
                fg = "#d0d0d0"
                if card.get("_is_skill"): fg = "#80c0ff"
                if card.get("_is_monster"): fg = "#e06060"
                tk.Label(row_frame, text=display, font=("微软雅黑",10), fg=fg, bg="#1e1e32", padx=6, pady=1).pack(side=tk.LEFT)

        self.canvas.yview_moveto(0)

    def _save_note(self):
        """保存当前事件备注到JSON"""
        display = self.sel_var.get()
        if display not in self._sel_lookup: return
        sel_type, data = self._sel_lookup[display]
        if sel_type != "event": return
        note_key = tr(data.get("name", ""), self.translations)
        note_key = re.sub(r'\s*\((?:铜|银|金|钻|Bronze|Silver|Gold|Diamond|Day[^)]+)\)\s*$', '', note_key).strip()
        text = self.note_text.get("1.0", "end-1c")
        if text.strip():
            self._notes_data[note_key] = text
        else:
            self._notes_data.pop(note_key, None)
        try:
            with open(self.note_path, "w", encoding="utf-8") as f:
                json.dump(self._notes_data, f, ensure_ascii=False, indent=2)
        except: pass



if __name__ == "__main__":
    root = tk.Tk()
    App(root)
    root.mainloop()
