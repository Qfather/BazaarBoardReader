# BazaarBoardReader

The Bazaar 游戏的 BepInEx 内存读取插件，实时显示棋盘物品/技能/商店名称叠加层，并支持 JSON 数据导出。

**只读不写，不修改游戏内容。**

## 功能

| 快捷键 | 功能 |
|--------|------|
| **F5** | 导出当前棋盘数据为 JSON（物品 + 技能 + 商店） |
| **F6** | 手动开关实时叠加层（OnGUI） |
| **F7** | 开关滑块调试面板 |

### 叠加层显示

- **物品标签** — 品质颜色（铜白/银蓝/金金/钻石粉/传说红）
- **技能标签** — 战斗技能 + 技能选择，品质颜色
- **商店标签** — 绿色文字，基于 EncounterController 识别
- 黑色圆角半透明底色 + 黑色描边
- 可调节偏移量和背景透明度

### 滑块面板（F7）

- 物品偏移（0-300px）
- 技能偏移（0-300px）
- 商店偏移（0-300px）
- 背景透明度（10%-95%）
- 保存 / 重置按钮，配置持久化

### JSON 导出

导出到 `BoardData/board_YYYYMMDD_HHmmss.json`，包含：
- `BoardItems` — 棋盘上的物品
- `StorageItems` — 仓库中的物品
- `SkillCards` — 技能卡片
- `Shops` — 商店信息（GameObject名 + 坐标）

## 环境要求

- **游戏**: The Bazaar (Unity 6000.3.11, Mono)
- **BepInEx**: 5.4.23.5
- **编译器**: .NET Framework 4.x (C# 5)
- **依赖**: Newtonsoft.Json (随游戏附带)

## 编译

```powershell
.\build.ps1
```

编译产物自动复制到 `BepInEx/plugins/`。

## 安装

1. 确保已安装 BepInEx 5.4.23.5
2. 将 `BazaarBoardReader.dll` 放入 `BepInEx/plugins/`
3. 启动游戏

## 目录结构

```
BazaarBoardReader/
├── BazaarBoardReaderPlugin.cs  # 主插件源码
├── build.ps1                   # 编译脚本
├── .gitignore
└── README.md
```

## 免责声明

本项目仅供学习研究使用，仅读取游戏内存数据，不修改任何游戏内容。
