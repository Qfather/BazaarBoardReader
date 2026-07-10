# BazaarBoardReader

The Bazaar 游戏的 BepInEx 辅助插件，提供实时棋盘数据导出、阵容推荐与管理系统。

**只读不写，不修改游戏内容。**

---

## 版本历史

### v7.6.3 (2026-07-10)

**面板交互与显示**
- 设置、构筑推荐、构筑管理支持在容器内按住 `Ctrl` 拖动，双击标题栏开合。
- 面板标题后新增 `ctrl:移动 双击:开合` 半透明提示，颜色跟随标题色。
- 固化设置面板默认位置，修复展开时跑出屏幕的问题。
- 物品/技能文本缩小到约 80%，文本黑色背景透明度降低 30%。

**构筑推荐刷新**
- 识别英雄变化时自动清空旧英雄的已选构筑并刷新构筑推荐。
- 构筑推荐在商店/事件没有命中构筑物品时显示 `☆☆☆ 0%`，并整体变暗。
- 修复评分/推荐符号识别，避免无推荐时文本消失或颜色错误。

**事件与升级奖励**
- “获取一件物品”事件支持铜/Bronze 品质，并使用中立 + 当前英雄物品池。
- `(Level Up)` 升级奖励支持“品质 + 标签”物品池，例如黄金级工具、灼烧物品等。
- 升级奖励中的战利品奖励不再显示构筑推荐。

**预览器同步**
- 预览器奖励池同步支持铜/青铜品质。
- 预览器事件奖励过滤同步为当前英雄 + Common 中立物品。

### v7.6.2 (2026-07-08)

**事件与升级奖励推荐**
- 事件推荐改为只在可获取物品的事件上显示，避免斯特银、特殊商店、战利品奖励等无物品池事件误显示推荐
- 新增 `(Level Up)` 升级奖励识别入口，已支持“获得物品/结交朋友”类物品池评分
- 标记人名专属 `(Level Up)` 为后续技能推荐模块处理，避免把学习技能误当作物品推荐
- 标记怒火熔炉后续按灼烧标签物品处理

**拥有物品与背包缓存**
- 拆分棋盘、背包、实时扫描缓存，关闭背包后仍保留上次背包物品数据
- 卖出物品后推荐面板会重新计算拥有状态，避免已出售核心/灵活物品继续半透明
- 打开背包时隐藏事件和商店推荐文本，减少背包界面遮挡

**战斗与技能 Overlay**
- 技能文本只显示玩家侧技能，过滤对手/怪物技能文本
- 修复战斗中玩家技能文本消失、对手技能文本出现的问题

**事件数据与构建部署**
- 特殊商店无专属奖励时不显示推荐，例如口香糖球贩售机
- 新增 The Lost Crate 中型物品事件数据
- build.ps1 改为 dotnet build，并增加 DLL 占用时的重试与提示
### v7.6.1 (2026-07-08)
- RateEvent 格式对齐 RateShop：核心/灵活分行，已拥有 `*` 前缀，`★★★ 45% (5/12)`
- 获取物品只显示英雄专属（排除中立），`!weapon` 排除标签
- 金币回退读取 game_state.json，中文冒号/方括号条件支持
- 事件分类单选，预览器只计总数

### v7.6.0 (2026-07-07)

**五模式预览器**
- 商店/事件/技能/教练/怪物 五模式切换
- 11 事件分类按钮（CSV 定义），默认只激活第一个
- 事件下拉菜单含品质后缀：战利品获取(铜银金钻)、获取物品(银金钻)
- 品质过滤：按事件自身等级而非天数
- 专属奖励系统：12 事件从 `event_rewards.json` 解析，根据描述（等级/尺寸/标签）自动过滤卡牌池
- 备注持久保存到 `event_notes.json`，中文基础名共享
- 物品按拼音排序

**事件推荐系统（C# 插件）**
- 从 `game_state.json` 读取活跃事件，匹配 `event_notes.json` 备注
- 条件解析：金币/生命/收入/天数/声望 比较（`<` `>` `<=` `>=`）
- 支持格式：`金币<10：文本`（中英文冒号、方括号前缀）
- 模板匹配：`template_id` → 事件名映射（`cards_generated.json`）
- 推荐文本金色 `◆` 前缀，显示在事件/商店下方

**数据修复**
- Item 优先：5 张卡（Calico/Apothecary/Gunpowder/Library/FairyCircle）修复怪物覆盖
- 事件 notes 翻译：MD5 哈希匹配 `zh-CN.bytes`，新增 47 条
- 翻译去后缀：`Day 1-2` 格式支持，by_name 增至 2809 条
- `events.json` 按 CSV 分类过滤

**预览器专属奖励文件**
- `event_rewards.json`：CSV 专属奖励 12 事件
- `event_notes.json`：备注持久存储

### v7.5.0 (2026-07-07)

**目录结构重组**
- 删除 `temp_data/`、`BoardData/`，统一为 `data/` 和 `scripts/`
- `data/` — 所有数据文件（卡牌、翻译、运行时导出）
- `scripts/` — 所有 Python 工具
- 根目录仅保留源码、编译脚本和文档

**数据管线重写**
- 新建 `scripts/export_raw_db.py`，从 `GameData.db` 一步生成 `cards.json`、`cards_generated.json`、`translations_zh_cn.json`
- 修复 monster 表空数据覆盖物品数据的问题（Unibou/Wolverine/Wild Boar/Worry Wart 四张 Karnok 伙伴卡恢复）
- 删除旧的 `import_game_data.py`、`export_game_data.py`、`extract_translations.py` 等冗余脚本

**商店推荐增强**
- 结合实时游戏天数与物品/技能等级随天数变化，过滤商店物品和计算获取率
- 商店 Overlay 文本：核心/灵活物品中已拥有的以 `*` 前缀 + 50% 透明度显示，钻石物品不显示
- 构筑推荐面板：当前天数不可用的物品以 30% 灰色显示，保持阵容预设顺序不变
- 商店文本居中，最多 4 个物品超出加 `……`，显示 `(需求/总数)` 测试信息
- 天数未知时隐藏所有面板与标签

**拥有物品检测修复**
- `GatherAllItemsWithTier()` 始终从 `game_state.json` 合并背包物品
- 添加 `_ownedItemsCache` 内存缓存，关背包不丢失
- `MatchBuilds` 改为大小写不敏感比较

**BoardData 合并到 data/**
- 所有运行时生成文件（`game_state.json`、`board_latest.json`、`game_state_zh.json`）统一写入 `data/`
- StateExporter 输出路径、BepInEx 配置同步更新

### v7.4.0 (2026-07-06)
- 集成 StateExporter，数据从游戏缓存生成
- 新增 `cards_generated.json` 含 template_id 映射
- 新增 `translations_zh_cn.json`
- 清理旧数据文件

### v7.3.3 (2026-07-06)
- F5 导出增强：完整游戏数据 + 中文名称映射

### v7.3.2 (2026-07-05)
- 商店逻辑优化、F5 导出增强、独立 GUI 面板、NetMessagePatch 诊断

---

## 功能概览

### 📊 游戏状态实时导出（StateExporter）
- 拦截 `NetMessageGameStateSync` 网络消息，实时读取完整游戏状态
- 自动写入 `data/game_state.json`（每秒更新）
- 包含：英雄、天数、金币、血量、收入、声望、等级
- 包含：面板物品、背包物品、技能（带 template_id、rarity、enchantments）
- 包含：当前事件选项、商店状态

### 📄 中文数据导出
- `scripts/export_zh.py` — `game_state.json` → `game_state_zh.json`（全中文）

### 🔄 数据刷新
- `scripts/export_raw_db.py` — 从 `GameData.db` 一步生成全部数据文件
- 游戏版本更新后运行一次即可

### 📺 实时叠加层（OnGUI）
- **物品标签** — 场上+背包物品，按品质着色
- **技能标签** — 战斗技能+技能选择，品质着色
- **商店标签** — 商店名称中文翻译，显示构筑物品命中率及可升级物品
- **悬停提示** — 鼠标悬停显示完整名称

### 🎯 阵容推荐（F9 面板）
- 基于社区阵容匹配当前棋盘，计算匹配百分比
- 按构筑原始顺序显示核心/灵活物品与技能
- 已拥有=30%透明，可刷=100%，不可刷=30%灰色
- 自动检测当前英雄，支持手动切换

### 🏗️ 阵容管理（F10 面板）
- F8 捕获当前棋盘阵容
- 新建/编辑/删除阵容模板
- 一键导入社区阵容

---

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| **F5** | 导出当前棋盘数据为 JSON |
| **F6** | 一键开关所有面板 |
| **F7** | 开关调试面板 |
| **F8** | 捕获当前棋盘阵容 |
| **F9** | 刷新阵容推荐 |
| **F10** | 开关阵容管理面板 |

---

## 目录结构

```
BazaarBoardReader/
├── BazaarBoardReaderPlugin.cs   # 主插件源码
├── BazaarBoardReader.csproj     # 项目文件
├── build.ps1                    # 编译脚本
├── README.md
├── CLAUDE.md
│
├── StateExporter/               # 游戏状态导出子插件
│   ├── Plugin.cs
│   ├── StateProbe.cs
│   ├── StateSnapshot.cs
│   ├── NetMessagePatches.cs
│   ├── JsonStateWriter.cs
│   ├── RuntimeCardExporter.cs
│   └── BazaarStateExporter.csproj
│
├── scripts/                     # Python 工具
│   ├── export_raw_db.py         # 从 GameData.db 生成全部数据
│   ├── shop_browser.py          # 商店浏览器 (tkinter GUI)
│   ├── export_zh.py             # 中文状态导出
│   ├── export_merchants.py      # 商人数据导出
│   └── rebuild.py               # 重建 C# 插件源码
│
├── data/                        # 数据文件 + 运行时输出
│   ├── cards.json               # 卡牌数据（2958张）
│   ├── cards_generated.json     # 卡牌数据 + template_id
│   ├── translations_zh_cn.json  # 中文翻译（2593条）
│   ├── merchants.json           # 商人配置
│   ├── events.json              # 事件/商店规则
│   ├── shops.json               # 商店数据
│   ├── community_builds.json    # 社区阵容
│   ├── game_data_analysis.json  # 游戏机制分析
│   ├── game_state.json          # 运行时生成（StateExporter）
│   ├── game_state_zh.json       # 运行时生成（export_zh.py）
│   └── board_latest.json        # 运行时生成（F5导出）
│
├── bin/                         # 编译输出（.gitignore）
└── obj/                         # 编译缓存（.gitignore）
```

---

## 数据流

```
游戏运行
  ├── StateExporter 拦截网络消息 → data/game_state.json（每秒更新）
  │     └── scripts/export_zh.py → data/game_state_zh.json（全中文）
  │
  └── 游戏缓存 GameData.db + zh-CN.bytes
        └── scripts/export_raw_db.py → data/cards.json
                                        data/cards_generated.json
                                        data/translations_zh_cn.json
```

---

## 环境要求

- **游戏**: The Bazaar
- **BepInEx**: 5.4.23+
- **编译器**: .NET SDK 8.0+
- **Python**: 3.10+

## 编译 & 安装

```powershell
# 编译
dotnet build BazaarBoardReader.csproj
# DLL 自动生成到 bin/Debug/net472/，复制到 BepInEx/plugins/

# 游戏更新后刷新数据
python scripts/export_raw_db.py
```

## 免责声明

本项目仅供学习研究使用，仅读取游戏内存数据，不修改任何游戏内容。
