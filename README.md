# BazaarBoardReader

The Bazaar 游戏的 BepInEx 内存读取辅助插件，提供实时棋盘叠加层、阵容推荐与管理系统。

**只读不写，不修改游戏内容。**

---

## 功能概览

### 📺 实时叠加层（OnGUI）

- **物品标签** — 场上+背包物品，按品质着色（铜/银/金/钻石/传说）
- **技能标签** — 战斗技能+技能选择，品质着色
- **商店标签** — 基于 `EncounterController` 识别商店名称，中文翻译
- **悬停提示** — 鼠标悬停显示完整名称（英文+中文）
- 黑色圆角半透明底色，可调偏移量

### 🎯 阵容推荐（F9 面板）

- 基于社区十胜阵容（`community_builds.json`）匹配当前棋盘
- **70% 物品权重 + 30% 技能权重** 计算匹配百分比
- 绿色进度条直观显示匹配度
- 分**核心/灵活**物品与技能，缺啥一目了然
- 商店有售缺失物品时 **★ 星标闪烁提示**
- 按英雄筛选，英雄按钮**颜色编码**（VAN红/PYG蓝/DOO橙/MAK绿/STE黄/JUL紫/KAR青）
- 英雄选择持久化保存

### 🏗️ 阵容管理（F10 面板）

- **F8 捕获**当前棋盘阵容（物品+技能）
- **新建/编辑/删除**阵容模板
- 物品/技能分**核心/灵活**，一键升降级（↓↑按钮）
- **导入社区阵容**一键批量导入
- 全部数据原子写入 `BepInEx/config/BazaarBoardReader_Builds.json`

### 🎛️ 调试面板（F7）

- 物品/技能/商店 Y 偏移滑块（0-300px）
- 背景透明度（10%-95%）
- 面板透明度（20%-95%）
- 推荐面板/管理面板位置滑块（X/Y）
- **中/EN 实时语言切换**（无需重启）
- 保存/重置按钮，配置持久化到 BepInEx config

### 🌐 中文翻译

- 优先从游戏自带 `zh-CN.bytes` SQLite 数据库直读
- 回退到本地 `translations_zh_cn.json`（4662条）
- 自动处理 `[前缀]` 和 `(Gold)` 等后缀
- F7 面板一键切换中/英文显示

---

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| **F5** | 导出当前棋盘数据为 JSON |
| **F6** | 一键开关所有面板（叠加层+推荐+管理+调试） |
| **F7** | 开关调试面板（滑块/设置） |
| **F8** | 捕获当前棋盘阵容 |
| **F9** | 刷新阵容推荐 |
| **F10** | 开关阵容管理面板 |

---

## 技术实现

| 技术 | 说明 |
|------|------|
| **Harmony Patch** | `CardController.SetCardData` 钩子，事件驱动卡牌追踪 |
| **反射** | 读取 `BoardManager._playerCardsOnBoard`、`SkillPresentationManager._skillList` |
| **SQLite 直读** | `Mono.Data.Sqlite` 读取游戏缓存翻译数据库 |
| **原子写入** | JSON 数据先写 `.tmp` 再 `File.Move`，防止断电损坏 |
| **GPU 优化** | 缓存单张 `WhiteTex` + `GUI.color` 替代每帧 `Texture2D.Apply()` |
| **圆角矩形** | 逐列竖条逼近圆角，无需运行时纹理生成 |

---

## 目录结构

```
BazaarBoardReader/
├── BazaarBoardReaderPlugin.cs   # 主插件源码（~1500行）
├── build.ps1                    # 编译脚本（.NET Framework 4.x csc.exe）
├── translations_zh_cn.json      # 本地翻译回退（4662条）
├── update_translations.ps1      # 翻译更新脚本
├── .gitignore
├── README.md
└── data/                        # 静态数据（从 bazaar-helper 导入）
    ├── cards_generated.json      # 1783 张卡牌
    ├── skills_generated.json     # 388 个技能
    ├── events.json               # 319 条商店卡池规则
    ├── community_builds.json     # 社区十胜阵容
    └── encounters_generated.json # 1095 条遭遇数据
```

---

## 环境要求

- **游戏**: The Bazaar (Unity 6000.3.11, IL2CPP/Mono)
- **BepInEx**: 5.4.23.5
- **编译器**: .NET Framework 4.x（C# 5）
- **依赖**: HarmonyLib, Newtonsoft.Json, Mono.Data.Sqlite（均随游戏/BepInEx 附带）

## 编译

```powershell
.\build.ps1
```

编译产物自动复制到 `BepInEx/plugins/`。

## 安装

1. 确保已安装 BepInEx 5.4.23.5
2. 将 `BazaarBoardReader.dll` 放入 `BepInEx/plugins/`
3. 将 `data/` 文件夹复制到 `BepInEx/plugins/data/`（或游戏根目录 `BazaarBoardReader/data/`）
4. 启动游戏 → 按 F6 开启面板

## 免责声明

本项目仅供学习研究使用，仅读取游戏内存数据，不修改任何游戏内容。
