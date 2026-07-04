using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace BazaarBoardReader
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BazaarBoardReaderPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.bazaar.boardreader";
        private const string PluginName = "BazaarBoardReader";
        private const string PluginVersion = "6.0.0";

        private ManualLogSource _logger;
        private float _lastExportTime;
        private const float CooldownSeconds = 2f;

        // 反射
        private static FieldInfo _playerCardsOnBoardField;
        private static FieldInfo _skillListField;
        private static PropertyInfo _isPlayerBoardProp;

        // 输入
        private static bool _inputChecked;
        private static bool _useNewInput;

        // GUI 开关
        private bool _overlayEnabled;
        private bool _showSliders;
        private bool _showRecommendations;
        private bool _showBuildManager;

        // 偏移/样式
        private float _itemOffsetY = 30f;
        private float _skillOffsetY = 20f;
        private float _shopOffsetY = 80f;
        private float _bgOpacity = 0.55f;
        private readonly List<OverlayLabel> _itemLabels = new List<OverlayLabel>();
        private readonly List<OverlayLabel> _skillLabels = new List<OverlayLabel>();
        private readonly List<OverlayLabel> _shopLabels = new List<OverlayLabel>();
        private GUIStyle _labelStyle;
        private GUIStyle _shopStyle;
        private readonly Dictionary<int, Texture2D> _bgCache = new Dictionary<int, Texture2D>();

        // 品质颜色
        private readonly Dictionary<string, Color> _tierColors = new Dictionary<string, Color>
        {
            { "Bronze",   new Color(1f, 0.95f, 0.7f) },
            { "Silver",   new Color(0.3f, 0.75f, 1f) },
            { "Gold",     new Color(1f, 0.8f, 0.05f) },
            { "Diamond",  new Color(1f, 0.5f, 0.7f) },
            { "Legendary",new Color(1f, 0.15f, 0.1f) },
            { "Invalid",  new Color(0.7f, 0.7f, 0.7f) },
        };

        // ==================== 阵容系统 ====================
        private List<BuildTemplate> _builds = new List<BuildTemplate>();
        private List<BuildMatchResult> _matchResults = new List<BuildMatchResult>();
        private string _detectedHero = "";
        private string _buildsPath;
        private Vector2 _buildListScroll;
        private Vector2 _recScroll;
        private string _captureBuildName = "";
        private bool _captureMode;
        private string _newItemInput = "";
        private string _newBuildNameInput = "";
        private string _newBuildHeroInput = "";
        private string _selectedBuildForEdit = "";

        // Config
        private const string CfgSec = "Offsets";
        private const string CfgItem = "ItemOffsetY";
        private const string CfgSkill = "SkillOffsetY";
        private const string CfgShop = "ShopOffsetY";
        private const string CfgBg = "BgOpacity";
        private const string CfgOverlay = "OverlayEnabled";
        private const string CfgSecGeneral = "General";

        private void Awake()
        {
            _logger = Logger;
            _itemOffsetY = Config.Bind(CfgSec, CfgItem, 30f).Value;
            _skillOffsetY = Config.Bind(CfgSec, CfgSkill, 20f).Value;
            _shopOffsetY = Config.Bind(CfgSec, CfgShop, 80f).Value;
            _bgOpacity = Config.Bind(CfgSec, CfgBg, 0.55f).Value;
            _overlayEnabled = Config.Bind(CfgSecGeneral, CfgOverlay, true, "启动时自动开启叠加层").Value;
            _logger.LogInfo(string.Format("[BoardReader] v6.0 物品={0} 技能={1} 商店={2} 背景={3:F0}% 叠加={4}",
                (int)_itemOffsetY, (int)_skillOffsetY, (int)_shopOffsetY, _bgOpacity * 100f, _overlayEnabled));

            _labelStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
            _shopStyle = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };

            _playerCardsOnBoardField = typeof(BoardManager).GetField("_playerCardsOnBoard", BindingFlags.Instance | BindingFlags.NonPublic);
            _isPlayerBoardProp = typeof(CardController).GetProperty("IsPlayerBoard", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _skillListField = typeof(TheBazaar.SkillPresentationManager).GetField("_skillList", BindingFlags.Instance | BindingFlags.NonPublic);

            // 阵容数据路径
            _buildsPath = Path.Combine(Paths.ConfigPath, "BazaarBoardReader_Builds.json");
            LoadBuilds();
        }

        // ==================== 输入 ====================

        private bool IsKeyPressed(string name)
        {
            if (!_inputChecked)
            {
                _inputChecked = true;
                try
                {
                    var kt = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    if (kt != null && kt.GetProperty("current").GetValue(null, null) != null)
                        _useNewInput = true;
                }
                catch { }
            }
            if (_useNewInput)
            {
                try
                {
                    var kt = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    var kb = kt.GetProperty("current").GetValue(null, null);
                    if (kb != null)
                    {
                        var kp = kt.GetProperty(name + "Key");
                        if (kp != null)
                        {
                            var key = kp.GetValue(kb, null);
                            if (key != null) return (bool)key.GetType().GetProperty("wasPressedThisFrame").GetValue(key, null);
                        }
                    }
                }
                catch { }
            }
            try
            {
                if (name == "f5") return Input.GetKeyDown(KeyCode.F5);
                if (name == "f6") return Input.GetKeyDown(KeyCode.F6);
                if (name == "f7") return Input.GetKeyDown(KeyCode.F7);
                if (name == "f8") return Input.GetKeyDown(KeyCode.F8);
                if (name == "f9") return Input.GetKeyDown(KeyCode.F9);
                if (name == "f10") return Input.GetKeyDown(KeyCode.F10);
            }
            catch { }
            return false;
        }

        // ==================== Update ====================

        private void Update()
        {
            if (IsKeyPressed("f7")) { _showSliders = !_showSliders; }
            if (IsKeyPressed("f6"))
            {
                _overlayEnabled = !_overlayEnabled;
                Config[CfgSecGeneral, CfgOverlay].BoxedValue = _overlayEnabled;
                Config.Save();
            }
            if (IsKeyPressed("f9")) { _showRecommendations = !_showRecommendations; }
            if (IsKeyPressed("f10")) { _showBuildManager = !_showBuildManager; _captureMode = false; }

            if (IsKeyPressed("f5"))
            {
                if (Time.time - _lastExportTime < CooldownSeconds) return;
                _lastExportTime = Time.time;
                try { ExportToJson(GatherBoardData()); }
                catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 导出失败: {0}", ex)); }
            }

            // F8: 捕获当前阵容
            if (IsKeyPressed("f8"))
            {
                _captureMode = true;
                _captureBuildName = "";
                _captureHeroInput = "";
                var items = GatherCurrentBoardItemNames();
                _logger.LogInfo(string.Format("[BoardReader] F8 捕获阵容: {0} 个物品", items.Count));
            }

            if (_overlayEnabled && Time.frameCount % 60 == 0)
            {
                RefreshOverlayLabels();
                // 每60帧检测一次英雄
                if (string.IsNullOrEmpty(_detectedHero))
                    _detectedHero = DetectHero();
            }
        }

        // ==================== GUI ====================

        private void OnGUI()
        {
            if (_overlayEnabled)
            {
                var cam = Camera.main ?? Camera.current;
                if (cam != null)
                {
                    foreach (var lbl in _itemLabels)
                    {
                        if (lbl.ScreenPos.z <= 0) continue;
                        DrawLabel(lbl, false);
                    }
                    foreach (var lbl in _skillLabels)
                    {
                        if (lbl.ScreenPos.z <= 0) continue;
                        DrawLabel(lbl, false);
                    }
                    foreach (var lbl in _shopLabels)
                    {
                        if (lbl.ScreenPos.z <= 0) continue;
                        DrawLabel(lbl, true);
                    }
                }
            }

            // F7 滑块面板
            if (_showSliders) DrawSlidersPanel();

            // F8 捕获确认
            if (_captureMode) DrawCaptureDialog();

            // F9 推荐面板
            if (_showRecommendations) DrawRecommendationPanel();

            // F10 阵容管理面板
            if (_showBuildManager) DrawBuildManagerPanel();
        }

        // ==================== 标签绘制（已有） ====================

        private void DrawLabel(OverlayLabel lbl, bool isShop)
        {
            var style = isShop ? _shopStyle : _labelStyle;
            var sz = style.CalcSize(new GUIContent(lbl.Text));
            var w = (int)(sz.x + 16);
            var h = (int)(sz.y + 6);
            var r = new Rect(lbl.ScreenPos.x - w / 2, Screen.height - lbl.ScreenPos.y, w, h);

            var bg = GetRoundedBg(w, h);
            GUI.DrawTexture(r, bg);

            var os = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, 0.9f) } };
            var tr = new Rect(r.x + 8, r.y + 3, sz.x, sz.y);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(tr.x + dx, tr.y + dy, sz.x, sz.y), lbl.Text, os);

            style.normal.textColor = isShop ? new Color(0.2f, 1f, 0.3f) : Color.white;
            if (!isShop && _tierColors.ContainsKey(lbl.Tier))
                style.normal.textColor = _tierColors[lbl.Tier];

            GUI.Label(tr, lbl.Text, style);
        }

        private Texture2D GetRoundedBg(int w, int h)
        {
            var key = (w << 16) | h;
            Texture2D tex;
            if (_bgCache.TryGetValue(key, out tex)) return tex;

            tex = new Texture2D(w, h);
            var r = Mathf.Min(6, w / 4, h / 4);
            var bg = new Color(0, 0, 0, _bgOpacity);
            var tr = Color.clear;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    var corner = false;
                    var cx = x < r ? r - x : (x >= w - r ? x - (w - r - 1) : 0);
                    var cy = y < r ? r - y : (y >= h - r ? y - (h - r - 1) : 0);
                    if (cx > 0 && cy > 0 && cx * cx + cy * cy > r * r)
                        corner = true;
                    tex.SetPixel(x, y, corner ? tr : bg);
                }
            tex.Apply();
            _bgCache[key] = tex;
            return tex;
        }

        private Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t;
        }

        private void DrawSlidersPanel()
        {
            var px = 10f;
            var py = Screen.height * 0.5f - 130f;
            var pw = 270f;
            var ph = 210f;
            GUI.DrawTexture(new Rect(px, py, pw, ph), Tex(new Color(0, 0, 0, 0.8f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 13 };
            s.normal.textColor = Color.white;

            GUI.Label(new Rect(px + 12, py + 8, 246, 18), string.Format("物品偏移: {0}px", (int)_itemOffsetY), s);
            _itemOffsetY = GUI.HorizontalSlider(new Rect(px + 12, py + 26, 246, 14), _itemOffsetY, 0f, 300f);

            GUI.Label(new Rect(px + 12, py + 44, 246, 18), string.Format("技能偏移: {0}px", (int)_skillOffsetY), s);
            _skillOffsetY = GUI.HorizontalSlider(new Rect(px + 12, py + 62, 246, 14), _skillOffsetY, 0f, 300f);

            GUI.Label(new Rect(px + 12, py + 80, 246, 18), string.Format("商店偏移: {0}px", (int)_shopOffsetY), s);
            _shopOffsetY = GUI.HorizontalSlider(new Rect(px + 12, py + 98, 246, 14), _shopOffsetY, 0f, 300f);

            GUI.Label(new Rect(px + 12, py + 116, 246, 18), string.Format("背景透明度: {0:F0}%", _bgOpacity * 100f), s);
            _bgOpacity = GUI.HorizontalSlider(new Rect(px + 12, py + 134, 246, 14), _bgOpacity, 0.1f, 0.95f);

            var bs = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(px + 20, py + 158, 85, 28), "保存", bs))
            {
                Config[CfgSec, CfgItem].BoxedValue = _itemOffsetY;
                Config[CfgSec, CfgSkill].BoxedValue = _skillOffsetY;
                Config[CfgSec, CfgShop].BoxedValue = _shopOffsetY;
                Config[CfgSec, CfgBg].BoxedValue = _bgOpacity;
                Config.Save();
            }
            if (GUI.Button(new Rect(px + 120, py + 158, 85, 28), "重置", bs))
            {
                _itemOffsetY = 30f; _skillOffsetY = 20f; _shopOffsetY = 80f; _bgOpacity = 0.55f;
            }
        }

        // ==================== F8 捕获对话框 ====================

        private string _captureHeroInput = "";

        private void CaptureCurrentBoardAsBuild()
        {
            var items = GatherCurrentBoardItemNames();
            if (items.Count == 0) return;
            var hero = string.IsNullOrEmpty(_captureHeroInput) ? _detectedHero : _captureHeroInput;
            var build = new BuildTemplate
            {
                HeroName = hero,
                BuildName = _captureBuildName,
                CoreItems = items,
                FlexItems = new List<string>()
            };
            _builds.Add(build);
            SaveBuilds();
            _logger.LogInfo(string.Format("[BoardReader] 已保存阵容: {0} 英雄={1} 物品={2}",
                _captureBuildName, hero, string.Join(", ", items.ToArray())));
        }

        private void DrawCaptureDialog()
        {
            var w = 330f;
            var h = 200f;
            var x = (Screen.width - w) / 2;
            var y = (Screen.height - h) / 2;
            GUI.DrawTexture(new Rect(x, y, w, h), Tex(new Color(0.1f, 0.1f, 0.15f, 0.95f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 14 };
            s.normal.textColor = Color.white;

            GUI.Label(new Rect(x + 15, y + 10, 300, 22), "捕获当前棋盘阵容", s);

            var boardItems = GatherCurrentBoardItemNames();
            s.fontSize = 12;
            GUI.Label(new Rect(x + 15, y + 35, 300, 40),
                string.Format("当前棋盘物品 ({0}):\n{1}", boardItems.Count, string.Join(", ", boardItems.ToArray())), s);

            s.fontSize = 13;
            GUI.Label(new Rect(x + 15, y + 85, 60, 22), "阵容名:", s);
            _captureBuildName = GUI.TextField(new Rect(x + 80, y + 85, 160, 22), _captureBuildName ?? "", 30);

            GUI.Label(new Rect(x + 15, y + 115, 60, 22), "英雄:", s);
            var heroDisplay = string.IsNullOrEmpty(_detectedHero) ? "手动输入" : _detectedHero;
            var heroPlaceholder = string.IsNullOrEmpty(_captureHeroInput) ? heroDisplay : _captureHeroInput;
            _captureHeroInput = GUI.TextField(new Rect(x + 80, y + 115, 160, 22), heroPlaceholder, 30);

            var bs = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(x + 20, y + 150, 90, 28), "保存", bs))
            {
                if (!string.IsNullOrEmpty(_captureBuildName))
                {
                    CaptureCurrentBoardAsBuild();
                    _captureMode = false;
                }
            }
            if (GUI.Button(new Rect(x + 120, y + 150, 90, 28), "取消", bs))
            {
                _captureMode = false;
            }
            if (GUI.Button(new Rect(x + 220, y + 150, 90, 28), "保存+管理", bs))
            {
                if (!string.IsNullOrEmpty(_captureBuildName))
                {
                    CaptureCurrentBoardAsBuild();
                    _captureMode = false;
                    _showBuildManager = true;
                }
            }
        }

        // ==================== F9 推荐面板 ====================

        private void DrawRecommendationPanel()
        {
            var px = 10f;
            var py = 10f;
            var pw = 350f;
            var ph = Mathf.Min(400f, Screen.height - 20f);
            GUI.DrawTexture(new Rect(px, py, pw, ph), Tex(new Color(0, 0, 0, 0.85f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 13, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;

            // 标题栏
            var ts = new GUIStyle(s) { fontSize = 15, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(1f, 0.8f, 0.2f);
            GUI.Label(new Rect(px + 10, py + 5, pw - 50, 22), "阵容推荐 (F9关闭)", ts);
            GUI.Label(new Rect(px + 10, py + 28, pw - 20, 18),
                string.Format("当前英雄: {0}", string.IsNullOrEmpty(_detectedHero) ? "未检测" : _detectedHero), s);

            // 实时刷新匹配结果（仅玩家物品） + 商店物品
            var currentItems = GatherAllItemNames();
            var shopItems = GatherShopItemNames();
            _matchResults = MatchBuilds(_detectedHero, currentItems);
            // 检查商店中是否有匹配的缺失物品
            foreach (var mr in _matchResults)
            {
                mr.ShopCoreMatches = mr.CoreMissing.FindAll(n => shopItems.Contains(n));
                mr.ShopFlexMatches = mr.FlexMissing.FindAll(n => shopItems.Contains(n));
            }

            // 匹配结果
            _recScroll = GUI.BeginScrollView(new Rect(px + 5, py + 50, pw - 10, ph - 60), _recScroll,
                new Rect(0, 0, pw - 30, _matchResults.Count * 85 + 10));

            float ry = 5;
            float barMaxW = pw - 95; // 进度条最大宽度（留空间给百分比）
            for (int i = 0; i < _matchResults.Count && i < 10; i++)
            {
                var mr = _matchResults[i];
                var score = mr.MatchScore * 100f;

                // 阵容名
                var cs = new GUIStyle(s) { fontSize = 13 };
                cs.normal.textColor = (i == 0) ? new Color(1f, 0.8f, 0.2f) : Color.white;
                GUI.Label(new Rect(10, ry, pw - 30, 18), string.Format("{0}. {1}", i + 1, mr.Template.BuildName), cs);
                ry += 20;

                // 绿色进度条 + 右对齐百分比
                var barY = ry;
                var barH = 16f;
                var barBgRect = new Rect(10, barY, barMaxW, barH);
                GUI.DrawTexture(barBgRect, Tex(new Color(0.15f, 0.15f, 0.15f, 0.8f))); // 底色

                var barFillW = barMaxW * (score / 100f);
                if (barFillW > 0)
                {
                    var fillColor = score >= 70f ? new Color(0.1f, 0.9f, 0.2f)
                        : score >= 40f ? new Color(0.8f, 0.8f, 0.1f)
                        : new Color(0.8f, 0.3f, 0.1f);
                    GUI.DrawTexture(new Rect(10, barY, barFillW, barH), Tex(fillColor));
                }

                // 百分比右对齐
                var pctStyle = new GUIStyle(s) { fontSize = 12, alignment = TextAnchor.MiddleRight };
                pctStyle.normal.textColor = Color.white;
                GUI.Label(new Rect(10 + barMaxW, barY, 55, barH), string.Format("{0:F0}%", score), pctStyle);

                ry += barH + 4;

                // 缺失物品（商店有货的醒目闪烁）
                if (mr.CoreMissing.Count > 0)
                {
                    DrawMissingLine(15, ref ry, pw - 30, "缺核心: ", mr.CoreMissing, mr.ShopCoreMatches,
                        new Color(1f, 0.3f, 0.3f), new Color(0.2f, 1f, 0.5f));
                }
                if (mr.FlexMissing.Count > 0)
                {
                    DrawMissingLine(15, ref ry, pw - 30, "缺灵活: ", mr.FlexMissing, mr.ShopFlexMatches,
                        new Color(1f, 0.7f, 0.3f), new Color(0.5f, 1f, 0.5f));
                }
                ry += 5;
            }

            if (_matchResults.Count == 0)
            {
                s.normal.textColor = Color.gray;
                GUI.Label(new Rect(10, ry, 300, 18), "暂无匹配阵容（F10添加阵容）", s);
            }

            GUI.EndScrollView();

            // 底部按钮
            if (GUI.Button(new Rect(px + 10, py + ph - 25, 55, 20), "F8捕获", GUI.skin.button))
                _captureMode = true;
            if (GUI.Button(new Rect(px + 70, py + ph - 25, 55, 20), "F10管理", GUI.skin.button))
                _showBuildManager = true;
            if (GUI.Button(new Rect(px + 130, py + ph - 25, 55, 20), "F5导出", GUI.skin.button))
            {
                try { ExportToJson(GatherBoardData()); }
                catch { }
            }
        }

        // ==================== F10 阵容管理面板 ====================

        private void DrawBuildManagerPanel()
        {
            var px = 10f;
            var py = Screen.height - 410f;
            var pw = 380f;
            var ph = 400f;
            if (py < 10) py = 10;
            GUI.DrawTexture(new Rect(px, py, pw, ph), Tex(new Color(0.05f, 0.05f, 0.1f, 0.95f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 12, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;
            var ts = new GUIStyle(s) { fontSize = 14, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(0.3f, 0.8f, 1f);

            GUI.Label(new Rect(px + 10, py + 5, pw - 50, 22), "阵容管理 (F10关闭)", ts);

            // 当前英雄
            GUI.Label(new Rect(px + 10, py + 28, pw - 20, 18),
                string.Format("当前英雄: {0} | 共 {1} 个阵容",
                    string.IsNullOrEmpty(_detectedHero) ? "未检测" : _detectedHero, _builds.Count), s);

            // 筛选英雄
            var heroBuilds = string.IsNullOrEmpty(_detectedHero)
                ? _builds : _builds.FindAll(b => b.HeroName == _detectedHero || string.IsNullOrEmpty(b.HeroName));
            if (heroBuilds.Count == 0) heroBuilds = _builds;

            float listY = py + 52;

            // 新建阵容
            GUI.Label(new Rect(px + 10, listY, 45, 18), "新建:", s);
            _newBuildNameInput = GUI.TextField(new Rect(px + 50, listY, 110, 20), _newBuildNameInput ?? "", 20);
            GUI.Label(new Rect(px + 165, listY, 30, 18), "英雄:", s);
            _newBuildHeroInput = GUI.TextField(new Rect(px + 195, listY, 80, 20), _newBuildHeroInput ?? _detectedHero, 20);
            if (GUI.Button(new Rect(px + 280, listY, 40, 20), "创建"))
            {
                if (!string.IsNullOrEmpty(_newBuildNameInput))
                {
                    _builds.Add(new BuildTemplate
                    {
                        HeroName = string.IsNullOrEmpty(_newBuildHeroInput) ? _detectedHero : _newBuildHeroInput,
                        BuildName = _newBuildNameInput,
                        CoreItems = new List<string>(),
                        FlexItems = new List<string>()
                    });
                    SaveBuilds();
                    _newBuildNameInput = "";
                    _newBuildHeroInput = "";
                }
            }
            listY += 26;

            // 阵容列表滚动
            float scrollH = ph - (listY - py) - 30;
            _buildListScroll = GUI.BeginScrollView(
                new Rect(px + 5, listY, pw - 15, scrollH),
                _buildListScroll,
                new Rect(0, 0, pw - 35, heroBuilds.Count * 200 + 10));

            float ry = 5;
            foreach (var build in heroBuilds)
            {
                var isSelected = _selectedBuildForEdit == build.BuildName;
                var expandH = isSelected ? 170f : 22f;

                // 背景
                GUI.DrawTexture(new Rect(0, ry, pw - 35, expandH),
                    Tex(isSelected ? new Color(0.15f, 0.15f, 0.25f, 0.7f) : new Color(0.1f, 0.1f, 0.15f, 0.5f)));

                // 标题行
                GUI.Label(new Rect(5, ry + 2, 120, 18),
                    string.Format("{0} ({1})", build.BuildName, build.HeroName), s);

                if (GUI.Button(new Rect(250, ry + 1, 35, 18), isSelected ? "收起" : "编辑"))
                {
                    _selectedBuildForEdit = isSelected ? "" : build.BuildName;
                }
                if (GUI.Button(new Rect(290, ry + 1, 35, 18), "删除"))
                {
                    _builds.Remove(build);
                    SaveBuilds();
                    _selectedBuildForEdit = "";
                    break;
                }

                if (isSelected)
                {
                    var ns = new GUIStyle(s) { fontSize = 11 };
                    ns.normal.textColor = new Color(1f, 0.5f, 0.5f);
                    GUI.Label(new Rect(15, ry + 26, 330, 16), "核心物品:", ns);

                    float iy = ry + 42;
                    for (int i = 0; i < build.CoreItems.Count; i++)
                    {
                        GUI.Label(new Rect(25, iy, 200, 16), build.CoreItems[i], ns);
                        if (GUI.Button(new Rect(250, iy, 25, 15), "X"))
                        {
                            build.CoreItems.RemoveAt(i);
                            SaveBuilds();
                        }
                        iy += 17;
                    }

                    ns.normal.textColor = new Color(1f, 0.7f, 0.3f);
                    GUI.Label(new Rect(15, iy, 330, 16), "灵活物品:", ns);
                    iy += 16;
                    for (int i = 0; i < build.FlexItems.Count; i++)
                    {
                        GUI.Label(new Rect(25, iy, 200, 16), build.FlexItems[i], ns);
                        if (GUI.Button(new Rect(250, iy, 25, 15), "X"))
                        {
                            build.FlexItems.RemoveAt(i);
                            SaveBuilds();
                        }
                        iy += 17;
                    }

                    // 添加物品输入
                    GUI.Label(new Rect(15, iy, 50, 18), "添加:", s);
                    _newItemInput = GUI.TextField(new Rect(65, iy, 150, 20), _newItemInput ?? "", 30);
                    if (GUI.Button(new Rect(220, iy, 35, 20), "核心"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput) && !build.CoreItems.Contains(_newItemInput))
                        {
                            build.CoreItems.Add(_newItemInput);
                            SaveBuilds();
                            _newItemInput = "";
                        }
                    }
                    if (GUI.Button(new Rect(260, iy, 35, 20), "灵活"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput) && !build.FlexItems.Contains(_newItemInput))
                        {
                            build.FlexItems.Add(_newItemInput);
                            SaveBuilds();
                            _newItemInput = "";
                        }
                    }
                }

                ry += expandH + 5;
            }

            GUI.EndScrollView();

            // 底部提示
            GUI.Label(new Rect(px + 10, py + ph - 20, pw - 20, 16),
                "提示: 按F8捕获当前棋盘 | 物品用英文名", s);
        }

        // ==================== 标签收集 ====================

        private void RefreshOverlayLabels()
        {
            _itemLabels.Clear();
            _skillLabels.Clear();
            _shopLabels.Clear();
            var cam = Camera.main ?? Camera.current;
            if (cam == null) return;

            try
            {
#pragma warning disable 0618
                var all = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (all != null)
                {
                    foreach (var cc in all)
                    {
                        try
                        {
                            var cd = cc.CardData;
                            if (cd == null) continue;
                            var typeStr = cd.Type.ToString();
                            var sp = cam.WorldToScreenPoint(cc.transform.position);
                            if (typeStr == "Item")
                            {
                                sp.y -= _itemOffsetY;
                                _itemLabels.Add(new OverlayLabel { Text = GetCardName(cd), Tier = cd.Tier.ToString(), ScreenPos = sp });
                            }
                            else if (typeStr == "Skill")
                            {
                                sp.y -= _skillOffsetY;
                                _skillLabels.Add(new OverlayLabel { Text = GetCardName(cd), Tier = cd.Tier.ToString(), ScreenPos = sp });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (_skillListField != null)
            {
                try
                {
#pragma warning disable 0618
                    var spm = UnityEngine.Object.FindObjectsOfType<TheBazaar.SkillPresentationManager>();
#pragma warning restore 0618
                    if (spm != null && spm.Length > 0)
                    {
                        var list = _skillListField.GetValue(spm[0]) as IList;
                        if (list != null)
                        {
                            foreach (var obj in list)
                            {
                                try
                                {
                                    var r = obj as TheBazaar.SkillProxyRenderer;
                                    if (r == null || r.Card == null) continue;
                                    var sp = cam.WorldToScreenPoint(r.transform.position);
                                    sp.y -= _skillOffsetY;
                                    _skillLabels.Add(new OverlayLabel { Text = GetCardName(r.Card), Tier = r.Card.Tier.ToString(), ScreenPos = sp });
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            ScanShops(cam);
        }

        private void ScanShops(Camera cam)
        {
            try
            {
#pragma warning disable 0618
                var encounters = UnityEngine.Object.FindObjectsOfType<EncounterController>();
#pragma warning restore 0618
                if (encounters == null) return;

                foreach (var ec in encounters)
                {
                    if (ec == null) continue;
                    var t = ec.transform;
                    var sp = cam.WorldToScreenPoint(t.position);
                    sp.y -= _shopOffsetY;
                    var shopName = t.name;
                    if (shopName.EndsWith("(Clone)"))
                        shopName = shopName.Substring(0, shopName.Length - 7);
                    _shopLabels.Add(new OverlayLabel { Text = shopName, Tier = "Invalid", ScreenPos = sp });
                }
            }
            catch { }
        }

        private void DrawMissingLine(float x, ref float y, float maxW, string prefix,
            List<string> missing, List<string> inShop, Color missingColor, Color shopColor)
        {
            var ms = new GUIStyle(_labelStyle) { fontSize = 11, alignment = TextAnchor.UpperLeft };
            ms.normal.textColor = missingColor;
            // 前缀
            GUI.Label(new Rect(x, y, 55, 16), prefix, ms);

            float cx = x + 55;
            for (int i = 0; i < missing.Count; i++)
            {
                var item = missing[i];
                var inStore = inShop.Contains(item);

                // 商店有货 → 闪烁绿色
                if (inStore)
                {
                    var blink = 0.6f + 0.4f * Mathf.Sin(Time.time * 5f);
                    ms.normal.textColor = new Color(shopColor.r, shopColor.g, shopColor.b, blink);
                }
                else
                {
                    ms.normal.textColor = missingColor;
                }

                var itemText = (i < missing.Count - 1) ? item + ", " : item;
                var sz = ms.CalcSize(new GUIContent(itemText));
                GUI.Label(new Rect(cx, y, sz.x + 5, 16), itemText, ms);

                // 商店有货加 ★ 标记
                if (inStore)
                {
                    var starStyle = new GUIStyle(ms) { fontSize = 10 };
                    starStyle.normal.textColor = new Color(1f, 0.9f, 0.1f);
                    GUI.Label(new Rect(cx + sz.x - 2, y - 2, 20, 16), "★", starStyle);
                }

                cx += sz.x + 2;
                if (cx > x + maxW - 30) { cx = x + 55; y += 16; }
            }
            y += 16;
        }

        private string GetCardName(BazaarGameClient.Domain.Models.Cards.Card c)
        {
            try
            {
                if (c.Template != null && !string.IsNullOrEmpty(c.Template.InternalName))
                    return c.Template.InternalName;
                if (!string.IsNullOrEmpty(c.Name)) return c.Name;
            }
            catch { }
            return "???";
        }

        // ==================== 英雄检测 ====================

        private string DetectHero()
        {
            try
            {
                // 尝试 RunManager.Instance
                var rmType = typeof(BoardManager).Assembly.GetType("TheBazaar.RunManager");
                if (rmType != null)
                {
                    var instProp = rmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instProp != null)
                    {
                        var inst = instProp.GetValue(null, null);
                        if (inst != null)
                        {
                            // 尝试读取 Hero / HeroName / SelectedHero
                            foreach (var pn in new[] { "Hero", "HeroName", "SelectedHero", "CurrentHero" })
                            {
                                var p = rmType.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p != null)
                                {
                                    var val = p.GetValue(inst, null);
                                    if (val != null && !string.IsNullOrEmpty(val.ToString()))
                                    {
                                        _logger.LogInfo(string.Format("[BoardReader] 检测到英雄: {0}", val));
                                        return val.ToString();
                                    }
                                }
                            }
                            // 尝试 field
                            foreach (var fn in new[] { "_hero", "_heroName", "_selectedHero" })
                            {
                                var f = rmType.GetField(fn, BindingFlags.Instance | BindingFlags.NonPublic);
                                if (f != null)
                                {
                                    var val = f.GetValue(inst);
                                    if (val != null && !string.IsNullOrEmpty(val.ToString()))
                                    {
                                        _logger.LogInfo(string.Format("[BoardReader] 检测到英雄: {0}", val));
                                        return val.ToString();
                                    }
                                }
                            }
                        }
                    }
                }

                // 尝试 ClientRunModel
                var crmType = typeof(BoardManager).Assembly.GetType("TheBazaar.ClientRunModel");
                if (crmType != null)
                {
                    var instProp = crmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instProp != null)
                    {
                        var inst = instProp.GetValue(null, null);
                        if (inst != null)
                        {
                            foreach (var pn in new[] { "Hero", "HeroName", "HeroId" })
                            {
                                var p = crmType.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p != null)
                                {
                                    var val = p.GetValue(inst, null);
                                    if (val != null && !string.IsNullOrEmpty(val.ToString()))
                                    {
                                        _logger.LogInfo(string.Format("[BoardReader] 检测到英雄: {0}", val));
                                        return val.ToString();
                                    }
                                }
                            }
                        }
                    }
                }

                // 尝试从 EncounterController 的 boardSection 推断
#pragma warning disable 0618
                var encounters = UnityEngine.Object.FindObjectsOfType<EncounterController>();
#pragma warning restore 0618
                if (encounters != null)
                {
                    foreach (var ec in encounters)
                    {
                        if (ec == null) continue;
                        var f = ec.GetType().GetField("boardSection", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f == null) continue;
                        var val = f.GetValue(ec);
                        if (val != null && val.ToString() == "Opponent")
                        {
                            // 从 GameObject name 提取英雄名
                            var nm = ec.name;
                            if (nm.Contains("("))
                                nm = nm.Substring(0, nm.IndexOf("(")).Trim();
                            if (!string.IsNullOrEmpty(nm) && nm != "Treasure Chest" && nm != "Socket")
                            {
                                _logger.LogInfo(string.Format("[BoardReader] 从 Encounter 检测到英雄: {0}", nm));
                                return nm;
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        // ==================== 物品名称收集 ====================

        private List<string> GatherCurrentBoardItemNames()
        {
            var result = new List<KeyValuePair<float, string>>();
            // 方法与 overlay 一致：通过 CardController 扫描
            try
            {
#pragma warning disable 0618
                var all = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (all != null)
                {
                    foreach (var cc in all)
                    {
                        try
                        {
                            var cd = cc.CardData;
                            if (cd == null || cd.Type.ToString() != "Item") continue;
                            // 排除商店物品
                            if (IsUnderEncounter(cc.transform)) continue;
                            // 只取玩家棋盘上的物品
                            if (_isPlayerBoardProp != null)
                            {
                                try
                                {
                                    if (!((bool)_isPlayerBoardProp.GetValue(cc, null)))
                                        continue;
                                }
                                catch { }
                            }
                            var name = GetCardName(cd);
                            if (!string.IsNullOrEmpty(name) && name != "???")
                                result.Add(new KeyValuePair<float, string>(cc.transform.position.x, name));
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 回退：BoardManager 方法
            if (result.Count == 0)
            {
                try
                {
                    if (_playerCardsOnBoardField != null)
                    {
                        var bm = BoardManager.Instance;
                        if (bm != null)
                        {
                            var list = _playerCardsOnBoardField.GetValue(bm) as IList;
                            if (list != null)
                            {
                                foreach (var o in list)
                                {
                                    if (o == null) continue;
                                    var ctrl = o as ItemController;
                                    if (ctrl == null) continue;
                                    var cd = ctrl.CardData;
                                    if (cd == null) continue;
                                    var name = GetCardName(cd);
                                    if (!string.IsNullOrEmpty(name) && name != "???")
                                        result.Add(new KeyValuePair<float, string>(((MonoBehaviour)ctrl).transform.position.x, name));
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 按 X 坐标排序（从左到右，游戏棋盘顺序）
            result.Sort(delegate (KeyValuePair<float, string> a, KeyValuePair<float, string> b)
            {
                return a.Key.CompareTo(b.Key);
            });
            var names = new List<string>();
            foreach (var kv in result) names.Add(kv.Value);
            return names;
        }

        private List<string> GatherAllItemNames()
        {
            // CardController 扫描，排除 EncounterController 子对象（商店物品）
            var result = new List<string>();
            try
            {
#pragma warning disable 0618
                var all = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (all != null)
                {
                    foreach (var cc in all)
                    {
                        try
                        {
                            var cd = cc.CardData;
                            if (cd == null || cd.Type.ToString() != "Item") continue;
                            // 排除商店物品：检查父级是否有 EncounterController
                            if (IsUnderEncounter(cc.transform)) continue;
                            var name = GetCardName(cd);
                            if (!string.IsNullOrEmpty(name) && name != "???")
                                result.Add(name);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        private bool IsUnderEncounter(Transform t)
        {
            while (t != null)
            {
                var ec = t.GetComponent<EncounterController>();
                if (ec != null) return true;
                t = t.parent;
            }
            return false;
        }

        // 扫描商店待售物品
        private List<string> GatherShopItemNames()
        {
            var result = new List<string>();
            try
            {
#pragma warning disable 0618
                var all = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (all != null)
                {
                    foreach (var cc in all)
                    {
                        try
                        {
                            var cd = cc.CardData;
                            if (cd == null || cd.Type.ToString() != "Item") continue;
                            if (!IsUnderEncounter(cc.transform)) continue;
                            var name = GetCardName(cd);
                            if (string.IsNullOrEmpty(name) || name == "???") continue;
                            result.Add(name);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        // ==================== 阵容系统 ====================

        private void LoadBuilds()
        {
            try
            {
                if (File.Exists(_buildsPath))
                {
                    var json = File.ReadAllText(_buildsPath, Encoding.UTF8);
                    _builds = JsonConvert.DeserializeObject<List<BuildTemplate>>(json) ?? new List<BuildTemplate>();
                    _logger.LogInfo(string.Format("[BoardReader] 已加载 {0} 个阵容", _builds.Count));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(string.Format("[BoardReader] 加载阵容失败: {0}", ex));
                _builds = new List<BuildTemplate>();
            }
        }

        private void SaveBuilds()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_builds, Formatting.Indented);
                File.WriteAllText(_buildsPath, json, Encoding.UTF8);
                _logger.LogInfo(string.Format("[BoardReader] 已保存 {0} 个阵容", _builds.Count));
            }
            catch (Exception ex)
            {
                _logger.LogError(string.Format("[BoardReader] 保存阵容失败: {0}", ex));
            }
        }

        private List<BuildMatchResult> MatchBuilds(string heroName, List<string> currentItems)
        {
            var results = new List<BuildMatchResult>();
            var ownedSet = new HashSet<string>(currentItems);

            foreach (var build in _builds)
            {
                // 按英雄筛选
                if (!string.IsNullOrEmpty(heroName) && !string.IsNullOrEmpty(build.HeroName)
                    && build.HeroName != heroName)
                    continue;

                var coreOwned = new List<string>();
                var coreMissing = new List<string>();
                foreach (var item in build.CoreItems)
                {
                    if (ownedSet.Contains(item)) coreOwned.Add(item);
                    else coreMissing.Add(item);
                }

                var flexOwned = new List<string>();
                var flexMissing = new List<string>();
                foreach (var item in build.FlexItems)
                {
                    if (ownedSet.Contains(item)) flexOwned.Add(item);
                    else flexMissing.Add(item);
                }

                float coreScore = build.CoreItems.Count > 0
                    ? (float)coreOwned.Count / build.CoreItems.Count : 0f;
                float flexScore = build.FlexItems.Count > 0
                    ? (float)flexOwned.Count / build.FlexItems.Count : 0f;
                float totalScore = coreScore * 0.7f + flexScore * 0.3f;

                results.Add(new BuildMatchResult
                {
                    Template = build,
                    MatchScore = totalScore,
                    CoreOwned = coreOwned,
                    CoreMissing = coreMissing,
                    FlexOwned = flexOwned,
                    FlexMissing = flexMissing
                });
            }

            results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
            return results;
        }

        // ==================== JSON导出 ====================

        private BoardData GatherBoardData()
        {
            var d = new BoardData
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BoardItems = new List<CardInfo>(),
                StorageItems = new List<CardInfo>(),
                SkillCards = new List<CardInfo>(),
                Shops = new List<ShopInfo>()
            };

            if (_playerCardsOnBoardField != null)
            {
                try
                {
                    var bm = BoardManager.Instance;
                    if (bm != null)
                    {
                        var list = _playerCardsOnBoardField.GetValue(bm) as IList;
                        if (list != null)
                        {
                            foreach (var o in list)
                            {
                                if (o == null) continue;
                                var ctrl = o as ItemController;
                                if (ctrl == null) continue;
                                var cd = ctrl.CardData;
                                if (cd == null) continue;
                                var loc = "Board";
                                try { if (_isPlayerBoardProp != null) loc = ((bool)_isPlayerBoardProp.GetValue(ctrl, null)) ? "Board" : "Storage"; } catch { }
                                var ci = new CardInfo { Name = GetCardName(cd), InstanceId = cd.InstanceId != null ? cd.InstanceId.ToString() : "", CardType = cd.Type.ToString(), CardSize = cd.Size.ToString(), Tier = cd.Tier.ToString(), Location = loc, Attributes = new Dictionary<string, int>() };
                                if (cd.Attributes != null) foreach (var kv in cd.Attributes) if (kv.Value != 0) ci.Attributes[kv.Key.ToString()] = kv.Value;
                                if (loc == "Board") d.BoardItems.Add(ci); else d.StorageItems.Add(ci);
                            }
                        }
                    }
                }
                catch { }
            }

            if (_skillListField != null)
            {
                try
                {
#pragma warning disable 0618
                    var spm = UnityEngine.Object.FindObjectsOfType<TheBazaar.SkillPresentationManager>();
#pragma warning restore 0618
                    if (spm != null && spm.Length > 0)
                    {
                        var list = _skillListField.GetValue(spm[0]) as IList;
                        if (list != null)
                        {
                            foreach (var o in list)
                            {
                                try
                                {
                                    var r = o as TheBazaar.SkillProxyRenderer;
                                    if (r != null && r.Card != null)
                                    {
                                        var ci = new CardInfo { Name = GetCardName(r.Card), InstanceId = r.Card.InstanceId != null ? r.Card.InstanceId.ToString() : "", CardType = r.Card.Type.ToString(), CardSize = r.Card.Size.ToString(), Tier = r.Card.Tier.ToString(), Location = "Skill", Attributes = new Dictionary<string, int>() };
                                        if (r.Card.Attributes != null) foreach (var kv in r.Card.Attributes) if (kv.Value != 0) ci.Attributes[kv.Key.ToString()] = kv.Value;
                                        d.SkillCards.Add(ci);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            try
            {
#pragma warning disable 0618
                var encounters = UnityEngine.Object.FindObjectsOfType<EncounterController>();
#pragma warning restore 0618
                if (encounters != null)
                {
                    foreach (var ec in encounters)
                    {
                        if (ec == null) continue;
                        var t = ec.transform;
                        d.Shops.Add(new ShopInfo
                        {
                            GameObjectName = t.name,
                            DisplayName = t.name,
                            Position = string.Format("{0:F1},{1:F1},{2:F1}", t.position.x, t.position.y, t.position.z)
                        });
                    }
                }
            }
            catch { }

            return d;
        }

        private void ExportToJson(BoardData d)
        {
            var dir = Path.Combine(Paths.GameRootPath, "BoardData");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, string.Format("board_{0}.json", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            File.WriteAllText(p, JsonConvert.SerializeObject(d, Formatting.Indented));
            _logger.LogInfo(string.Format("[BoardReader] 已导出: 物品={0} 仓库={1} 技能={2} 商店={3}",
                d.BoardItems.Count, d.StorageItems.Count, d.SkillCards.Count, d.Shops.Count));
        }
    }

    // ==================== 数据结构 ====================

    internal class OverlayLabel { public string Text; public string Tier; public Vector3 ScreenPos; }

    [Serializable]
    public class BuildTemplate
    {
        public string HeroName = "";
        public string BuildName = "";
        public List<string> CoreItems = new List<string>();
        public List<string> FlexItems = new List<string>();
    }

    public class BuildMatchResult
    {
        public BuildTemplate Template;
        public float MatchScore;
        public List<string> CoreOwned = new List<string>();
        public List<string> CoreMissing = new List<string>();
        public List<string> FlexOwned = new List<string>();
        public List<string> FlexMissing = new List<string>();
        public List<string> ShopCoreMatches = new List<string>();
        public List<string> ShopFlexMatches = new List<string>();
    }

    [Serializable]
    public class BoardData
    {
        public string Timestamp;
        public List<CardInfo> BoardItems;
        public List<CardInfo> StorageItems;
        public List<CardInfo> SkillCards;
        public List<ShopInfo> Shops;
    }

    [Serializable]
    public class ShopInfo
    {
        public string GameObjectName;
        public string DisplayName;
        public string Position;
    }

    [Serializable]
    public class CardInfo
    {
        public string Name = "未知";
        public string InstanceId;
        public string CardType;
        public string CardSize;
        public string Tier;
        public bool IsLegendary;
        public float Dps;
        public float Hps;
        public float CombatPower;
        public string RelativePower;
        public string Enchantment;
        public string Location;
        public int EffectCount;
        public List<string> ArtKeys;
        public Dictionary<string, int> Attributes;
    }
}
