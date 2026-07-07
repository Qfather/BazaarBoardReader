# BazaarBoardReader - 项目说明

## 项目概述

The Bazaar 游戏的游戏数据导出/导入/翻译工具，用于从游戏缓存中的 `GameData.db` SQLite 数据库提取卡牌、商店、遭遇等数据。

## 游戏数据库位置

```
%LOCALAPPDATA%\..\LocalLow\Tempo Storm\The Bazaar\prod\cache\GameData.db
```

实际路径：`C:\Users\<用户名>\AppData\LocalLow\Tempo Storm\The Bazaar\prod\cache\GameData.db`

数据库大小约 37MB，SQLite 格式。所有表结构为 `Id` + `Data`(JSON BLOB)。

### 数据库表

| 表名 | 条目数 | 说明 |
|---|---|---|
| `cards` | 3062 | 所有卡牌（物品、技能、遭遇事件、遭遇步骤、战斗等） |
| `challenges` | 80 | 每日/每周挑战 |
| `game_modes` | 1 | 游戏模式全局配置 |
| `level_ups` | 30 | 等级提升奖励配置 |
| `monsters` | 171 | 怪物/PvE 敌人数据 |
| `seasons` | 17 | 赛季数据 |
| `collectibles` | - | 收藏品 |
| `tooltips` | - | 提示文本 |

## 卡牌类型 (`$.$type`)

| 类型 | 数量 | 说明 |
|---|---|---|
| `TCardItem` | 1272 | 物品卡 |
| `TCardSkill` | 516 | 技能卡 |
| `TCardEncounterEvent` | 527 | 遭遇事件（商店/PvE选择节点） |
| `TCardEncounterStep` | 529 | 遭遇步骤（多步遭遇的子步骤） |
| `TCardEncounterCombat` | 175 | 战斗遭遇（PvE怪物战） |
| `TCardEncounterPedestal` | 32 | 底座遭遇（附魔等特殊效果） |
| `TCardPlayerEffect` | 9 | 玩家效果 |
| `TCardSocketEffect` | 2 | 插槽效果 |

## 商店/遭遇出现逻辑 ⭐

### 出现方式 (`SpawningEligibility`)

| 类型 | 数量 | 机制 |
|---|---|---|
| `Always` | 97 | 始终在候选池中，每小时随机抽取 |
| `GuidOnly` | 348 | 只能被直接 GUID 引用（教程链、升级奖励等） |
| `Never` | 82 | 禁用/调试事件，不会自然出现 |
| 概率生成 | 4 | 基于 `SpawningChance` 随机出现 |

### 遭遇等级权重 (`EncounterSpawnTierPercentages`)

- Bronze: 40% | Silver: 30% | Gold: 20% | Diamond: 10%

### 物品/技能等级随天数变化 (`ItemSkillSpawnTierPercantagesByDay`)

| 天 | Bronze | Silver | Gold | Diamond |
|---|---|---|---|---|
| 1 | 100% | 0% | 0% | 0% |
| 2 | 90% | 10% | 0% | 0% |
| 3 | 70% | 30% | 0% | 0% |
| 4 | 50% | 50% | 0% | 0% |
| 5 | 25% | 75% | 0% | 0% |
| 6 | 0% | 95% | 5% | 0% |
| 7 | 0% | 80% | 20% | 0% |
| 8 | 0% | 45% | 50% | 5% |
| 9 | 0% | 35% | 55% | 10% |
| 10 | 0% | 20% | 65% | 15% |

### 遭遇事件结构

每个 `TCardEncounterEvent` 包含：
- `SelectionContext` → `SpawnContext` (TSpawnContextQuery)
  - `Groups[]` → `TSpawnGroup`
    - `Filters` → `TSpawnFilterIdList` (物品 GUID 列表)
    - `SelectionMethod`: `Random` / `Sequential`
    - `Limit`: 展示物品数量
    - `RandomWeight`: 该组权重
    - `Behaviors`: `TSpawnBehaviorTier`(强制等级) / `TSpawnBehaviorIgnoreHero` / `TSpawnBehaviorIgnoreTierTable`
  - `Behaviors`: 全局等级/英雄行为覆盖
- `Rules`: `CanSelectMultiple`, `SelectionIsFree`, `CanExit`, `RerollRules`

### 英雄列表

Common, Dooley, Jules, Karnok, Mak, Pygmalien, Stelle, Vanessa

### 价格体系 (`StandardPrices`)

买入价（卖出价为一半）：

| 尺寸 | Bronze | Silver | Gold | Diamond | Legendary |
|---|---|---|---|---|---|
| Small | 2 | 4 | 8 | 16 | 24 |
| Medium | 4 | 8 | 16 | 32 | 48 |
| Large | 6 | 12 | 24 | 48 | 64 |
| Skill | 5 | 10 | 20 | 40 | 50 |

### 游戏基本参数

- 10 天，每天 6 小时
- 初始 Prestige 20，10 胜获胜
- 每级 8 XP，每小时 1 XP
- 暴击倍率 200%

## 主要商人一览

### 通用商人 (Common)

| 商人 | 等级 | 特色 |
|---|---|---|
| Jay Jay, Valpak | Bronze | 通用物品 |
| Aila | Bronze | 武器 (Weapon) |
| Kina | Bronze | 非武器 |
| Ande | Bronze | 小型物品 |
| Mittel | Bronze | 中型物品 |
| Midsworth, Barkun | Silver | 组合尺寸 |
| Quixel | Silver | 小型+中型 |
| Curio | Silver | 中立物品 |
| Silvia | Silver | 白银级 |
| Orion | Silver | 工具 (Tools) |
| Kev's Armory | Silver | 护盾/生命 |
| Tok's Clocks | Silver | 加速/减速/冷却 |
| Goldie | Gold | 黄金级 |
| Pinfeather | Gold | 飞行 (Flying) |
| Gaseo | Gold | 服饰 (Apparel) |
| Aimbot | Gold | 暴击 (Crit) |
| The Antiquarian | Gold | 遗物 (Relic) |
| Tatiana | Gold | 玩具 (Toys) |
| Private Pitchfork | Gold | 中立物品 |
| Luxe | Diamond | 钻石级 |
| Serafina | Diamond | 附魔物品 |
| Knightshade | Diamond | 剧毒 (Poison) |
| Chronos | Diamond | 加速 (Haste) |
| Hef | Diamond | 灼烧 (Burn) |
| Cobweb | Diamond | 减速 (Slow) |
| Freiya | Diamond | 冻结 (Freeze) |

### 专属商人

| 商人 | 英雄 | 特色 |
|---|---|---|
| Nautica | Vanessa | 水系 (Aquatic) |
| Colt | Vanessa | 弹药 (Ammo) |
| Shelter Shelby | Vanessa/Karnok | 伙伴 (Friend) |
| Prospero | Pygmalien | 经济 (Economic) |
| Mr. Morland | Pygmalien | 地产 (Property) |
| Flex | Pygmalien/Karnok | 最大生命 (MaxHealth) |
| Tinker | Dooley | 伙伴 (Friend) |
| The Tester | Dooley/Stelle | 科技 (Tech) |
| Aero | Stelle | 载具/无人机 |
| Eli | Mak | 药水 (Potion) |
| Gastro | Jules | 食物 (Food) |

### 英雄商人 (Diamond, 跨英雄)

Karnok, Mak, Dooley, Vanessa, Pygmalien, Jules, Stelle — 各自的英雄商人出售该英雄主题物品给其他英雄。

## 目录结构

```
BazaarBoardReader/
├── BazaarBoardReader.csproj   # C# 插件项目
├── BazaarBoardReaderPlugin.cs # 主插件代码
├── build.ps1                  # 编译脚本
├── StateExporter/             # BepInEx 子插件（游戏状态导出）
├── scripts/                   # Python 工具
│   ├── export_raw_db.py       # 从 GameData.db 生成 data/ 下全部数据文件
│   ├── shop_browser.py        # 商店浏览器 (tkinter GUI)
│   ├── export_zh.py           # 中文游戏状态导出
│   ├── export_merchants.py    # 导出商人数据
│   └── rebuild.py             # 重建 C# 插件（从 .bak）
├── data/
│   ├── cards.json             # 卡牌数据（C# 插件+shop_browser 用）
│   ├── cards_generated.json   # 卡牌+template_id
│   ├── translations_zh_cn.json
│   ├── merchants.json
│   ├── events.json
│   ├── shops.json
│   ├── community_builds.json
│   └── game_data_analysis.json
├── BoardData/                 # 运行时生成（游戏运行后自动产生）
└── bin/, obj/                 # 编译输出
```

## 数据更新

游戏更新后只需运行一次：
```bash
python scripts/export_raw_db.py
```
直接从 GameData.db 生成 `data/cards.json`、`data/cards_generated.json`、`data/translations_zh_cn.json`。

### 翻译来源

所有中文翻译来自游戏内置的 `zh-CN.bytes`（SQLite 数据库）：
- 卡牌/事件名：通过 `Localization.Title.Key` 匹配 `hash → text`
- 事件描述 notes：通过 MD5(英文原文) 匹配
- 翻译文件路径：`%LOCALAPPDATA%\..\LocalLow\Tempo Storm\The Bazaar\prod\cache\translations\zh-CN.bytes`
- 输出到：**[data/translations_zh_cn.json](data/translations_zh_cn.json)**

## 详细分析数据

完整的游戏数据分析（含所有遭遇事件、商人、物品池等）见：
**[game_data_analysis.json](game_data_analysis.json)**

该文件包含：
- 所有 97 个 Always 事件的详细信息
- 所有商人的英雄/等级/标签
- 物品等级随天数的完整分布
- 价格体系
- 所有怪物列表
- 游戏参数配置
