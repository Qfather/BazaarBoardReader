# BazaarBoardReader

The Bazaar 游戏的 BepInEx 辅助插件，提供实时棋盘数据导出、阵容推荐与管理系统。

**只读不写，不修改游戏内容。**

---

## 功能概览

### 📊 游戏状态实时导出（StateExporter）

- 拦截 `NetMessageGameStateSync` 网络消息，实时读取完整游戏状态
- 自动写入 `BoardData/game_state.json`（每秒更新）
- 包含：英雄、天数、金币、血量、收入、声望、等级
- 包含：面板物品、背包物品、技能（带 template_id、rarity、enchantments）
- 包含：当前事件选项、商店状态

### 📄 中文数据导出（export_zh.py）

- 读取 `game_state.json` → 输出 `game_state_zh.json`
- 所有物品、技能、事件自动翻译为中文名
- 格式化为易读的结构化 JSON

### 🔄 数据刷新（import_game_data.py）

- 从游戏本地缓存 `GameData.db` + `zh-CN.bytes` 直接提取最新数据
- 生成 `cards_generated.json`（卡牌数据库）
- 生成 `cards.json`（shop_browser 兼容格式）
- 生成 `translations_zh_cn.json`（中文翻译映射）
- 游戏版本更新后运行一次即可

### 📺 实时叠加层（OnGUI）

- **物品标签** — 场上+背包物品，按品质着色（铜/银/金/钻石/传说）
- **技能标签** — 战斗技能+技能选择，品质着色
- **商店标签** — 基于 `EncounterController` 识别商店名称，中文翻译
- **悬停提示** — 鼠标悬停显示完整名称

### 🎯 阵容推荐（F9 面板）

- 基于社区阵容（`community_builds.json`）匹配当前棋盘
- 70% 物品权重 + 30% 技能权重计算匹配百分比
- 分核心/灵活物品与技能
- 按英雄筛选，英雄按钮颜色编码

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
├── BazaarBoardReaderPlugin.cs   # 主插件源码（实时叠加层+阵容推荐）
├── BazaarBoardReader.csproj     # 主插件项目文件
├── build.ps1                    # 编译脚本（csc.exe 回退方案）
│
├── StateExporter/               # 游戏状态实时导出插件
│   ├── Plugin.cs                # 入口，配置输出路径和轮询
│   ├── StateProbe.cs            # 核心：从 DTO 读取 Hero/Day/Gold...
│   ├── StateSnapshot.cs         # 数据模型（GameStateSnapshot）
│   ├── NetMessagePatches.cs     # Harmony 补丁拦截 NetMessageGameStateSync
│   ├── JsonStateWriter.cs       # 原子 JSON 写入
│   ├── RuntimeCardExporter.cs   # 卡牌运行时扫描
│   └── BazaarStateExporter.csproj
│
├── import_game_data.py          # 从 GameData.db 刷新全部数据文件
├── export_zh.py                 # game_state.json → game_state_zh.json
├── test_hotkey.py               # F8 热键实时显示游戏状态
│
├── data/                        # 数据文件（import_game_data.py 生成）
│   ├── cards_generated.json     # 卡牌数据库（~3000 张）
│   ├── cards.json               # shop_browser 兼容格式
│   ├── translations_zh_cn.json  # 中文翻译（by_name + by_id）
│   ├── events.json              # 事件/商店规则配置
│   ├── community_builds.json    # 社区阵容配置
│   ├── merchants.json           # 商人数据
│   └── shops.json               # 商店数据
│
└── BoardData/                   # 运行时生成（游戏运行后自动产生）
    ├── game_state.json          # StateExporter 实时导出
    ├── game_state_zh.json       # export_zh.py 中文版
    └── board_latest.json        # F5 手动导出（BazaarBoardReader）
```

---

## 数据流

```
游戏运行
  ├── StateExporter 拦截网络消息 → BoardData/game_state.json（实时，每秒更新）
  │     └── export_zh.py → BoardData/game_state_zh.json（全中文）
  │
  └── 游戏缓存 GameData.db + zh-CN.bytes
        └── import_game_data.py → data/cards_generated.json
                                   data/cards.json
                                   data/translations_zh_cn.json
```

---

## 环境要求

- **游戏**: The Bazaar
- **BepInEx**: 5.4.23+
- **编译器**: .NET SDK 8.0+（`dotnet build`）
- **Python**: 3.10+（运行数据脚本）

## 编译

```powershell
# StateExporter（游戏状态导出）
dotnet build StateExporter/BazaarStateExporter.csproj -p:GameRoot="F:\SteamLibrary\steamapps\common\The Bazaar"

# 主插件（可选，功能面板）
dotnet build BazaarBoardReader.csproj

# 或使用旧版 csc.exe
.\build.ps1
```

编译产物复制到 `BepInEx/plugins/`。

## 安装

1. 确保已安装 BepInEx
2. 编译 `StateExporter`，DLL 放入 `BepInEx/plugins/BazaarStateExporter/`
3. 首次运行后检查配置 `BepInEx/config/local.bazaar.stateexporter.cfg`
4. 运行 `py import_game_data.py` 生成数据文件
5. 启动游戏，进入对局即可看到 `BoardData/game_state.json`

## 免责声明

本项目仅供学习研究使用，仅读取游戏内存数据，不修改任何游戏内容。
