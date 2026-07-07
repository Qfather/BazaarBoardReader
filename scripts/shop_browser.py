#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""The Bazaar 商店浏览器 — 独立 tkinter GUI"""

import json
import os
import re
import tkinter as tk
from pypinyin import lazy_pinyin
from tkinter import ttk

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(BASE_DIR, "data")

# ─── 英雄配置 ───
HEROES = ["Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle", "Jules", "Karnok"]
HERO_ABBREV = {
    "Vanessa": "VAN", "Pygmalien": "PYG", "Dooley": "DOO", "Mak": "MAK",
    "Stelle": "STE", "Jules": "JUL", "Karnok": "KAR",
}
HERO_COLORS = {
    "Vanessa": "#e04040", "Pygmalien": "#4080e0", "Dooley": "#e0a000",
    "Mak": "#30a030", "Stelle": "#e0e040", "Jules": "#a040d0", "Karnok": "#00b0b0",
}

# ─── 数据加载 ───
def load_json(filename):
    path = os.path.join(DATA_DIR, filename)
    if not os.path.exists(path):
        path = os.path.join(BASE_DIR, filename)
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)

def load_data():
    merchants_raw = load_json("merchants.json")
    cards_raw = load_json("cards.json")
    trans_raw = load_json("translations_zh_cn.json")
    translations = trans_raw.get("by_name", {})

    # 构建卡片索引: { internal_name_lower: card }
    cards = {}
    for key, card in cards_raw.get("cards", {}).items():
        name = card.get("internal_name", key)
        cards[name.lower()] = card

    # 翻译查找函数
    def has_trans(name):
        zh = translations.get(name, '')
        return zh and zh != name

    # 过滤：只保留真实存在的 Item
    BLACKLIST = {'assembly line', 'augment reagents',
        # Dooley 初始 Core（不在商店出售）
        'armored core', 'companion core', 'critical core',
        'focused core', 'ignition core', 'launcher core',
        'the core', 'weaponized core', 'oblivion core',
        # 无效/抽奖专属物品
        'unused card', "magician's top hat",
        'blue gumball', 'green gumball', 'red gumball', 'yellow gumball'}
    filtered = {}
    for k, v in cards.items():
        if v.get('type') != 'Item': continue
        if 'DEBUG' in k.upper() or 'TEMPLATE' in k.upper(): continue
        if 'COMMUNITY TEAM' in k.upper(): continue
        if 'Ship [OnBuy]' in k: continue
        name = v.get('internal_name', '')
        if name.lower() in BLACKLIST: continue
        if "'s Package" in name: continue
        if re.search(r' [A-LR]$', name): continue
        tags = v.get('tags', []) or []
        if 'Loot' in tags and 'Crystal' in name: continue  # 水晶探险奖品，非商店物品
        hidden = v.get('hidden_tags', []) or []
        if 'Package' in hidden: continue
        # 中立物品必须有中文翻译（否则不是游戏内真实物品）
        heroes = v.get('heroes', []) or []
        if heroes == ['Common'] and not has_trans(name):
            continue
        filtered[k] = v
    return merchants_raw, filtered, translations

# ─── 天数 & 等级数据 ───
def load_tier_data():
    """加载物品等级随天数分布"""
    analysis = load_json("game_data_analysis.json")
    raw = analysis.get("item_skill_tier_by_day", {})
    result = {}
    for k, v in raw.items():
        result[int(k)] = v
    return result

def get_available_tiers(day, tier_probs):
    """返回当天概率>0的等级集合"""
    keys = sorted(tier_probs.keys())
    if not keys:
        return set()
    best_key = keys[0]
    for k in keys:
        if k <= day:
            best_key = k
        else:
            break
    prob = tier_probs.get(best_key, {})
    return {t for t, p in prob.items() if p > 0}

def card_available_today(card, available_tiers):
    """检查物品在当前天数是否至少有一个等级可用"""
    card_tiers = set(card.get("tiers", []) or [])
    card_tiers.discard("Legendary")
    return bool(card_tiers & available_tiers)

def blend_alpha(fg_hex, bg_hex="#1e1e32", alpha=0.5):
    """将前景色与背景色混合，模拟50%透明度"""
    fg_r, fg_g, fg_b = int(fg_hex[1:3], 16), int(fg_hex[3:5], 16), int(fg_hex[5:7], 16)
    bg_r, bg_g, bg_b = int(bg_hex[1:3], 16), int(bg_hex[3:5], 16), int(bg_hex[5:7], 16)
    return f"#{int(fg_r*alpha+bg_r*(1-alpha)):02x}{int(fg_g*alpha+bg_g*(1-alpha)):02x}{int(fg_b*alpha+bg_b*(1-alpha)):02x}"

# ─── 商店物品池构建 (复刻 RateShop 逻辑) ───
def card_matches_tags(card_tags, card_desc, merchant_tags):
    """复刻 CardMatchesMerchantTags（修复 Health 误匹配 Heal）"""
    if not merchant_tags:
        return True
    tags_lower = [t.lower() for t in card_tags] if card_tags else []
    for mt in merchant_tags:
        mt_lower = mt.lower()
        # 精确匹配
        if mt_lower in tags_lower:
            return True
        # 引用变体 (e.g. HealReference matches "Heal", 但 Health 不匹配 Heal)
        ref_tag = mt_lower + "reference"
        if ref_tag in tags_lower:
            return True
        # 复数→单数 (e.g. Toys→Toy, Friends→Friend)
        if mt_lower.endswith('s'):
            singular = mt_lower[:-1]
            if singular in tags_lower or (singular + "reference") in tags_lower:
                return True
        # MaxHealth 特殊映射
        if mt_lower == "maxhealth":
            for t in tags_lower:
                if t.startswith("health"):
                    return True
            if card_desc and "max health" in card_desc.lower():
                return True
    return False

def has_exclude_tag(card_tags, exclude_tags):
    if not exclude_tags or not card_tags:
        return False
    tags_lower = set(t.lower() for t in card_tags)
    for et in exclude_tags:
        if et.lower() in tags_lower:
            return True
    return False

def build_shop_pool(hero, merchant, cards, available_tiers=None):
    pool = []
    merchant_heroes = [h.lower() for h in merchant.get("heroes", [])]
    cross_hero = merchant.get("cross_hero", False)
    merchant_tags = merchant.get("tags", [])
    exclude_tags = merchant.get("exclude_tags", [])
    size_filter = [s.strip().lower() for s in merchant.get("size", "").split(",") if s.strip()]
    is_neutral_shop = any(t.lower() == "neutral" for t in merchant_tags) if merchant_tags else False
    # The Tester: 出售所有英雄的科技物品
    if merchant.get("name", "") == "The Tester":
        cross_hero = True
    # The Antiquarian: 仅 VAN PYG DOO MAK KAR
    antiquarian_heroes = {"vanessa", "pygmalien", "dooley", "mak", "karnok"}

    for name_lower, card in cards.items():
        card_heroes = [h.lower() for h in card.get("heroes", [])]
        hero_lower = hero.lower()
        card_is_neutral = (len(card_heroes) == 1 and card_heroes[0] == "common")

        # 所有商店不卖传说物品
        tiers = card.get("tiers", []) or []
        if "Legendary" in tiers:
            continue

        # The Antiquarian: 仅 VAN PYG DOO MAK KAR
        if merchant.get("name", "") == "The Antiquarian":
            if hero_lower not in antiquarian_heroes:
                continue

        # Neutral items only appear in neutral shops or cross_hero shops
        if card_is_neutral and not is_neutral_shop and not cross_hero:
            continue

        # Neutral shop: only neutral items, Curio (Silver tier) = Bronze only
        if is_neutral_shop:
            if not card_is_neutral:
                continue
            # Curio 只卖青铜中立物品
            shop_tier = merchant.get("tier", "")
            if shop_tier == "Silver":
                tiers = card.get("tiers", []) or []
                if "Bronze" not in tiers:
                    continue

        # Hero filter (non-neutral items, non-neutral shops)
        if not card_is_neutral and not is_neutral_shop:
            if cross_hero:
                pass
            elif len(merchant_heroes) == 1 and merchant_heroes[0] == "common":
                if hero_lower not in card_heroes:
                    continue
            else:
                if hero_lower not in card_heroes:
                    continue
                if hero_lower not in merchant_heroes:
                    continue

        # Quality-tier shop filter (品质限定商店)
        TIER_SHOPS = {"silvia": "silver", "goldie": "gold", "luxe": "diamond"}
        if not is_neutral_shop:
            shop_name_lower = merchant.get("name", "").lower()
            if shop_name_lower in TIER_SHOPS:
                allowed_tier = TIER_SHOPS[shop_name_lower]
                card_tiers = [t.lower() for t in (card.get("tiers", []) or [])]
                if allowed_tier not in card_tiers:
                    continue

        # Size filter
        card_size = card.get("size", "").lower()
        if size_filter and card_size not in size_filter:
            continue

        # Tag filter (skip for neutral shops)
        if not is_neutral_shop:
            all_tags = list(card.get("tags", []) or [])
            hidden = card.get("hidden_tags", []) or []
            all_tags.extend(hidden)
            if merchant_tags and not card_matches_tags(all_tags, card.get("description", ""), merchant_tags):
                continue

        # Exclude tags (skip for neutral shops)
        if not is_neutral_shop and exclude_tags:
            all_tags = list(card.get("tags", []) or [])
            hidden = card.get("hidden_tags", []) or []
            all_tags.extend(hidden)
            if has_exclude_tag(all_tags, exclude_tags):
                continue

        # 天数等级过滤：物品至少有一个等级在当天可用
        if available_tiers is not None:
            card_tiers = set(card.get("tiers", []) or [])
            card_tiers.discard("Legendary")
            if not (card_tiers & available_tiers):
                continue
            # 附加上今天可用的等级列表，供 UI 显示
            card["_available_tiers_today"] = sorted(card_tiers & available_tiers)

        pool.append(card)
    return pool
# ─── 翻译 ───
def tr(text, translations):
    result = translations.get(text)
    if result is not None and result != text:
        return result
    # 尝试 (Merchant) 后缀
    result = translations.get(f"{text} (Merchant)")
    if result is not None:
        return result
    return text

def load_builds():
    """加载社区阵容数据"""
    path = os.path.join(DATA_DIR, "community_builds.json")
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8") as f:
        raw = json.load(f)
    if isinstance(raw, dict):
        return list(raw.values())
    return raw

# ─── GUI ───
class ShopBrowser:
    def __init__(self, root):
        self.root = root
        self.root.title("The Bazaar 商店浏览器")
        self.root.geometry("880x700")
        self.root.configure(bg="#1a1a2e")

        # 加载数据
        self.merchants, self.cards, self.translations = load_data()
        self.tier_probs = load_tier_data()

        # 状态
        self.selected_hero = None     # None = 全部
        self.selected_merchant = None
        self.pool = []
        self.current_day = 1
        self.available_tiers = get_available_tiers(1, self.tier_probs)
        self.build_annotations = {}   # {card_name_lower: role}

        self.all_heroes = HEROES

        # 预计算构建索引
        self._precompute_build_index()

        self._build_ui()

    def _precompute_build_index(self):
        """预计算构建索引: (hero_lower, day) -> {card_name_lower: role}"""
        builds = load_builds()
        index = {}
        for b in builds:
            hero = b.get("hero", "").lower()
            if not hero:
                continue
            dr = b.get("day_range", [])
            if not dr or len(dr) < 1:
                continue
            start_day = int(dr[0]) if dr[0] is not None else 1
            end_day = int(dr[1]) if len(dr) > 1 and dr[1] is not None else 20
            for day in range(start_day, end_day + 1):
                key = (hero, day)
                if key not in index:
                    index[key] = {}
                for card_name in b.get("core_cards", []):
                    cn = card_name.lower()
                    if cn not in index[key] or index[key][cn] != "core":
                        index[key][cn] = "core"
                for card_name in b.get("transition_cards", []):
                    cn = card_name.lower()
                    if cn not in index[key]:
                        index[key][cn] = "transition"
                for card_name in b.get("optional_cards", []):
                    cn = card_name.lower()
                    if cn not in index[key]:
                        index[key][cn] = "optional"
        self.build_index = index

    def _get_build_annotations(self):
        """获取当前英雄+天数的构建标注"""
        if not self.selected_hero:
            return {}
        key = (self.selected_hero.lower(), self.current_day)
        return self.build_index.get(key, {})

    def _on_day_change(self, val):
        self.current_day = int(float(val))
        self.available_tiers = get_available_tiers(self.current_day, self.tier_probs)
        self.build_annotations = self._get_build_annotations()
        # 更新可用等级标签
        TIER_ABB = {"Bronze": "B", "Silver": "S", "Gold": "G", "Diamond": "D"}
        tiers_str = "/".join(TIER_ABB.get(t, t[0]) for t in sorted(self.available_tiers))
        self.tier_info_label.configure(text=f"可用: {tiers_str}")
        self.day_label.configure(text=f"Day {self.current_day}")
        self._update_items()

    def _build_ui(self):
        # ── 主框架 ──
        main = tk.Frame(self.root, bg="#1a1a2e")
        main.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        # ── 标题 ──
        title_frame = tk.Frame(main, bg="#1a1a2e")
        title_frame.pack(fill=tk.X, pady=(0, 8))
        tk.Label(title_frame, text="The Bazaar 商店浏览器", font=("微软雅黑", 16, "bold"),
                 fg="#e0c080", bg="#1a1a2e").pack(side=tk.LEFT)
        self.total_label = tk.Label(title_frame, text="总计: 0 件", font=("微软雅黑", 12),
                                    fg="#80c0ff", bg="#1a1a2e")
        self.total_label.pack(side=tk.RIGHT)

        # ── 英雄筛选 ──
        hero_frame = tk.Frame(main, bg="#1a1a2e")
        hero_frame.pack(fill=tk.X, pady=(0, 6))
        tk.Label(hero_frame, text="英雄:", font=("微软雅黑", 11),
                 fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0, 8))

        # 全部按钮
        self.hero_btns = {}
        all_btn = tk.Button(hero_frame, text="全部", font=("微软雅黑", 9, "bold"),
                            bg="#555555", fg="#ffffff", relief=tk.FLAT, padx=8,
                            command=lambda: self._select_hero(None))
        all_btn.pack(side=tk.LEFT, padx=2)
        self.hero_btns[None] = all_btn

        for h in self.all_heroes:
            abbrev = HERO_ABBREV.get(h, h[:3].upper())
            color = HERO_COLORS.get(h, "#888888")
            btn = tk.Button(hero_frame, text=abbrev, font=("微软雅黑", 9, "bold"),
                            bg="#333333", fg=color, relief=tk.FLAT, padx=8,
                            command=lambda hero=h: self._select_hero(hero))
            btn.pack(side=tk.LEFT, padx=2)
            self.hero_btns[h] = btn

        # ── 天数选择 ──
        day_frame = tk.Frame(main, bg="#1a1a2e")
        day_frame.pack(fill=tk.X, pady=(4, 2))
        tk.Label(day_frame, text="天数:", font=("微软雅黑", 11),
                 fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0, 8))
        self.day_var = tk.IntVar(value=1)
        self.day_scale = tk.Scale(day_frame, from_=1, to=20, orient=tk.HORIZONTAL,
                                  variable=self.day_var, length=300, resolution=1,
                                  bg="#2a2a3e", fg="#e0c080", troughcolor="#1a1a2e",
                                  highlightthickness=0, bd=0, command=self._on_day_change)
        self.day_scale.pack(side=tk.LEFT)
        self.day_label = tk.Label(day_frame, text="Day 1", font=("微软雅黑", 11, "bold"),
                                  fg="#e0c080", bg="#1a1a2e")
        self.day_label.pack(side=tk.LEFT, padx=(10, 20))
        TIER_ABB = {"Bronze": "B", "Silver": "S", "Gold": "G", "Diamond": "D"}
        tiers_str = "/".join(TIER_ABB.get(t, t[0]) for t in sorted(self.available_tiers))
        self.tier_info_label = tk.Label(day_frame, text=f"可用: {tiers_str}",
                                        font=("微软雅黑", 9), fg="#808080", bg="#1a1a2e")
        self.tier_info_label.pack(side=tk.LEFT)

        # ── 分隔线 ──
        ttk.Separator(main, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=4)

        # ── 商店筛选 ──
        shop_frame = tk.Frame(main, bg="#1a1a2e")
        shop_frame.pack(fill=tk.X, pady=(0, 4))
        tk.Label(shop_frame, text="商店:", font=("微软雅黑", 11),
                 fg="#aaaaaa", bg="#1a1a2e").pack(side=tk.LEFT, padx=(0, 8))

        self.shop_listbox_frame = tk.Frame(shop_frame, bg="#1a1a2e")
        self.shop_listbox_frame.pack(side=tk.LEFT, fill=tk.X, expand=True)

        self.shop_var = tk.StringVar()
        self.shop_combo = ttk.Combobox(self.shop_listbox_frame, textvariable=self.shop_var,
                                       font=("微软雅黑", 10), state="readonly",
                                       width=35)
        self.shop_combo.pack(fill=tk.X)
        self.shop_combo.bind("<<ComboboxSelected>>", self._on_shop_select)

        # ── 商店介绍 ──
        self.desc_frame = tk.Frame(main, bg="#2a2a3e", height=28)
        self.desc_frame.pack(fill=tk.X, pady=(0, 6))
        self.desc_frame.pack_propagate(False)
        self.desc_label = tk.Label(self.desc_frame, text="",
                                   font=("微软雅黑", 10), fg="#b0b0b0", bg="#2a2a3e",
                                   anchor=tk.W, justify=tk.LEFT, wraplength=850)
        self.desc_label.pack(fill=tk.BOTH, padx=10, pady=4)

        # ── 分隔线 ──
        ttk.Separator(main, orient=tk.HORIZONTAL).pack(fill=tk.X, pady=4)

        # ── 物品列表 (Canvas + Scrollbar) ──
        list_container = tk.Frame(main, bg="#1a1a2e")
        list_container.pack(fill=tk.BOTH, expand=True)

        self.canvas = tk.Canvas(list_container, bg="#1e1e32", highlightthickness=0)
        scrollbar = ttk.Scrollbar(list_container, orient=tk.VERTICAL, command=self.canvas.yview)
        self.items_frame = tk.Frame(self.canvas, bg="#1e1e32")

        self.items_frame.bind("<Configure>",
                              lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.create_window((0, 0), window=self.items_frame, anchor=tk.NW,
                                  tags="items_window")
        self.canvas.configure(yscrollcommand=scrollbar.set)

        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        # 鼠标滚轮绑定
        self.canvas.bind("<Enter>", lambda e: self._bind_mousewheel())
        self.canvas.bind("<Leave>", lambda e: self._unbind_mousewheel())
        # 窗口 resize 时调整内部 frame 宽度
        self.canvas.bind("<Configure>", self._on_canvas_resize)

        # 初始更新
        self._update_shop_list()
        self._update_items()

    def _bind_mousewheel(self):
        self.canvas.bind_all("<MouseWheel>", self._on_mousewheel)

    def _unbind_mousewheel(self):
        self.canvas.unbind_all("<MouseWheel>")

    def _on_mousewheel(self, event):
        self.canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    def _on_canvas_resize(self, event):
        self.canvas.itemconfig("items_window", width=event.width)

    def _select_hero(self, hero):
        self.selected_hero = hero
        # 更新按钮样式
        for h, btn in self.hero_btns.items():
            if h == hero:
                btn.configure(bg="#606060", relief=tk.SUNKEN)
            else:
                btn.configure(bg="#333333", relief=tk.FLAT)
                if h is None:
                    btn.configure(bg="#555555" if hero is not None else "#606060",
                                  relief=tk.SUNKEN if hero is None else tk.FLAT)
        # 重置按钮样式
        none_btn = self.hero_btns[None]
        if hero is None:
            none_btn.configure(bg="#606060", relief=tk.SUNKEN)
        else:
            none_btn.configure(bg="#555555", relief=tk.FLAT)

        # 更新构建标注
        self.build_annotations = self._get_build_annotations()

        # 更新商店列表（内部会保留当前选中）
        self.selected_merchant = None
        self._update_shop_list()

    def _update_shop_list(self):
        """根据英雄筛选更新商店下拉列表"""
        available = []
        for m in self.merchants:
            merchant_heroes = m.get("heroes", [])
            if self.selected_hero is None:
                available.append(m)
            else:
                cross = m.get("cross_hero", False)
                heroes_lower = [h.lower() for h in merchant_heroes]
                if cross or "common" in heroes_lower or self.selected_hero.lower() in heroes_lower:
                    available.append(m)

        values = []
        self._shop_lookup = {}
        for m in available:
            name_en = m["name"]
            name_zh = tr(name_en, self.translations)
            desc = m.get("desc_zh", "") or m.get("desc", "")
            display = f"{name_zh}  [{desc}]" if desc else name_zh
            values.append(display)
            self._shop_lookup[display] = m

        self.shop_combo["values"] = values
        # 保留之前选中的商店（如果新英雄也有的话）
        old_display = self.shop_var.get()
        if values:
            if old_display in self._shop_lookup:
                self.shop_var.set(old_display)
            else:
                self.shop_var.set(values[0])
            self._on_shop_select()
        else:
            self.shop_var.set('')
            self.selected_merchant = None
            self.desc_label.configure(text='')
            self._update_items()

    def _on_shop_select(self, event=None):
        display = self.shop_var.get()
        if display in self._shop_lookup:
            self.selected_merchant = self._shop_lookup[display]
            # 更新描述
            desc = self.selected_merchant.get("desc_zh", "") or self.selected_merchant.get("desc", "")
            # desc shown in dropdown
        else:
            self.selected_merchant = None
            # desc cleared
        self._update_items()

    def _update_items(self):
        """重建物品列表"""
        for w in self.items_frame.winfo_children():
            w.destroy()

        hero = self.selected_hero
        merchant = self.selected_merchant

        # 更新英雄按钮：当前商店无物品的英雄变暗
        if merchant:
            for h in self.all_heroes:
                if h in self.hero_btns:
                    p = build_shop_pool(h, merchant, self.cards, self.available_tiers)
                    clr = HERO_COLORS.get(h, "#888888") if len(p) > 0 else "#444444"
                    self.hero_btns[h].configure(fg=clr)
        else:
            for h in self.all_heroes:
                if h in self.hero_btns:
                    self.hero_btns[h].configure(fg=HERO_COLORS.get(h, "#888888"))

        if hero is None or merchant is None:
            self.pool = []
            self.total_label.configure(text="总计: 0 件")
            return

        self.pool = build_shop_pool(hero, merchant, self.cards, self.available_tiers)
        self.total_label.configure(text=f"总计: {len(self.pool)} 件")

        if not self.pool:
            tk.Label(self.items_frame, text="(该英雄在此商店无可用物品)",
                     font=("微软雅黑", 11), fg="#666666", bg="#1e1e32").pack(pady=20)
            return

        # 按 size 分组排序
        size_order = {"small": 0, "medium": 1, "large": 2}
        size_labels = {"small": "小型", "medium": "中型", "large": "大型"}
        groups = {"small": [], "medium": [], "large": [], "other": []}

        for card in self.pool:
            sz = card.get("size", "").lower()
            if sz in groups:
                groups[sz].append(card)
            else:
                groups["other"].append(card)

        for g in groups.values():
            g.sort(key=lambda c: lazy_pinyin(tr(c.get("internal_name", ""), self.translations)))

        # 渲染
        for key in ["small", "medium", "large", "other"]:
            items = groups[key]
            if not items:
                continue
            label = size_labels.get(key, key.capitalize())
            # 分组标题
            header = tk.Frame(self.items_frame, bg="#2a2a40")
            header.pack(fill=tk.X, pady=(8, 2) if key != "small" else (2, 2))
            tk.Label(header, text=f"■ {label} ({len(items)}件)",
                     font=("微软雅黑", 11, "bold"), fg="#e0c080", bg="#2a2a40").pack(
                side=tk.LEFT, padx=10, pady=2)

            # 物品网格 — 每行放多个
            row_frame = None
            col = 0
            max_cols = 5
            for i, card in enumerate(items):
                if i % max_cols == 0:
                    row_frame = tk.Frame(self.items_frame, bg="#1e1e32")
                    row_frame.pack(fill=tk.X, padx=10)
                    col = 0

                name_en = card.get("internal_name", "???")
                name_zh = tr(name_en, self.translations)
                display = name_zh if name_zh != name_en else name_en

                lbl = tk.Label(row_frame, text=display, font=("微软雅黑", 10),
                               fg="#d0d0d0", bg="#1e1e32", padx=6, pady=1)
                lbl.pack(side=tk.LEFT)
                col += 1

        self.canvas.yview_moveto(0)


if __name__ == "__main__":
    root = tk.Tk()
    app = ShopBrowser(root)
    root.mainloop()
