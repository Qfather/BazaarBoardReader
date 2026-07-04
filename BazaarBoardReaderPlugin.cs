using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace BazaarBoardReader
{
    [HarmonyPatch(typeof(CardController), "SetCardData")]
    static class CardController_SetCardData_Patch
    {
        static void Postfix(CardController __instance)
        {
            try
            {
                var cd = __instance.CardData;
                if (cd == null) return;
                var typeStr = cd.Type.ToString();
                if (typeStr != "Item" && typeStr != "Skill") return;
                var name = BazaarBoardReaderPlugin.GetCardNameStatic(cd);
                if (string.IsNullOrEmpty(name) || name == "???") return;
                var id = __instance.GetInstanceID();
                bool isPlayer = false;
                try { isPlayer = BazaarBoardReaderPlugin.IsPlayerBoardFor(__instance); }
                catch { }
                lock (BazaarBoardReaderPlugin.TrackedCards)
                {
                    BazaarBoardReaderPlugin.TrackedCards[id] = new TrackedCard
                    { Name = name, Type = typeStr, IsPlayer = isPlayer, LastSeen = Time.time };
                }
            }
            catch { }
        }
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BazaarBoardReaderPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.bazaar.boardreader";
        private const string PluginName = "BazaarBoardReader";
        private const string PluginVersion = "7.0.0";

        private ManualLogSource _logger;
        private float _lastExportTime;
        private const float CooldownSeconds = 2f;

        private static FieldInfo _playerCardsOnBoardField;
        private static FieldInfo _skillListField;
        private static PropertyInfo _isPlayerBoardProp;
        private static PropertyInfo _sIsPlayerBoardProp;

        private static bool _inputChecked;
        private static bool _useNewInput;

        private bool _overlayEnabled;
        private bool _showSliders;
        private bool _showRecommendations;
        private bool _showBuildManager;

        private float _lastRefreshTime;
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

        private readonly Dictionary<string, Color> _tierColors = new Dictionary<string, Color>
        {
            { "Bronze", new Color(1f, 0.95f, 0.7f) },
            { "Silver", new Color(0.3f, 0.75f, 1f) },
            { "Gold", new Color(1f, 0.8f, 0.05f) },
            { "Diamond", new Color(1f, 0.5f, 0.7f) },
            { "Legendary", new Color(1f, 0.15f, 0.1f) },
            { "Invalid", new Color(0.7f, 0.7f, 0.7f) },
        };

        private List<BuildTemplate> _builds = new List<BuildTemplate>();
        private List<BuildMatchResult> _matchResults = new List<BuildMatchResult>();
        private string _detectedHero = "";
        private string _buildsPath;
        private Vector2 _buildListScroll;
        private Vector2 _recScroll;
        private string _captureBuildName = "";
        private string _captureHeroInput = "";
        private bool _captureMode;
        private string _newItemInput = "";
        private string _newBuildNameInput = "";
        private string _newBuildHeroInput = "";
        private string _selectedBuildForEdit = "";

        private Dictionary<string, string> _translations = new Dictionary<string, string>();
        private bool _useChinese = true;
        // 商店推荐
        private Dictionary<string, EventShopEntry> _shopData = new Dictionary<string, EventShopEntry>();
        private Dictionary<string, CardDbEntry> _cardDb = new Dictionary<string, CardDbEntry>();

        public static Dictionary<int, TrackedCard> TrackedCards = new Dictionary<int, TrackedCard>();

        public static bool IsPlayerBoardFor(CardController cc)
        {
            if (_sIsPlayerBoardProp == null) return false;
            try { return (bool)_sIsPlayerBoardProp.GetValue(cc, null); }
            catch { return false; }
        }

        public static string GetCardNameStatic(BazaarGameClient.Domain.Models.Cards.Card c)
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
            _overlayEnabled = Config.Bind(CfgSecGeneral, CfgOverlay, true).Value;
            _logger.LogInfo(string.Format("[BoardReader] v7.0 物品={0} 技能={1} 商店={2} 背景={3:F0}%",
                (int)_itemOffsetY, (int)_skillOffsetY, (int)_shopOffsetY, _bgOpacity * 100f));

            _labelStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
            _shopStyle = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };

            _playerCardsOnBoardField = typeof(BoardManager).GetField("_playerCardsOnBoard", BindingFlags.Instance | BindingFlags.NonPublic);
            _isPlayerBoardProp = typeof(CardController).GetProperty("IsPlayerBoard", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _sIsPlayerBoardProp = _isPlayerBoardProp;
            _skillListField = typeof(TheBazaar.SkillPresentationManager).GetField("_skillList", BindingFlags.Instance | BindingFlags.NonPublic);

            _buildsPath = Path.Combine(Paths.ConfigPath, "BazaarBoardReader_Builds.json");
            LoadBuilds();
            LoadTranslations();
            LoadShopRecommendationData();

            try
            {
                var harmony = new Harmony("com.bazaar.boardreader.patches");
                harmony.PatchAll(typeof(CardController_SetCardData_Patch).Assembly);
                _logger.LogInfo("[BoardReader] Harmony OK");
            }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] Harmony: {0}", ex)); }
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
            if (IsKeyPressed("f6"))
            {
                _overlayEnabled = !_overlayEnabled;
                _showSliders = _overlayEnabled;
                _showRecommendations = _overlayEnabled;
                _showBuildManager = _overlayEnabled;
                Config[CfgSecGeneral, CfgOverlay].BoxedValue = _overlayEnabled;
                Config.Save();
            }

            if (IsKeyPressed("f5"))
            {
                if (Time.time - _lastExportTime < CooldownSeconds) return;
                _lastExportTime = Time.time;
                try { ExportToJson(GatherBoardData()); }
                catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 导出: {0}", ex)); }
            }

            if (IsKeyPressed("f8"))
            {
                _captureMode = true;
                _captureBuildName = "";
                _captureHeroInput = "";
            }

            if (Time.frameCount % 300 == 0)
            {
                var cutoff = Time.time - 5f;
                var toRemove = new List<int>();
                lock (TrackedCards)
                {
                    foreach (var kv in TrackedCards)
                        if (kv.Value.LastSeen < cutoff) toRemove.Add(kv.Key);
                    foreach (var k in toRemove) TrackedCards.Remove(k);
                }
            }

            if (_overlayEnabled && Time.frameCount % 60 == 0 && Time.time - _lastRefreshTime > 0.5f)
            {
                _lastRefreshTime = Time.time;
                try { RefreshOverlayLabels(); }
                catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] Refresh: {0}", ex)); }
                if (string.IsNullOrEmpty(_detectedHero)) _detectedHero = DetectHero();
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
                    { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, false); }
                    foreach (var lbl in _skillLabels)
                    { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, false); }
                    foreach (var lbl in _shopLabels)
                    { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, true); }
                }
            }
            if (_showSliders) DrawSlidersPanel();
            if (_captureMode) DrawCaptureDialog();
            if (_showRecommendations) DrawRecommendationPanel();
            if (_showBuildManager) DrawBuildManagerPanel();
            DrawHoverTooltip();
        }

        // ==================== 绘制基础 ====================

        private Rect _lastHoverRect;
        private string _hoverTooltip = "";

        private void DrawLabel(OverlayLabel lbl, bool isShop)
        {
            var style = isShop ? _shopStyle : _labelStyle;
            var sz = style.CalcSize(new GUIContent(lbl.Text));
            var w = (int)(sz.x + 16);
            var extraH = 0;
            if (!string.IsNullOrEmpty(lbl.SubText))
            {
                var subSz = style.CalcSize(new GUIContent(lbl.SubText));
                if (subSz.x + 16 > w) w = (int)(subSz.x + 16);
                extraH = (int)(subSz.y + 2);
            }
            var h = (int)(sz.y + 6) + extraH;
            var r = new Rect(lbl.ScreenPos.x - w / 2, Screen.height - lbl.ScreenPos.y, w, h);
            GUI.DrawTexture(r, GetRoundedBg(w, h));

            // 描边
            var os = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, 0.9f) } };
            var tr = new Rect(r.x + 8, r.y + 3, sz.x, sz.y);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(tr.x + dx, tr.y + dy, sz.x, sz.y), lbl.Text, os);

            // 主文字颜色
            if (isShop)
            {
                style.normal.textColor = new Color(0.2f, 1f, 0.3f);
            }
            else if (_tierColors.ContainsKey(lbl.Tier))
            {
                style.normal.textColor = _tierColors[lbl.Tier];
            }
            else
            {
                style.normal.textColor = Color.white;
            }
            GUI.Label(tr, lbl.Text, style);

            // 副文字（推荐/暴击率）
            if (!string.IsNullOrEmpty(lbl.SubText))
            {
                var subStyle = new GUIStyle(style) { fontSize = 11, alignment = TextAnchor.UpperCenter };
                var isCrit = false;
                if (lbl.SubText == "推荐") subStyle.normal.textColor = new Color(0.2f, 1f, 0.3f);
                else if (lbl.SubText == "一般") subStyle.normal.textColor = new Color(1f, 0.8f, 0.2f);
                else if (lbl.SubText == "不推荐") subStyle.normal.textColor = new Color(0.7f, 0.3f, 0.3f);
                else { subStyle.normal.textColor = new Color(1f, 0.25f, 0.2f); subStyle.fontSize = 13; subStyle.fontStyle = FontStyle.Bold; isCrit = true; }
                var subRect = new Rect(r.x, r.y + 3 + sz.y + 1, w, extraH);
                if (isCrit)
                {
                    // 白色描边
                    var sk = new GUIStyle(subStyle) { normal = { textColor = Color.black } };
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            if (dx != 0 || dy != 0)
                                GUI.Label(new Rect(subRect.x + dx, subRect.y + dy, subRect.width, subRect.height), lbl.SubText, sk);
                }
                GUI.Label(subRect, lbl.SubText, subStyle);
            }

            // 悬停检测
            if (!string.IsNullOrEmpty(lbl.HoverData))
            {
                var mousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                if (r.Contains(mousePos))
                {
                    _hoverTooltip = lbl.HoverData;
                    _lastHoverRect = r;
                }
            }
        }

        // 在 OnGUI 末尾绘制悬停提示
        private void DrawHoverTooltip()
        {
            if (string.IsNullOrEmpty(_hoverTooltip)) return;
            var ts = new GUIStyle(_labelStyle) { fontSize = 12, alignment = TextAnchor.UpperLeft };
            ts.normal.textColor = Color.white;
            var sz = ts.CalcSize(new GUIContent(_hoverTooltip));
            var mx = _lastHoverRect.x + _lastHoverRect.width / 2;
            var my = _lastHoverRect.y + _lastHoverRect.height + 5;
            var tw = sz.x + 16;
            var th = sz.y + 10;
            if (mx + tw > Screen.width) mx = Screen.width - tw - 10;
            if (my + th > Screen.height) my = _lastHoverRect.y - th - 5;
            GUI.DrawTexture(new Rect(mx, my, tw, th), Tex(new Color(0, 0, 0, 0.9f)));
            GUI.Label(new Rect(mx + 8, my + 5, sz.x, sz.y), _hoverTooltip, ts);
            _hoverTooltip = "";
        }

        private Texture2D GetRoundedBg(int w, int h)
        {
            var key = (w << 16) | h;
            Texture2D tex;
            if (_bgCache.TryGetValue(key, out tex)) return tex;
            tex = new Texture2D(w, h);
            var rad = Mathf.Min(6, w / 4, h / 4);
            var bg = new Color(0, 0, 0, _bgOpacity);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    var cx = x < rad ? rad - x : (x >= w - rad ? x - (w - rad - 1) : 0);
                    var cy = y < rad ? rad - y : (y >= h - rad ? y - (h - rad - 1) : 0);
                    tex.SetPixel(x, y, (cx > 0 && cy > 0 && cx * cx + cy * cy > rad * rad) ? Color.clear : bg);
                }
            tex.Apply();
            _bgCache[key] = tex;
            return tex;
        }

        private Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

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
            if (GUI.Button(new Rect(px + 20, py + 158, 48, 28), "保存", bs))
            {
                Config[CfgSec, CfgItem].BoxedValue = _itemOffsetY;
                Config[CfgSec, CfgSkill].BoxedValue = _skillOffsetY;
                Config[CfgSec, CfgShop].BoxedValue = _shopOffsetY;
                Config[CfgSec, CfgBg].BoxedValue = _bgOpacity;
                Config.Save();
            }
            if (GUI.Button(new Rect(px + 72, py + 158, 45, 28), _useChinese ? "中" : "EN", bs))
            {
                _useChinese = !_useChinese;
            }
            if (GUI.Button(new Rect(px + 122, py + 158, 55, 28), "重置", bs))
            {
                _itemOffsetY = 30f; _skillOffsetY = 20f; _shopOffsetY = 80f; _bgOpacity = 0.55f;
            }
        }

        // ==================== F8 捕获 ====================

        private void CaptureCurrentBoardAsBuild()
        {
            var items = GatherCurrentBoardItemNames();
            var skills = GatherPlayerSkillNames();
            if (items.Count == 0 && skills.Count == 0) return;
            var hero = string.IsNullOrEmpty(_captureHeroInput) ? _detectedHero : _captureHeroInput;
            _builds.Add(new BuildTemplate
            {
                HeroName = hero, BuildName = _captureBuildName,
                CoreItems = items, FlexItems = new List<string>(),
                CoreSkills = skills, FlexSkills = new List<string>()
            });
            SaveBuildsAtomic();
        }

        private void DrawCaptureDialog()
        {
            var w = 330f; var h = 200f;
            var x = (Screen.width - w) / 2; var y = (Screen.height - h) / 2;
            GUI.DrawTexture(new Rect(x, y, w, h), Tex(new Color(0.1f, 0.1f, 0.15f, 0.95f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 14, wordWrap = true };
            s.normal.textColor = Color.white;
            GUI.Label(new Rect(x + 15, y + 10, 300, 22), "捕获当前棋盘阵容", s);

            var boardItems = GatherCurrentBoardItemNames();
            var boardSkills = GatherPlayerSkillNames();
            s.fontSize = 12;
            GUI.Label(new Rect(x + 15, y + 35, 300, 40),
                string.Format("物品({0}): {1}\n技能({2}): {3}",
                    boardItems.Count, string.Join(", ", boardItems.ToArray()),
                    boardSkills.Count, string.Join(", ", boardSkills.ToArray())), s);

            s.fontSize = 13;
            GUI.Label(new Rect(x + 15, y + 85, 60, 22), "阵容名:", s);
            _captureBuildName = GUI.TextField(new Rect(x + 80, y + 85, 160, 22), _captureBuildName ?? "", 30);

            GUI.Label(new Rect(x + 15, y + 115, 60, 22), "英雄:", s);
            var hp = string.IsNullOrEmpty(_captureHeroInput) ? (string.IsNullOrEmpty(_detectedHero) ? "手动输入" : _detectedHero) : _captureHeroInput;
            _captureHeroInput = GUI.TextField(new Rect(x + 80, y + 115, 160, 22), hp, 30);

            var bs = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(x + 20, y + 150, 90, 28), "保存", bs))
            { if (!string.IsNullOrEmpty(_captureBuildName)) { CaptureCurrentBoardAsBuild(); _captureMode = false; } }
            if (GUI.Button(new Rect(x + 120, y + 150, 90, 28), "取消", bs)) _captureMode = false;
            if (GUI.Button(new Rect(x + 220, y + 150, 90, 28), "保存+管理", bs))
            { if (!string.IsNullOrEmpty(_captureBuildName)) { CaptureCurrentBoardAsBuild(); _captureMode = false; _showBuildManager = true; } }
        }

        // ==================== F9 推荐面板 ====================

        private void DrawRecommendationPanel()
        {
            var px = 10f; var py = 10f; var pw = 350f; var ph = Mathf.Min(400f, Screen.height - 20f);
            GUI.DrawTexture(new Rect(px, py, pw, ph), Tex(new Color(0, 0, 0, 0.85f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 13, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;

            var ts = new GUIStyle(s) { fontSize = 15, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(1f, 0.8f, 0.2f);
            GUI.Label(new Rect(px + 10, py + 5, pw - 50, 22), "阵容推荐 (F6)", ts);
            // 英雄按钮
            var heroes = CollectHeroNames();
            GUI.Label(new Rect(px + 10, py + 28, 30, 18), "英雄:", s);
            float hx = px + 42;
            foreach (var h in heroes)
            {
                var hbw = s.CalcSize(new GUIContent(h)).x + 10;
                if (hx + hbw > px + pw - 10) { hx = px + 10; /* would wrap but skip for now */ }
                var isActive = _detectedHero == h;
                var hbs = new GUIStyle(GUI.skin.button) { fontSize = 11 };
                if (isActive) hbs.normal.textColor = Color.yellow;
                if (GUI.Button(new Rect(hx, py + 26, hbw, 18), h, hbs))
                {
                    _detectedHero = (_detectedHero == h) ? "" : h;
                }
                hx += hbw + 2;
            }

            var currentItems = GatherAllItemNames();
            var shopItems = GatherShopItemNames();
            _matchResults = MatchBuilds(_detectedHero, currentItems);
            foreach (var mr in _matchResults)
            {
                mr.ShopCoreMatches = mr.CoreMissing.FindAll(n => shopItems.Contains(n));
                mr.ShopFlexMatches = mr.FlexMissing.FindAll(n => shopItems.Contains(n));
            }

            _recScroll = GUI.BeginScrollView(new Rect(px + 5, py + 50, pw - 10, ph - 60), _recScroll,
                new Rect(0, 0, pw - 30, _matchResults.Count * 90 + 10));

            float ry = 5;
            float barMaxW = pw - 95;
            for (int i = 0; i < _matchResults.Count && i < 10; i++)
            {
                var mr = _matchResults[i];
                var score = mr.MatchScore * 100f;

                var cs = new GUIStyle(s) { fontSize = 13 };
                cs.normal.textColor = (i == 0) ? new Color(1f, 0.8f, 0.2f) : Color.white;
                GUI.Label(new Rect(10, ry, pw - 30, 18),
                    string.Format("{0}. {1}", i + 1, Translate(mr.Template.BuildName)), cs);
                ry += 20;

                var barH = 16f;
                GUI.DrawTexture(new Rect(10, ry, barMaxW, barH), Tex(new Color(0.15f, 0.15f, 0.15f, 0.8f)));
                var barFillW = barMaxW * (score / 100f);
                if (barFillW > 0)
                {
                    var fc = score >= 70f ? new Color(0.1f, 0.9f, 0.2f) : score >= 40f ? new Color(0.8f, 0.8f, 0.1f) : new Color(0.8f, 0.3f, 0.1f);
                    GUI.DrawTexture(new Rect(10, ry, barFillW, barH), Tex(fc));
                }
                var ps2 = new GUIStyle(s) { fontSize = 12, alignment = TextAnchor.MiddleRight };
                ps2.normal.textColor = Color.white;
                GUI.Label(new Rect(10 + barMaxW, ry, 55, barH), string.Format("{0:F0}%", score), ps2);
                ry += barH + 4;

                if (mr.CoreMissing.Count > 0)
                    DrawMissingLine(15, ref ry, pw - 30, "缺核心: ", mr.CoreMissing, mr.ShopCoreMatches, new Color(1f, 0.3f, 0.3f), new Color(0.2f, 1f, 0.5f));
                if (mr.FlexMissing.Count > 0)
                    DrawMissingLine(15, ref ry, pw - 30, "缺灵活: ", mr.FlexMissing, mr.ShopFlexMatches, new Color(1f, 0.7f, 0.3f), new Color(0.5f, 1f, 0.5f));
                if (mr.SkillCoreMissing.Count > 0)
                    DrawMissingLine(15, ref ry, pw - 30, "缺技核: ", mr.SkillCoreMissing, new List<string>(), new Color(0.4f, 0.5f, 1f), new Color(0.5f, 1f, 0.5f));
                if (mr.SkillFlexMissing.Count > 0)
                    DrawMissingLine(15, ref ry, pw - 30, "缺技灵: ", mr.SkillFlexMissing, new List<string>(), new Color(0.5f, 0.6f, 1f), new Color(0.5f, 1f, 0.5f));
                ry += 5;
            }

            if (_matchResults.Count == 0)
            { s.normal.textColor = Color.gray; GUI.Label(new Rect(10, ry, 300, 18), "暂无匹配阵容 (F10添加)", s); }
            GUI.EndScrollView();

            if (GUI.Button(new Rect(px + 10, py + ph - 25, 55, 20), "F6捕获", GUI.skin.button)) _captureMode = true;
            if (GUI.Button(new Rect(px + 70, py + ph - 25, 55, 20), "F6管理", GUI.skin.button)) _showBuildManager = true;
            if (GUI.Button(new Rect(px + 130, py + ph - 25, 55, 20), "F5", GUI.skin.button))
            { try { ExportToJson(GatherBoardData()); } catch { } }
        }

        private void DrawMissingLine(float x, ref float y, float maxW, string prefix,
            List<string> missing, List<string> inShop, Color missingColor, Color shopColor)
        {
            var ms = new GUIStyle(_labelStyle) { fontSize = 11, alignment = TextAnchor.UpperLeft };
            ms.normal.textColor = missingColor;
            GUI.Label(new Rect(x, y, 55, 16), prefix, ms);
            float cx = x + 55;
            for (int i = 0; i < missing.Count; i++)
            {
                var item = missing[i];
                var disp = Translate(item);
                var inStore = inShop.Contains(item);
                if (inStore) ms.normal.textColor = new Color(shopColor.r, shopColor.g, shopColor.b, 0.6f + 0.4f * Mathf.Sin(Time.time * 5f));
                else ms.normal.textColor = missingColor;
                var itemText = (i < missing.Count - 1) ? disp + ", " : disp;
                var sz = ms.CalcSize(new GUIContent(itemText));
                GUI.Label(new Rect(cx, y, sz.x + 5, 16), itemText, ms);
                if (inStore)
                {
                    var ss = new GUIStyle(ms) { fontSize = 10 };
                    ss.normal.textColor = new Color(1f, 0.9f, 0.1f);
                    GUI.Label(new Rect(cx + sz.x - 2, y - 2, 20, 16), "★", ss);
                }
                cx += sz.x + 2;
                if (cx > x + maxW - 30) { cx = x + 55; y += 16; }
            }
            y += 16;
        }

        // ==================== F10 阵容管理 ====================

        private void DrawBuildManagerPanel()
        {
            var px = 10f; var py = Screen.height - 410f; var pw = 380f; var ph = 400f;
            if (py < 10) py = 10;
            GUI.DrawTexture(new Rect(px, py, pw, ph), Tex(new Color(0.05f, 0.05f, 0.1f, 0.95f)));

            var s = new GUIStyle(_labelStyle) { fontSize = 12, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;
            var ts = new GUIStyle(s) { fontSize = 14, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(0.3f, 0.8f, 1f);

            GUI.Label(new Rect(px + 10, py + 5, pw - 90, 22), "阵容管理 (F6)", ts);
            if (GUI.Button(new Rect(px + pw - 80, py + 5, 70, 20), "导入社区"))
            {
                ImportCommunityBuilds();
            }
            GUI.Label(new Rect(px + 10, py + 28, pw - 20, 18),
                string.Format("英雄: {0} | {1} 阵容", string.IsNullOrEmpty(_detectedHero) ? "?" : _detectedHero, _builds.Count), s);

            var heroBuilds = string.IsNullOrEmpty(_detectedHero)
                ? _builds : _builds.FindAll(b => b.HeroName == _detectedHero || string.IsNullOrEmpty(b.HeroName));
            if (heroBuilds.Count == 0) heroBuilds = _builds;

            float listY = py + 48;
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
                        BuildName = _newBuildNameInput
                    });
                    SaveBuildsAtomic();
                    _newBuildNameInput = ""; _newBuildHeroInput = "";
                }
            }
            listY += 26;

            float scrollH = ph - (listY - py) - 30;
            _buildListScroll = GUI.BeginScrollView(new Rect(px + 5, listY, pw - 15, scrollH), _buildListScroll,
                new Rect(0, 0, pw - 35, heroBuilds.Count * 200 + 10));

            float ry = 5;
            foreach (var build in heroBuilds)
            {
                var isSelected = _selectedBuildForEdit == build.BuildName;
                var expandH = isSelected ? 330f : 22f;
                GUI.DrawTexture(new Rect(0, ry, pw - 35, expandH),
                    Tex(isSelected ? new Color(0.15f, 0.15f, 0.25f, 0.7f) : new Color(0.1f, 0.1f, 0.15f, 0.5f)));

                GUI.Label(new Rect(5, ry + 2, 120, 18), string.Format("{0} ({1})", Translate(build.BuildName), build.HeroName), s);
                if (GUI.Button(new Rect(250, ry + 1, 35, 18), isSelected ? "收起" : "编辑"))
                    _selectedBuildForEdit = isSelected ? "" : build.BuildName;
                if (GUI.Button(new Rect(290, ry + 1, 35, 18), "删除"))
                { _builds.Remove(build); SaveBuildsAtomic(); _selectedBuildForEdit = ""; break; }

                if (isSelected)
                {
                    var ns = new GUIStyle(s) { fontSize = 11 };
                    ns.normal.textColor = new Color(1f, 0.5f, 0.5f);
                    GUI.Label(new Rect(15, ry + 26, 330, 16), "核心物品:", ns);
                    float iy = ry + 42;
                    DrawItemRow(build.CoreItems, build.FlexItems, ref iy, ns);
                    ns.normal.textColor = new Color(1f, 0.7f, 0.3f);
                    GUI.Label(new Rect(15, iy, 330, 16), "灵活物品:", ns);
                    iy += 16;
                    DrawItemRowReverse(build.FlexItems, build.CoreItems, ref iy, ns);

                    GUI.Label(new Rect(15, iy, 50, 18), "加物:", s);
                    _newItemInput = GUI.TextField(new Rect(55, iy, 140, 20), _newItemInput ?? "", 30);
                    if (GUI.Button(new Rect(198, iy, 35, 20), "核心"))
                    { if (!string.IsNullOrEmpty(_newItemInput) && !build.CoreItems.Contains(_newItemInput)) { build.CoreItems.Add(_newItemInput); SaveBuildsAtomic(); _newItemInput = ""; } }
                    if (GUI.Button(new Rect(236, iy, 35, 20), "灵活"))
                    { if (!string.IsNullOrEmpty(_newItemInput) && !build.FlexItems.Contains(_newItemInput)) { build.FlexItems.Add(_newItemInput); SaveBuildsAtomic(); _newItemInput = ""; } }
                    iy += 22;

                    ns.normal.textColor = new Color(0.4f, 0.5f, 1f);
                    GUI.Label(new Rect(15, iy, 330, 16), "核心技能:", ns);
                    iy += 16;
                    DrawItemRow(build.CoreSkills, build.FlexSkills, ref iy, ns);
                    ns.normal.textColor = new Color(0.5f, 0.6f, 1f);
                    GUI.Label(new Rect(15, iy, 330, 16), "灵活技能:", ns);
                    iy += 16;
                    DrawItemRowReverse(build.FlexSkills, build.CoreSkills, ref iy, ns);

                    GUI.Label(new Rect(15, iy, 50, 18), "加技:", s);
                    _newItemInput = GUI.TextField(new Rect(55, iy, 140, 20), _newItemInput ?? "", 30);
                    if (GUI.Button(new Rect(198, iy, 35, 20), "核心"))
                    { if (!string.IsNullOrEmpty(_newItemInput) && !build.CoreSkills.Contains(_newItemInput)) { build.CoreSkills.Add(_newItemInput); SaveBuildsAtomic(); _newItemInput = ""; } }
                    if (GUI.Button(new Rect(236, iy, 35, 20), "灵活"))
                    { if (!string.IsNullOrEmpty(_newItemInput) && !build.FlexSkills.Contains(_newItemInput)) { build.FlexSkills.Add(_newItemInput); SaveBuildsAtomic(); _newItemInput = ""; } }
                }
                ry += expandH + 5;
            }
            GUI.EndScrollView();
        }

        private void DrawItemRow(List<string> src, List<string> dst, ref float iy, GUIStyle ns)
        {
            for (int i = 0; i < src.Count; i++)
            {
                GUI.Label(new Rect(25, iy, 160, 16), Translate(src[i]), ns);
                if (GUI.Button(new Rect(195, iy, 25, 15), "↓")) { dst.Add(src[i]); src.RemoveAt(i); SaveBuildsAtomic(); }
                if (GUI.Button(new Rect(222, iy, 25, 15), "X")) { src.RemoveAt(i); SaveBuildsAtomic(); }
                iy += 17;
            }
        }

        private void DrawItemRowReverse(List<string> src, List<string> dst, ref float iy, GUIStyle ns)
        {
            for (int i = 0; i < src.Count; i++)
            {
                GUI.Label(new Rect(25, iy, 160, 16), Translate(src[i]), ns);
                if (GUI.Button(new Rect(195, iy, 25, 15), "↑")) { dst.Add(src[i]); src.RemoveAt(i); SaveBuildsAtomic(); }
                if (GUI.Button(new Rect(222, iy, 25, 15), "X")) { src.RemoveAt(i); SaveBuildsAtomic(); }
                iy += 17;
            }
        }

        // ==================== 标签收集 ====================

        private void RefreshOverlayLabels()
        {
            _itemLabels.Clear(); _skillLabels.Clear(); _shopLabels.Clear();
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
                                var subText = GetCritText(cd);
                                _itemLabels.Add(new OverlayLabel { Text = GetCardName(cd), Tier = cd.Tier.ToString(), ScreenPos = sp, SubText = subText });
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
                    // 排除玩家角色框 (Encounter_Frame_xxx_PV)
                    if (t.name.Contains("_Frame_") || t.name.Contains("_PV")) continue;
                    var sp = cam.WorldToScreenPoint(t.position);
                    sp.y -= _shopOffsetY;
                    var shopName = t.name;
                    if (shopName.EndsWith("(Clone)")) shopName = shopName.Substring(0, shopName.Length - 7);
                    var rating = RateShop(shopName);
                    var subText = "";
                    var hoverData = "";
                    if (rating != null)
                    {
                        var parts = rating.Split('|');
                        subText = parts[0];
                        hoverData = parts.Length > 1 ? parts[1] : "";
                    }
                    _shopLabels.Add(new OverlayLabel { Text = Translate(shopName), Tier = "Invalid", ScreenPos = sp, SubText = subText, HoverData = hoverData });
                }
            }
            catch { }
        }

        private string GetCritText(BazaarGameClient.Domain.Models.Cards.Card cd)
        {
            try
            {
                if (cd.Attributes != null)
                {
                    foreach (var kv in cd.Attributes)
                    {
                        if (kv.Key.ToString().ToLower().Contains("crit") && kv.Value != 0)
                            return kv.Value + "%";
                    }
                }
            }
            catch { }
            return null;
        }

        private bool IsPlayerItem(Transform t)
        {
            while (t != null)
            {
                if (t.GetComponent<BoardManager>() != null) return true;
                t = t.parent;
            }
            return false;
        }

        private string GetCardName(BazaarGameClient.Domain.Models.Cards.Card c)
        {
            try
            {
                if (c.Template != null && !string.IsNullOrEmpty(c.Template.InternalName))
                    return Translate(c.Template.InternalName);
                if (!string.IsNullOrEmpty(c.Name)) return Translate(c.Name);
            }
            catch { }
            return "???";
        }

        // ==================== 英雄检测 ====================

        private List<string> CollectHeroNames()
        {
            var heroes = new HashSet<string>();
            foreach (var b in _builds)
                if (!string.IsNullOrEmpty(b.HeroName)) heroes.Add(b.HeroName);
            // 加入社区阵容英雄
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "community_builds.json");
                if (File.Exists(path))
                {
                    var cb = JsonConvert.DeserializeObject<Dictionary<string, CommunityBuild>>(File.ReadAllText(path, Encoding.UTF8));
                    if (cb != null)
                        foreach (var kv in cb)
                            if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.hero))
                                heroes.Add(kv.Value.hero);
                }
            }
            catch { }
            var list = new List<string>(heroes);
            list.Sort();
            return list;
        }

        private string DetectHero()
        {
            try
            {
                var rmType = typeof(BoardManager).Assembly.GetType("TheBazaar.RunManager");
                if (rmType != null)
                {
                    var ip = rmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ip != null)
                    {
                        var inst = ip.GetValue(null, null);
                        if (inst != null)
                        {
                            foreach (var pn in new[] { "Hero", "HeroName", "SelectedHero", "CurrentHero" })
                            {
                                var p = rmType.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p != null) { var v = p.GetValue(inst, null); if (v != null && !string.IsNullOrEmpty(v.ToString())) return v.ToString(); }
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        // ==================== 物品收集 ====================

        private List<string> GatherCurrentBoardItemNames()
        {
            var result = new List<KeyValuePair<float, string>>();
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
                            if (!IsPlayerItem(cc.transform)) continue;
                            var name = GetCardNameStatic(cd);
                            if (string.IsNullOrEmpty(name) || name == "???") continue;
                            if (_isPlayerBoardProp != null)
                            {
                                try { if (!((bool)_isPlayerBoardProp.GetValue(cc, null))) continue; }
                                catch { }
                            }
                            result.Add(new KeyValuePair<float, string>(cc.transform.position.x, name));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            result.Sort((a, b) => a.Key.CompareTo(b.Key));
            var names = new List<string>();
            foreach (var kv in result) names.Add(kv.Value);
            return names;
        }

        private List<string> GatherAllItemNames()
        {
            var result = new List<string>();
            // 优先 Harmony 追踪
            lock (TrackedCards)
            {
                foreach (var kv in TrackedCards)
                {
                    var tc = kv.Value;
                    if (tc.IsPlayer && tc.Type == "Item") result.Add(BoardCardName(tc.Name));
                }
            }
            // 回退
            if (result.Count == 0)
            {
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
                                if (!IsPlayerItem(cc.transform)) continue;
                                var name = GetCardNameStatic(cd);
                                if (!string.IsNullOrEmpty(name) && name != "???") result.Add(name);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        private string BoardCardName(string english)
        {
            // 返回英文名用于阵容匹配
            if (string.IsNullOrEmpty(english)) return "";
            // GetCardName 返回翻译后的，这里需要英文原名用于匹配
            // Harmony 存储的是 Template.InternalName（英文）
            return english;
        }

        private List<string> GatherShopItemNames()
        {
            var result = new List<string>();
            var owned = new HashSet<string>(GatherAllItemNames());
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
                            if (IsPlayerItem(cc.transform)) continue;
                            var name = GetCardNameStatic(cd);
                            if (string.IsNullOrEmpty(name) || name == "???") continue;
                            if (!owned.Contains(name)) result.Add(name);
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
                    _builds = JsonConvert.DeserializeObject<List<BuildTemplate>>(File.ReadAllText(_buildsPath, Encoding.UTF8)) ?? new List<BuildTemplate>();
            }
            catch { _builds = new List<BuildTemplate>(); }
        }

        private void SaveBuildsAtomic()
        {
            try { WriteJsonAtomic(_buildsPath, JsonConvert.SerializeObject(_builds, Formatting.Indented)); }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 保存失败: {0}", ex)); }
        }

        private void ImportCommunityBuilds()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "community_builds.json");
                if (!File.Exists(path))
                {
                    _logger.LogWarning("[BoardReader] 未找到 community_builds.json");
                    return;
                }
                var json = File.ReadAllText(path, Encoding.UTF8);
                var builds = JsonConvert.DeserializeObject<Dictionary<string, CommunityBuild>>(json);
                if (builds == null) return;
                int imported = 0;
                foreach (var kv in builds)
                {
                    var cb = kv.Value;
                    if (cb == null) continue;
                    // 检查是否已存在同名
                    if (_builds.Exists(b => b.BuildName == cb.display_name)) continue;
                    var bt = new BuildTemplate
                    {
                        HeroName = cb.hero ?? "",
                        BuildName = cb.display_name ?? kv.Key,
                        CoreItems = cb.core_cards ?? new List<string>(),
                        FlexItems = (cb.transition_cards ?? new List<string>())
                            .Concat(cb.optional_cards ?? new List<string>()).ToList(),
                        CoreSkills = new List<string>(),
                        FlexSkills = new List<string>()
                    };
                    _builds.Add(bt);
                    imported++;
                }
                SaveBuildsAtomic();
                _logger.LogInfo(string.Format("[BoardReader] 导入社区阵容: {0} 个", imported));
            }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 导入失败: {0}", ex)); }
        }

        private static void WriteJsonAtomic(string path, string json)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private List<BuildMatchResult> MatchBuilds(string heroName, List<string> currentItems)
        {
            var results = new List<BuildMatchResult>();
            var ownedSet = new HashSet<string>(currentItems);
            var ownedSkills = GatherPlayerSkillNames();
            var skillSet = new HashSet<string>(ownedSkills);

            foreach (var build in _builds)
            {
                if (!string.IsNullOrEmpty(heroName) && !string.IsNullOrEmpty(build.HeroName) && build.HeroName != heroName)
                    continue;

                var co = new List<string>(); var cm = new List<string>();
                foreach (var item in build.CoreItems) { if (ownedSet.Contains(item)) co.Add(item); else cm.Add(item); }
                var fo = new List<string>(); var fm = new List<string>();
                foreach (var item in build.FlexItems) { if (ownedSet.Contains(item)) fo.Add(item); else fm.Add(item); }
                var sco = new List<string>(); var scm = new List<string>();
                foreach (var s in build.CoreSkills) { if (skillSet.Contains(s)) sco.Add(s); else scm.Add(s); }
                var sfo = new List<string>(); var sfm = new List<string>();
                foreach (var s in build.FlexSkills) { if (skillSet.Contains(s)) sfo.Add(s); else sfm.Add(s); }

                float ics = build.CoreItems.Count > 0 ? (float)co.Count / build.CoreItems.Count : 0f;
                float ifs = build.FlexItems.Count > 0 ? (float)fo.Count / build.FlexItems.Count : 0f;
                float scs = build.CoreSkills.Count > 0 ? (float)sco.Count / build.CoreSkills.Count : 0f;
                float sfs = build.FlexSkills.Count > 0 ? (float)sfo.Count / build.FlexSkills.Count : 0f;

                float it = ics * 0.7f + ifs * 0.3f;
                float st = scs * 0.7f + sfs * 0.3f;
                float ti = build.CoreItems.Count + build.FlexItems.Count;
                float ts2 = build.CoreSkills.Count + build.FlexSkills.Count;
                float tw = ti + ts2;
                float totalScore = tw > 0 ? (it * ti + st * ts2) / tw : 0f;

                results.Add(new BuildMatchResult
                {
                    Template = build, MatchScore = totalScore,
                    CoreOwned = co, CoreMissing = cm, FlexOwned = fo, FlexMissing = fm,
                    SkillCoreMissing = scm, SkillFlexMissing = sfm
                });
            }
            results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
            return results;
        }

        private List<string> GatherPlayerSkillNames()
        {
            var result = new List<string>();
            try
            {
                if (_skillListField != null)
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
                                    var name = GetCardNameStatic(r.Card);
                                    if (!string.IsNullOrEmpty(name) && name != "???") result.Add(name);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        // ==================== 翻译 ====================

        private void LoadTranslations()
        {
            // 方案1：从游戏 SQLite 缓存读取（永远最新）
            try
            {
                var localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                localLow = localLow.Substring(0, localLow.Length - 7) + "Low"; // AppData\Local → LocalLow
                var dbPath = Path.Combine(localLow, "Tempo Storm", "The Bazaar", "prod", "cache", "translations", "zh-CN.bytes");
                if (File.Exists(dbPath))
                {
                    var conn = new Mono.Data.Sqlite.SqliteConnection("URI=file:" + dbPath);
                    conn.Open();
                    var cmd = conn.CreateCommand();
                    // 查表结构
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    var tableName = "";
                    using (var r = cmd.ExecuteReader()) { if (r.Read()) tableName = r.GetString(0); }
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        // 查列结构
                        var cols = new List<string>();
                        cmd.CommandText = "PRAGMA table_info(" + tableName + ")";
                        using (var r = cmd.ExecuteReader())
                        { while (r.Read()) cols.Add(r.GetString(1)); }

                        // 找到 key/value 对应的列名
                        var keyCol = cols.Find(c => c.ToLower().Contains("key") || c.ToLower().Contains("source") || c.ToLower().Contains("string"));
                        var valCol = cols.Find(c => c.ToLower().Contains("value") || c.ToLower().Contains("text") || c.ToLower().Contains("translation") || c.ToLower().Contains("target"));
                        if (keyCol == null) keyCol = cols[0];
                        if (valCol == null) valCol = cols[cols.Count - 1];

                        cmd.CommandText = string.Format("SELECT [{0}], [{1}] FROM {2}", keyCol, valCol, tableName);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                var k = r.GetValue(0);
                                var v = r.GetValue(1);
                                if (k != null && v != null && !DBNull.Value.Equals(k) && !DBNull.Value.Equals(v))
                                    _translations[k.ToString()] = v.ToString();
                            }
                        }
                    }
                    conn.Close();
                    if (_translations.Count > 100)
                    {
                        _logger.LogInfo(string.Format("[BoardReader] SQLite翻译: {0} 条", _translations.Count));
                        return;
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(string.Format("[BoardReader] SQLite: {0}", ex)); }

            // 方案2：JSON 文件回退
            try
            {
                var dir = Path.GetDirectoryName(typeof(BazaarBoardReaderPlugin).Assembly.Location);
                var path = Path.Combine(dir, "translations_zh_cn.json");
                if (!File.Exists(path))
                    path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "translations_zh_cn.json");
                if (File.Exists(path))
                {
                    var wrapper = JsonConvert.DeserializeObject<TranslationData>(File.ReadAllText(path, Encoding.UTF8));
                    if (wrapper != null && wrapper.by_name != null)
                    { _translations = wrapper.by_name; _logger.LogInfo(string.Format("[BoardReader] JSON翻译: {0} 条", _translations.Count)); }
                }
            }
            catch { }
        }

        // ==================== 商店推荐 ====================

        private void LoadShopRecommendationData()
        {
            try
            {
                var dataDir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
                // 加载 events.json
                var eventsPath = Path.Combine(dataDir, "events.json");
                if (File.Exists(eventsPath))
                {
                    var json = File.ReadAllText(eventsPath, Encoding.UTF8);
                    // events.json 结构: { "shops": [...], "skill_shops": [...], ... }
                    var wrapper = JsonConvert.DeserializeObject<Dictionary<string, List<EventShopEntry>>>(json);
                    if (wrapper != null && wrapper.ContainsKey("shops"))
                    {
                        foreach (var shop in wrapper["shops"])
                        {
                            if (!string.IsNullOrEmpty(shop.name))
                                _shopData[shop.name.ToLower()] = shop;
                        }
                    }
                }
                // 加载 cards_generated.json (只取需要的字段)
                var cardsPath = Path.Combine(dataDir, "cards_generated.json");
                if (File.Exists(cardsPath))
                {
                    var cardsJson = File.ReadAllText(cardsPath, Encoding.UTF8);
                    var cardsDict = JsonConvert.DeserializeObject<Dictionary<string, CardDbEntry>>(cardsJson);
                    if (cardsDict != null)
                    {
                        foreach (var kv in cardsDict)
                        {
                            if (kv.Value != null)
                                _cardDb[kv.Key.ToLower()] = kv.Value;
                        }
                    }
                }
                _logger.LogInfo(string.Format("[BoardReader] 商店数据: {0}店 {1}卡", _shopData.Count, _cardDb.Count));
            }
            catch (Exception ex) { _logger.LogWarning(string.Format("[BoardReader] 商店数据加载失败: {0}", ex)); }
        }

        private string RateShop(string shopName)
        {
            if (_shopData.Count == 0 || _cardDb.Count == 0) return null;
            var key = shopName.ToLower();
            EventShopEntry shop;
            if (!_shopData.TryGetValue(key, out shop)) return null;
            if (shop.shop_pool == null || shop.shop_pool.reward_tags == null) return null;

            // 收集当前阵容缺失的核心物品
            var missingCore = new HashSet<string>();
            foreach (var b in _builds)
            {
                if (!string.IsNullOrEmpty(_detectedHero) && !string.IsNullOrEmpty(b.HeroName) && b.HeroName != _detectedHero) continue;
                foreach (var item in b.CoreItems) missingCore.Add(item.ToLower());
                foreach (var item in b.CoreSkills) missingCore.Add(item.ToLower());
            }
            // 去掉已拥有的
            var owned = new HashSet<string>();
            foreach (var n in GatherAllItemNames()) owned.Add(n.ToLower());
            foreach (var n in GatherPlayerSkillNames()) owned.Add(n.ToLower());
            missingCore.RemoveWhere(n => owned.Contains(n));

            if (missingCore.Count == 0) return null;

            // 构建商店卡池
            var poolCards = new List<string>();
            foreach (var tag in shop.shop_pool.reward_tags)
            {
                foreach (var kv in _cardDb)
                {
                    if (kv.Value.tags != null && kv.Value.tags.Contains(tag))
                        poolCards.Add(kv.Key);
                }
            }
            if (poolCards.Count == 0) return null;

            // 计算核心命中
            var hits = new List<string>();
            foreach (var card in poolCards)
            {
                if (missingCore.Contains(card)) hits.Add(card);
            }

            if (hits.Count == 0) return "不推荐";
            float hitRate = (float)hits.Count / missingCore.Count;
            if (hitRate >= 0.3f) return "推荐|命中:" + string.Join(",", hits.ToArray());
            if (hitRate >= 0.1f) return "一般|命中:" + string.Join(",", hits.ToArray());
            return "不推荐";
        }

        // ==================== 翻译 ====================

        private string Translate(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            if (!_useChinese) return english;
            string chinese;
            if (_translations.TryGetValue(english, out chinese)) return chinese;
            // 循环去掉 [Karnok Unique] [Crash Site Expedition] 等方括号前缀
            var stripped = english;
            while (stripped.StartsWith("[") && stripped.IndexOf(']') > 0)
            {
                stripped = stripped.Substring(stripped.IndexOf(']') + 1).TrimStart();
                if (_translations.TryGetValue(stripped, out chinese)) return chinese;
            }
            // 去掉括号后缀 (Gold) (Silver) 等
            var parenIdx = english.IndexOf('(');
            if (parenIdx > 0)
            {
                var s2 = english.Substring(0, parenIdx).TrimEnd();
                if (_translations.TryGetValue(s2, out chinese)) return chinese;
            }
            return english;
        }

        // ==================== JSON 导出 ====================

        private BoardData GatherBoardData()
        {
            var d = new BoardData
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BoardItems = new List<CardInfo>(), StorageItems = new List<CardInfo>(),
                SkillCards = new List<CardInfo>(), Shops = new List<ShopInfo>()
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
                        d.Shops.Add(new ShopInfo { GameObjectName = t.name, DisplayName = Translate(t.name), Position = string.Format("{0:F1},{1:F1},{2:F1}", t.position.x, t.position.y, t.position.z) });
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
            WriteJsonAtomic(p, JsonConvert.SerializeObject(d, Formatting.Indented));
            _logger.LogInfo(string.Format("[BoardReader] 导出: 物品={0} 仓库={1} 技能={2} 商店={3}",
                d.BoardItems.Count, d.StorageItems.Count, d.SkillCards.Count, d.Shops.Count));
        }
    }

    // ==================== 数据类 ====================

    internal class OverlayLabel { public string Text; public string Tier; public Vector3 ScreenPos; public string SubText; public string HoverData; }

    public class TrackedCard { public string Name; public string Type; public bool IsPlayer; public float LastSeen; }

    [Serializable]
    public class EventShopPool { public List<string> reward_tags; public string match_mode; public string rarity_rule; public List<string> excluded_tags; public string hero_scope; }
    [Serializable]
    public class EventShopEntry { public string name; public string shop_type; public List<string> event_heroes; public EventShopPool shop_pool; }
    [Serializable]
    public class EventsData { public List<EventShopEntry> shops; }
    public class CardDbEntry { public string internal_name; public string type; public string hero; public List<string> heroes; public List<string> tags; public string size; public string rarity; }
    public class CommunityBuild { public string hero; public string display_name; public List<string> core_cards; public List<string> transition_cards; public List<string> optional_cards; }

    [Serializable]
    public class BuildTemplate
    {
        public string HeroName = "";
        public string BuildName = "";
        public List<string> CoreItems = new List<string>();
        public List<string> FlexItems = new List<string>();
        public List<string> CoreSkills = new List<string>();
        public List<string> FlexSkills = new List<string>();
    }

    public class BuildMatchResult
    {
        public BuildTemplate Template;
        public float MatchScore;
        public List<string> CoreOwned = new List<string>();
        public List<string> CoreMissing = new List<string>();
        public List<string> FlexOwned = new List<string>();
        public List<string> FlexMissing = new List<string>();
        public List<string> SkillCoreMissing = new List<string>();
        public List<string> SkillFlexMissing = new List<string>();
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
    public class TranslationData
    {
        public Dictionary<string, string> by_name;
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
