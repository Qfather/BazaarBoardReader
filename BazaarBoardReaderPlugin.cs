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
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BazaarBoardReader
{
    // 拦截所有 NetMessage 获取游戏状态
    [HarmonyPatch]
    public static class NetMessagePatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var pt = AccessTools.TypeByName("TheBazaar.NetMessageProcessor");
            if (pt == null) { try { BazaarBoardReaderPlugin._logger.LogInfo("[BoardReader] NetMessageProcessor type not found!"); } catch { } yield break; }
            var methods = pt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int count = 0;
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (m.Name == "Handle" && ps.Length == 1 && (ps[0].ParameterType.FullName ?? "").IndexOf("NetMessage", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                    yield return m;
                }
            }
            try { BazaarBoardReaderPlugin._logger.LogInfo(string.Format("[BoardReader] NetMessagePatch: found {0} Handle methods", count)); } catch { }
        }

        public static void Prefix(object __0)
        {
            if (__0 == null) return;
            try {
                var dataProp = __0.GetType().GetProperty("Data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (dataProp != null) {
                    var data = dataProp.GetValue(__0, null);
                    if (data != null && StateProbeLooksLikeGameState(data)) {
                        BazaarBoardReaderPlugin.LatestGameStateDto = data;
                        BazaarBoardReaderPlugin.GameStateDirty = true;
                        BazaarBoardReaderPlugin._logger.LogInfo("[BoardReader] Captured GameStateSync!");
                    }
                }
            } catch { }
        }

        private static bool StateProbeLooksLikeGameState(object dto)
        {
            if (dto == null) return false;
            var run = GetFieldRefl(dto, "Run");
            var player = GetFieldRefl(dto, "Player");
            if (run == null || player == null) return false;
            var hero = GetFieldRefl(player, "Hero");
            return hero != null && !string.IsNullOrEmpty(hero.ToString());
        }

        private static object GetFieldRefl(object target, string name)
        {
            if (target == null) return null;
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(target);
        }
    }

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
                    var tier = cd.Tier.ToString();
                    BazaarBoardReaderPlugin.TrackedCards[id] = new TrackedCard
                    { Name = name, Type = typeStr, IsPlayer = isPlayer, LastSeen = Time.time, Tier = tier };
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
        private const string PluginVersion = "7.6.5";

        internal static ManualLogSource _logger;
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
        private bool _showItemSkillLabels = true;
        private bool _showShopEventLabels = true;
        private bool _settingsCollapsed = true;
        private bool _recCollapsed;  // 推荐面板收起
        private bool _mgrCollapsed;
        private bool _hideOtherBuilds = true;  // 管理面板收起

        private float _lastRefreshTime;
        private float _itemOffsetY = 100f;
        private float _skillOffsetY = 30f;
        private float _shopOffsetY = 80f;
        private float _bgOpacity = 0.55f;
        private readonly List<OverlayLabel> _itemLabels = new List<OverlayLabel>();
        private readonly List<OverlayLabel> _skillLabels = new List<OverlayLabel>();
        private readonly List<OverlayLabel> _shopLabels = new List<OverlayLabel>();
        private GUIStyle _labelStyle;
        private GUIStyle _shopStyle;
        private readonly Dictionary<int, Texture2D> _bgCache = new Dictionary<int, Texture2D>();
        private const float RecommendationRefreshInterval = 0.5f;
        private float _lastRecommendationCalcTime = -999f;
        private int _lastRecommendationCalcFrame = -1;
        private string _lastRecommendationCalcHero = "";
        private string _lastRecommendationCalcBuild = "";
        private int _lastRecommendationCalcBuildsVersion = -1;
        private BuildTemplate _bestBuildCache;
        private int _bestBuildCacheFrame = -1;
        private string _bestBuildCacheHero = "";
        private string _bestBuildCacheBuild = "";
        private int _bestBuildCacheBuildsVersion = -1;
        private int _buildsVersion = 0;

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
        private int _currentDay = -1;
        // 物品/技能等级随天数变化：等级→最早出现天数
        private static readonly Dictionary<string, int> TierMinDay = new Dictionary<string, int>
        {
            {"Bronze", 1}, {"Silver", 2}, {"Gold", 6}, {"Diamond", 8}, {"Legendary", 99}
        };
        // 卡牌英文名→可用的等级列表（从 cards.json 加载）
        private Dictionary<string, List<string>> _cardNameToTiers = new Dictionary<string, List<string>>();
        // template_id → internal_name 映射（从 cards_generated.json 加载，用于读取 game_state.json）
        private Dictionary<string, string> _templateIdToName = new Dictionary<string, string>();
        // 已拥有物品缓存（内存持久化，不会因关背包而丢失）
        private Dictionary<string, string> _ownedItemsCache = new Dictionary<string, string>();
        private Dictionary<string, string> _ownedBoardItemsCache = new Dictionary<string, string>();
        private Dictionary<string, string> _ownedStorageItemsCache = new Dictionary<string, string>();
        private Dictionary<string, string> _lastLiveBoardItems = new Dictionary<string, string>();
        private Dictionary<string, string> _lastLiveStorageItems = new Dictionary<string, string>();
        private List<KeyValuePair<string, string>> _ownedItemsResultCache = new List<KeyValuePair<string, string>>();
        private int _ownedItemsResultCacheFrame = -1;
        private bool _lastLiveScanSawStorage;
        private bool _backpackOpen;
        private int _currentGold = 0;
        private int _currentIncome = 0;
        private int _currentHealth = 0;
        private int _currentPrestige = 0;
        // 游戏状态 DTO（来自网络消息拦截）
        internal static object LatestGameStateDto;
        internal static bool GameStateDirty;
        private string _selectedBuildName = ""; // 用户手动选择的阵容名
        private string _buildsPath;
        private Vector2 _buildListScroll;
        private Vector2 _recScroll;
        private string _captureBuildName = "";
        private string _captureHeroInput = "";
        private string _captureItemsInput = "";
        private string _captureSkillsInput = "";
        private bool _captureMode;
        private string _newItemInput = "";
        private string _selectedBuildForEdit = "";

        private Dictionary<string, string> _translations = new Dictionary<string, string>();
        private Dictionary<string, string> _reverseTranslations = new Dictionary<string, string>(); // 中文→英文
        private bool _useChinese = true;
        // 商店推荐
        private Dictionary<string, MerchantEntry> _merchants = new Dictionary<string, MerchantEntry>();
        private Dictionary<string, CardDataEntry> _cardDb = new Dictionary<string, CardDataEntry>();

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
        private const string CfgHero = "SelectedHero";
        private const string CfgRecX = "RecPanelX";
        private const string CfgRecY = "RecPanelY";
        private const string CfgMgrX = "MgrPanelX";
        private const string CfgMgrY = "MgrPanelY";
        private const string CfgSettingsX = "SettingsPanelX";
        private const string CfgSettingsY = "SettingsPanelY";
        private const string CfgShowItemSkill = "ShowItemSkillLabels";
        private const string CfgShowShopEvent = "ShowShopEventLabels";
        private const string CfgPanelAlpha = "PanelAlpha";
        private const string CfgBuild = "SelectedBuild";
        private const string CfgSecGeneral = "General";
        private float _recPanelX, _recPanelY, _mgrPanelX, _mgrPanelY, _settingsPanelX, _settingsPanelY;
        private float _panelAlpha = 0.4f;
        // Ctrl+拖动窗口
        private bool _draggingRec, _draggingMgr, _draggingSettings;
        private float _dragStartMouseX, _dragStartMouseY;
        private float _dragStartPanelX, _dragStartPanelY;

        private void Awake()
        {
            _logger = Logger;
            Config.Bind(CfgSec, CfgItem, 100f);
            Config.Bind(CfgSec, CfgSkill, 30f);
            Config.Bind(CfgSec, CfgShop, 80f);
            Config.Bind(CfgSec, CfgBg, 0.55f);
            _itemOffsetY = 100f;
            _skillOffsetY = 30f;
            _shopOffsetY = 80f;
            _bgOpacity = 0.55f;
            _overlayEnabled = Config.Bind(CfgSecGeneral, CfgOverlay, true).Value;
            _detectedHero = Config.Bind(CfgSecGeneral, CfgHero, "").Value;
            _recPanelX = Config.Bind(CfgSec, CfgRecX, 50f).Value;
            _recPanelY = Config.Bind(CfgSec, CfgRecY, 50f).Value;
            _mgrPanelX = Config.Bind(CfgSec, CfgMgrX, 50f).Value;
            _mgrPanelY = Config.Bind(CfgSec, CfgMgrY, 470f).Value;
            _settingsPanelX = Config.Bind(CfgSec, CfgSettingsX, 16f).Value;
            _settingsPanelY = Config.Bind(CfgSec, CfgSettingsY, 1021f).Value;
            _showItemSkillLabels = Config.Bind(CfgSecGeneral, CfgShowItemSkill, true).Value;
            _showShopEventLabels = Config.Bind(CfgSecGeneral, CfgShowShopEvent, true).Value;
            Config.Bind(CfgSec, CfgPanelAlpha, 0.4f);
            _panelAlpha = 0.4f;
            _selectedBuildName = Config.Bind(CfgSecGeneral, CfgBuild, "").Value;
            // 默认全部开启
            _showSliders = true;
            _showRecommendations = true;
            _showBuildManager = true;
            _mgrCollapsed = true;
            _logger.LogInfo(string.Format("[BoardReader] v7.6.5 物品={0} 技能={1} 商店={2} 背景={3:F0}%",
                (int)_itemOffsetY, (int)_skillOffsetY, (int)_shopOffsetY, _bgOpacity * 100f));

            _labelStyle = new GUIStyle { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
            _shopStyle = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };

            _playerCardsOnBoardField = typeof(BoardManager).GetField("_playerCardsOnBoard", BindingFlags.Instance | BindingFlags.NonPublic);
            _isPlayerBoardProp = typeof(CardController).GetProperty("IsPlayerBoard", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _sIsPlayerBoardProp = _isPlayerBoardProp;
            _skillListField = typeof(TheBazaar.SkillPresentationManager).GetField("_skillList", BindingFlags.Instance | BindingFlags.NonPublic);

            _buildsPath = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "builds.json");
            LoadBuilds();
            CleanBuilds();
            LoadTranslations();
            LoadShopRecommendationData();
            LoadEventData();
            LoadEventNotes();

            try
            {
                var harmony = new Harmony("com.bazaar.boardreader.patches");
                harmony.PatchAll(typeof(CardController_SetCardData_Patch).Assembly);
                _logger.LogInfo("[BoardReader] Harmony OK");
            }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] Harmony: {0}", ex)); }

            // 确保 BoardData 目录存在
            try
            {
                var boardDataDir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
                Directory.CreateDirectory(boardDataDir);
                _logger.LogInfo("[BoardReader] BoardData dir: " + boardDataDir);
            }
            catch { }
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
                if (name == "f6") return Input.GetKeyDown(KeyCode.F6);
                if (name == "f7") return Input.GetKeyDown(KeyCode.F7);
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
                _showItemSkillLabels = _overlayEnabled;
                _showShopEventLabels = _overlayEnabled;
                Config[CfgSecGeneral, CfgOverlay].BoxedValue = _overlayEnabled;
                Config.Save();
            }

            // 持续尝试读取游戏状态
            if (GameStateDirty) ReadGameStateFromDto();
            if (Time.frameCount % 300 == 0)
                RefreshDayFromFile();


            if (IsKeyPressed("f7"))
            {
                StartCaptureMode();
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
            // 每5秒刷新天数（从 game_state.json 或 UI）
            if (Time.frameCount % 300 == 0)
                RefreshDayFromFile();
            // 天数未知时隐藏所有面板与标签（数据不完整）
            bool dayKnown = _currentDay >= 1;
            if (_overlayEnabled && dayKnown)
            {
                var cam = Camera.main ?? Camera.current;
                if (cam != null)
                {
                    if (_showItemSkillLabels)
                    {
                        foreach (var lbl in _itemLabels)
                        { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, false); }
                        foreach (var lbl in _skillLabels)
                        { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, false); }
                    }
                    if (_showShopEventLabels && !_backpackOpen)
                    {
                        foreach (var lbl in _shopLabels)
                        { if (lbl.ScreenPos.z <= 0) continue; DrawLabel(lbl, true); }
                    }
                }
            }
            if (_showSliders) { try { DrawSlidersPanel(); } catch (Exception e) { _logger.LogError("F7面板: " + e); } }
            if (_captureMode) { try { DrawCaptureDialog(); } catch (Exception e) { _logger.LogError("捕获: " + e); } }
            if (_showRecommendations && dayKnown) { try { DrawRecommendationPanel(); } catch (Exception e) { _logger.LogError("推荐: " + e); } }
            if (_showBuildManager && dayKnown) { try { DrawBuildManagerPanel(); } catch (Exception e) { _logger.LogError("管理: " + e); } }
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

            // 多行 SubText
            var subLines = string.IsNullOrEmpty(lbl.SubText) ? new string[0] : lbl.SubText.Split('\n');
            var subLineH = 0f;
            var maxSubW = 0f;
            var subStyle = new GUIStyle(style) { fontSize = 11, alignment = TextAnchor.UpperCenter };
            foreach (var line in subLines)
            {
                var lsz = subStyle.CalcSize(new GUIContent(line));
                if (lsz.x + 16 > maxSubW) maxSubW = lsz.x + 16;
                subLineH += lsz.y + 1;
            }
            if (maxSubW > w) w = (int)maxSubW;
            var h = (int)(sz.y + 6 + subLineH);
            var r = new Rect(lbl.ScreenPos.x - w / 2, Screen.height - lbl.ScreenPos.y, w, h);
            GUI.DrawTexture(r, GetRoundedBg(w, h));

            // 主文字描边
            var os = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, 0.9f) } };
            var tr = new Rect(r.x, r.y + 3, w, sz.y);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    if (dx != 0 || dy != 0)
                        GUI.Label(new Rect(tr.x + dx, tr.y + dy, w, sz.y), lbl.Text, os);

            // 无匹配时整体30%透明度
            bool isEmpty = subLines.Length > 0 && subLines[0].StartsWith("\u2606\u2606\u2606");
            float masterAlpha = isEmpty ? 0.3f : 1f;

            // 主文字颜色
            if (isShop) style.normal.textColor = new Color(0.2f, 1f, 0.3f, masterAlpha);
            else if (_tierColors.ContainsKey(lbl.Tier))
            { var tc = _tierColors[lbl.Tier]; style.normal.textColor = new Color(tc.r, tc.g, tc.b, masterAlpha); }
            else style.normal.textColor = new Color(1, 1, 1, masterAlpha);
            GUI.Label(tr, lbl.Text, style);

            // 多行副文字
            float subY = r.y + 3 + sz.y + 1;
            bool isShopRating = subLines.Length > 0 && (subLines[0].StartsWith("\u2605") || subLines[0].StartsWith("\u2606") || subLines[0].StartsWith("\u25c6"));
            if (!isShopRating && subLines.Length == 1)
            {
                // 暴击文本：根据数值动态颜色(白→红)和大小(50%→150%)
                float pct = 0.5f;
                var txt = subLines[0].Replace("%", "");
                float.TryParse(txt, out pct);
                pct = Mathf.Clamp(pct / 100f, 0.01f, 1f);
                float baseSize = 12f;
                subStyle.normal.textColor = new Color(1f, 1f - pct, 1f - pct); // 白→红
                subStyle.fontSize = (int)(baseSize * (0.5f + pct * 2.5f)); // 50%→300%
                subStyle.fontStyle = FontStyle.Bold;
                var lineRect = new Rect(r.x + 4, subY, w - 8, subStyle.fontSize + 4);
                var sk = new GUIStyle(subStyle) { normal = { textColor = Color.black } };
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            GUI.Label(new Rect(lineRect.x + dx, lineRect.y + dy, lineRect.width, lineRect.height), subLines[0], sk);
                GUI.Label(lineRect, subLines[0], subStyle);
            }
            else
            {
                for (int i = 0; i < subLines.Length; i++)
                {
                    var line = subLines[i];
                    if (line.StartsWith("\u2605") || line.StartsWith("\u2606"))
                        subStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
                    else if (line.StartsWith("\u25c6"))
                        subStyle.normal.textColor = new Color(1f, 0.85f, 0.2f); // 推荐文本=金色
                    else if (i == 1)
                        subStyle.normal.textColor = new Color(1f, 0.25f, 0.2f);       // 核心=红色
                    else if (i == 2)
                        subStyle.normal.textColor = new Color(1f, 0.85f, 0.1f);       // 灵活=黄色
                    else
                        subStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
                    // 居中：先计算总宽度
                    float totalW = CalcLineWidth(line, subStyle);
                    float startX = r.x + Mathf.Max(4, (w - totalW) / 2f);
                    DrawLineWithOwnedAlpha(startX, subY, w - 8, line, subStyle);
                    var lsz = subStyle.CalcSize(new GUIContent(line));
                    subY += lsz.y + 1;
                }
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

        // 计算一行物品文本的总渲染宽度
        private float CalcLineWidth(string line, GUIStyle baseStyle)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            float total = 0;
            var parts = line.Split(',');
            var s = new GUIStyle(baseStyle);
            for (int pi = 0; pi < parts.Length; pi++)
            {
                var part = parts[pi];
                var display = part.StartsWith("*") ? part.Substring(1).Trim() : part.Trim();
                var sep = (pi < parts.Length - 1) ? "," : "";
                total += s.CalcSize(new GUIContent(display + sep)).x;
            }
            return total;
        }

        // 渲染一行物品文本，*前缀的物品用50%透明度（已拥有可升级）
        private void DrawLineWithOwnedAlpha(float x, float y, float maxW, string line, GUIStyle baseStyle)
        {
            if (string.IsNullOrEmpty(line)) return;
            float cx = x;
            // 按逗号分割，逐项渲染
            var parts = line.Split(',');
            for (int pi = 0; pi < parts.Length; pi++)
            {
                var part = parts[pi];
                bool isOwned = part.StartsWith("*");
                var display = isOwned ? part.Substring(1).Trim() : part.Trim();
                var sep = (pi < parts.Length - 1) ? "," : "";
                var text = display + sep;
                var s = new GUIStyle(baseStyle);
                if (isOwned)
                    s.normal.textColor = new Color(baseStyle.normal.textColor.r, baseStyle.normal.textColor.g, baseStyle.normal.textColor.b, 0.5f);
                var sz = s.CalcSize(new GUIContent(text));
                if (cx + sz.x > x + maxW - 4) { cx = x; y += sz.y + 1; }
                GUI.Label(new Rect(cx, y, sz.x + 2, sz.y), text, s);
                cx += sz.x;
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
            DrawRect(new Rect(mx, my, tw, th), new Color(0, 0, 0, 0.9f));
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
            var bg = new Color(0, 0, 0, _bgOpacity * 0.7f);
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

        private Texture2D _whiteTex;
        private Texture2D WhiteTex() { if (_whiteTex == null) { _whiteTex = new Texture2D(1, 1); _whiteTex.SetPixel(0, 0, Color.white); _whiteTex.Apply(); } return _whiteTex; }
        // 画纯色矩形（复用白色纹理+GUI.color）
        private void DrawRect(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, WhiteTex()); GUI.color = old; }

        // 画圆角矩形（用竖条逐步逼近圆角，无需生成纹理）
        private void DrawRoundedRect(Rect r, Color c, int radius = 10)
        {
            if (radius < 2 || r.width < radius * 2 || r.height < radius * 2) { DrawRect(r, c); return; }
            var old = GUI.color;
            GUI.color = c;
            var wt = WhiteTex();
            // 主体：中间矩形 + 左右两条（去掉圆角区域）
            GUI.DrawTexture(new Rect(r.x + radius, r.y, r.width - 2 * radius, r.height), wt);
            GUI.DrawTexture(new Rect(r.x, r.y + radius, r.width, r.height - 2 * radius), wt);
            // 四角：逐列从外向内画竖条，竖条高度逐步增加到完整高度
            for (int i = 0; i < radius; i++)
            {
                int step = radius - i;
                GUI.DrawTexture(new Rect(r.x + i, r.y + step, 1, r.height - 2 * step), wt);
                GUI.DrawTexture(new Rect(r.x + r.width - 1 - i, r.y + step, 1, r.height - 2 * step), wt);
            }
            GUI.color = old;
        }

        // 面板背景颜色=英雄颜色暗化版（未选英雄则黑色）
        private Color GetPanelBgColor()
        {
            try
            {
                if (string.IsNullOrEmpty(_detectedHero))
                    return new Color(0, 0, 0, _panelAlpha);
                var hc = GetHeroColor(_detectedHero);
                return new Color(hc.r * 0.12f, hc.g * 0.12f, hc.b * 0.12f, _panelAlpha);
            }
            catch { return new Color(0, 0, 0, _panelAlpha); }
        }

        // Ctrl+拖动标题栏移动窗口
        private bool IsCtrlHeld()
        {
            if (_useNewInput)
            {
                try
                {
                    var kt = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    var kb = kt.GetProperty("current").GetValue(null, null);
                    if (kb != null)
                    {
                        var lc = kt.GetProperty("leftCtrlKey").GetValue(kb, null);
                        var rc = kt.GetProperty("rightCtrlKey").GetValue(kb, null);
                        bool lp = lc != null && (bool)lc.GetType().GetProperty("isPressed").GetValue(lc, null);
                        bool rp = rc != null && (bool)rc.GetType().GetProperty("isPressed").GetValue(rc, null);
                        return lp || rp;
                    }
                }
                catch { }
            }
            try { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
            catch { return false; }
        }

        private void HandleCtrlDrag(Rect bar, ref float px, ref float py, string cfgX, string cfgY, ref bool isDragging)
        {
            Event e = Event.current;
            if (e == null) return;
            if (!IsCtrlHeld())
            {
                isDragging = false;
                return;
            }
            var mp = e.mousePosition;
            if (e.type == EventType.MouseDown && bar.Contains(mp))
            {
                isDragging = true;
                _dragStartMouseX = mp.x; _dragStartMouseY = mp.y;
                _dragStartPanelX = px; _dragStartPanelY = py;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && isDragging)
            {
                px = _dragStartPanelX + (mp.x - _dragStartMouseX);
                py = _dragStartPanelY + (mp.y - _dragStartMouseY);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && isDragging)
            {
                isDragging = false;
                Config[CfgSec, cfgX].BoxedValue = px;
                Config[CfgSec, cfgY].BoxedValue = py;
                Config.Save();
                e.Use();
            }
        }

        private bool IsTitleDoubleClicked(Rect titleBar)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 0 || e.clickCount < 2) return false;
            if (!titleBar.Contains(e.mousePosition)) return false;
            e.Use();
            return true;
        }

        private float ClampPanelY(float y, float h)
        {
            if (y + h > Screen.height) y = Screen.height - h - 10f;
            if (y < 10f) y = 10f;
            return y;
        }

        private void DrawSlidersPanel()
        {
            var pw = 270f;
            const float collapsedH = 28f;
            const float expandedH = 206f;
            var ph = _settingsCollapsed ? collapsedH : expandedH;
            var px = _settingsPanelX;
            var py = _settingsPanelY < 0f ? Screen.height - ph - 40f : _settingsPanelY;
            if (px < 10f) px = 10f;
            if (px + pw > Screen.width) px = Screen.width - pw - 10f;
            py = ClampPanelY(py, ph);
            _settingsPanelX = px; _settingsPanelY = py;

            var panelRect = new Rect(px, py, pw, ph);
            DrawRoundedRect(panelRect, GetPanelBgColor());
            var titleBar = new Rect(px, py + ph - collapsedH, pw, collapsedH);
            HandleCtrlDrag(panelRect, ref _settingsPanelX, ref _settingsPanelY, CfgSettingsX, CfgSettingsY, ref _draggingSettings);
            px = _settingsPanelX; py = _settingsPanelY;
            titleBar = new Rect(px, py + ph - collapsedH, pw, collapsedH);
            if (IsTitleDoubleClicked(titleBar))
            {
                if (_settingsCollapsed)
                {
                    _settingsPanelY = ClampPanelY(titleBar.y + collapsedH - expandedH, expandedH);
                    _settingsCollapsed = false;
                }
                else
                {
                    _settingsPanelY = ClampPanelY(titleBar.y, collapsedH);
                    _settingsCollapsed = true;
                }
                Config[CfgSec, CfgSettingsY].BoxedValue = _settingsPanelY;
                Config.Save();
                return;
            }

            var s = new GUIStyle(_labelStyle) { fontSize = 12, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;
            var title = new GUIStyle(s) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            title.normal.textColor = new Color(0.8f, 0.95f, 1f);
            var btn = new GUIStyle(GUI.skin.button) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

            if (!_settingsCollapsed)
            {
                float ry = py + 10f;
                GUI.Label(new Rect(px + 12, ry, 110, 16), string.Format("\u7269\u54c1 {0}px", (int)_itemOffsetY), s);
                _itemOffsetY = GUI.HorizontalSlider(new Rect(px + 118, ry + 4, 138, 10), _itemOffsetY, 0f, 300f);
                ry += 24f;
                GUI.Label(new Rect(px + 12, ry, 110, 16), string.Format("\u6280\u80fd {0}px", (int)_skillOffsetY), s);
                _skillOffsetY = GUI.HorizontalSlider(new Rect(px + 118, ry + 4, 138, 10), _skillOffsetY, 0f, 300f);
                ry += 24f;
                GUI.Label(new Rect(px + 12, ry, 110, 16), string.Format("\u5546\u5e97 {0}px", (int)_shopOffsetY), s);
                _shopOffsetY = GUI.HorizontalSlider(new Rect(px + 118, ry + 4, 138, 10), _shopOffsetY, 0f, 300f);
                ry += 24f;
                GUI.Label(new Rect(px + 12, ry, 110, 16), string.Format("\u9762\u677f {0:F0}%", _panelAlpha * 100f), s);
                _panelAlpha = GUI.HorizontalSlider(new Rect(px + 118, ry + 4, 138, 10), _panelAlpha, 0.2f, 0.95f);
                ry += 28f;

                if (GUI.Button(new Rect(px + 12, ry, 58, 20), "\u4fdd\u5b58", btn))
                {
                    Config[CfgSec, CfgItem].BoxedValue = _itemOffsetY;
                    Config[CfgSec, CfgSkill].BoxedValue = _skillOffsetY;
                    Config[CfgSec, CfgShop].BoxedValue = _shopOffsetY;
                    Config[CfgSec, CfgPanelAlpha].BoxedValue = _panelAlpha;
                    Config[CfgSecGeneral, CfgShowItemSkill].BoxedValue = _showItemSkillLabels;
                    Config[CfgSecGeneral, CfgShowShopEvent].BoxedValue = _showShopEventLabels;
                    Config.Save();
                }
                if (GUI.Button(new Rect(px + 78, ry, 58, 20), "\u91cd\u7f6e", btn))
                {
                    _itemOffsetY = 100f; _skillOffsetY = 30f; _shopOffsetY = 80f; _panelAlpha = 0.4f;
                }
                if (GUI.Button(new Rect(px + pw - 70, ry, 58, 20), "\u4e2d/EN", btn))
                    _useChinese = !_useChinese;
                ry += 28f;

                GUI.Label(new Rect(px + 12, ry + 2, 36, 18), "\u663e\u9690", s);
                DrawToggleChip(new Rect(px + 52, ry, 64, 20), "\u7269\u54c1\u6280\u80fd", _showItemSkillLabels, delegate(bool v) { _showItemSkillLabels = v; });
                DrawToggleChip(new Rect(px + 120, ry, 64, 20), "\u5546\u5e97\u4e8b\u4ef6", _showShopEventLabels, delegate(bool v) { _showShopEventLabels = v; });
                            }

            GUI.Label(new Rect(px + 10, titleBar.y + 4, 120, 20), "\u8bbe\u7f6e", title);
            DrawTitleHint(px + 10, titleBar.y + 5, "\u8bbe\u7f6e", title);
            if (GUI.Button(new Rect(px + pw - 30, titleBar.y + 4, 24, 20), "X", btn))
            {
                _overlayEnabled = false;
                _showSliders = false;
                _showRecommendations = false;
                _showBuildManager = false;
                Config[CfgSecGeneral, CfgOverlay].BoxedValue = _overlayEnabled;
                Config.Save();
            }
        }

        private void DrawToggleChip(Rect rect, string text, bool active, Action<bool> setValue)
        {
            var oldColor = GUI.color;
            GUI.color = active ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            var style = new GUIStyle(GUI.skin.button) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            if (GUI.Button(rect, text, style))
            {
                setValue(!active);
                Config[CfgSecGeneral, CfgShowItemSkill].BoxedValue = _showItemSkillLabels;
                Config[CfgSecGeneral, CfgShowShopEvent].BoxedValue = _showShopEventLabels;
                Config.Save();
            }
            GUI.color = oldColor;
        }

        // ==================== F8 捕获 ====================

        private void StartCaptureMode()
        {
            _captureMode = true;
            _captureBuildName = "";
            _captureHeroInput = _detectedHero;
            _captureItemsInput = string.Join(", ", TranslateEach(GatherCurrentBoardItemNames()).ToArray());
            _captureSkillsInput = string.Join(", ", TranslateEach(GatherPlayerSkillNames()).ToArray());
        }

        private void StartEmptyCaptureMode()
        {
            _captureMode = true;
            _captureBuildName = "";
            _captureHeroInput = _detectedHero;
            _captureItemsInput = "";
            _captureSkillsInput = "";
        }

        private List<string> ParseCaptureList(string input)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(input)) return result;
            var parts = input.Split(new[] { ',', '\n', '\uFF0C', '\u3001', ';', '\uFF1B' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var eng = ReverseTranslate(name);
                if (!result.Contains(eng)) result.Add(eng);
            }
            return result;
        }

        private void CaptureCurrentBoardAsBuild()
        {
            var items = ParseCaptureList(_captureItemsInput);
            var skills = ParseCaptureList(_captureSkillsInput);
            if (items.Count == 0 && skills.Count == 0) return;
            var hero = string.IsNullOrEmpty(_detectedHero) ? _captureHeroInput : _detectedHero;
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
            var w = 420f; var h = 275f;
            var x = (Screen.width - w) / 2; var y = (Screen.height - h) / 2;
            DrawRoundedRect(new Rect(x, y, w, h), new Color(0.1f, 0.1f, 0.15f, _panelAlpha));

            var s = new GUIStyle(_labelStyle) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;
            GUI.Label(new Rect(x + 15, y + 10, 360, 22), "\u6355\u83b7\u5f53\u524d\u68cb\u76d8\u6784\u7b51", s);

            s.fontSize = 13;
            GUI.Label(new Rect(x + 15, y + 42, 70, 22), "\u6784\u7b51\u540d:", s);
            _captureBuildName = GUI.TextField(new Rect(x + 82, y + 40, 170, 22), _captureBuildName ?? "", 30);

            GUI.Label(new Rect(x + 265, y + 42, 50, 22), "\u82f1\u96c4:", s);
            var hero = string.IsNullOrEmpty(_detectedHero) ? "\u672a\u77e5" : _detectedHero;
            GUI.Label(new Rect(x + 310, y + 42, 90, 22), hero, s);
            _captureHeroInput = hero;

            GUI.Label(new Rect(x + 15, y + 74, 80, 20), "\u7269\u54c1:", s);
            _captureItemsInput = GUI.TextArea(new Rect(x + 15, y + 96, 390, 55), _captureItemsInput ?? "", 500);

            GUI.Label(new Rect(x + 15, y + 158, 80, 20), "\u6280\u80fd:", s);
            _captureSkillsInput = GUI.TextArea(new Rect(x + 15, y + 180, 390, 55), _captureSkillsInput ?? "", 500);

            var bs = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            if (GUI.Button(new Rect(x + 215, y + 242, 90, 26), "\u4fdd\u5b58", bs))
            { if (!string.IsNullOrEmpty(_captureBuildName)) { CaptureCurrentBoardAsBuild(); _captureMode = false; } }
            if (GUI.Button(new Rect(x + 315, y + 242, 90, 26), "\u53d6\u6d88", bs)) _captureMode = false;
        }

        // ==================== F9 推荐面板 ====================

        private void OpenPreviewer()
        {
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "BazaarBoardReader");
                var script = Path.Combine(root, "scripts", "shop_browser.py");
                var psi = new System.Diagnostics.ProcessStartInfo();
                if (File.Exists(script))
                {
                    psi.FileName = "pythonw.exe";
                    psi.Arguments = "\"" + script + "\"";
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.FileName = Path.Combine(root, "\u9884\u89c8\u5668.bat");
                    psi.UseShellExecute = true;
                    psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                }
                psi.WorkingDirectory = root;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { _logger.LogError("[BoardReader] OpenPreviewer: " + ex); }
        }

        private Color WithAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, c.a * alpha);
        }

        private void DrawTitleHint(float x, float y, string titleText, GUIStyle titleStyle)
        {
            var hintStyle = new GUIStyle(titleStyle) { fontSize = Math.Max(9, titleStyle.fontSize - 4), fontStyle = FontStyle.Normal };
            var tc = titleStyle.normal.textColor;
            hintStyle.normal.textColor = new Color(tc.r, tc.g, tc.b, tc.a * 0.5f);
            var titleW = titleStyle.CalcSize(new GUIContent(titleText)).x;
            GUI.Label(new Rect(x + titleW + 8f, y, 180f, 20f), "ctrl:移动 双击:开合", hintStyle);
        }

        private void InvalidateRecommendationCache()
        {
            _lastRecommendationCalcFrame = -1;
            _lastRecommendationCalcTime = -999f;
            _lastRecommendationCalcBuildsVersion = -1;
            _bestBuildCache = null;
            _bestBuildCacheFrame = -1;
            _bestBuildCacheHero = "";
            _bestBuildCacheBuild = "";
            _bestBuildCacheBuildsVersion = -1;
            _ownedItemsResultCacheFrame = -1;
        }

        private void MarkBuildsChanged()
        {
            _buildsVersion++;
            InvalidateRecommendationCache();
        }

        private void SetDetectedHero(string hero)
        {
            if (string.IsNullOrEmpty(hero) || hero == _detectedHero) return;
            _detectedHero = hero;
            _selectedBuildName = "";
            Config[CfgSecGeneral, CfgHero].BoxedValue = _detectedHero;
            Config[CfgSecGeneral, CfgBuild].BoxedValue = _selectedBuildName;
            Config.Save();
            ClearOwnedItemCaches();
            InvalidateRecommendationCache();
        }

        private void RefreshRecommendationMatchesIfDue(bool force = false)
        {
            if (!force
                && _lastRecommendationCalcFrame == Time.frameCount
                && _lastRecommendationCalcHero == _detectedHero
                && _lastRecommendationCalcBuild == _selectedBuildName
                && _lastRecommendationCalcBuildsVersion == _buildsVersion)
                return;
            if (!force && Time.time - _lastRecommendationCalcTime < RecommendationRefreshInterval)
                return;
            _lastRecommendationCalcFrame = Time.frameCount;
            _lastRecommendationCalcTime = Time.time;
            _lastRecommendationCalcHero = _detectedHero;
            _lastRecommendationCalcBuild = _selectedBuildName;
            _lastRecommendationCalcBuildsVersion = _buildsVersion;
            try
            {
                _matchResults = MatchBuilds(_detectedHero, GatherAllItemNames());
            }
            catch { _matchResults = new List<BuildMatchResult>(); }
        }

        private List<BuildMatchResult> GetDisplayMatchResults()
        {
            var results = _matchResults != null ? new List<BuildMatchResult>(_matchResults) : new List<BuildMatchResult>();
            if (!string.IsNullOrEmpty(_selectedBuildName))
            {
                int idx = results.FindIndex(r => r.Template != null && r.Template.BuildName == _selectedBuildName);
                if (idx > 0)
                {
                    var selected = results[idx];
                    results.RemoveAt(idx);
                    results.Insert(0, selected);
                }
            }
            return results;
        }

        private void DrawRecommendationPanel()
        {
            var px = _recPanelX; var py = _recPanelY; var pw = 350f;
            var s = new GUIStyle(_labelStyle) { fontSize = 13, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;

            RefreshRecommendationMatchesIfDue();
            var displayResults = GetDisplayMatchResults();
            bool canHideOtherBuilds = displayResults.Count > 1;
            var visibleResults = GetVisibleMatchResults(displayResults);

            var heroes = GetVisibleHeroNames();
            float heroRows = CountHeroRows(heroes, pw, s);
            float contentYRel = 70f;
            float bottomControlsH = 32f;
            float contentH = EstimateRecommendationContentHeight(visibleResults, pw - 30f);
            float maxPanelH = Mathf.Max(96f, Mathf.Min(Screen.height * 0.5f, Screen.height - py - 10f));
            var ph = _recCollapsed ? 28f : Mathf.Clamp(contentYRel + contentH + bottomControlsH, 96f, maxPanelH);

            py = ClampPanelY(py, ph);
            _recPanelX = px; _recPanelY = py;
            var recRect = new Rect(px, py, pw, ph);
            DrawRoundedRect(recRect, GetPanelBgColor());
            HandleCtrlDrag(recRect, ref _recPanelX, ref _recPanelY, CfgRecX, CfgRecY, ref _draggingRec);
            px = _recPanelX; py = ClampPanelY(_recPanelY, ph);
            var recTitleBar = new Rect(px, py, pw, 28f);
            if (IsTitleDoubleClicked(recTitleBar))
            {
                _recCollapsed = !_recCollapsed;
                return;
            }

            var ts = new GUIStyle(s) { fontSize = 15, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(1f, 0.8f, 0.2f);
            GUI.Label(new Rect(px + 10, py + 5, pw - 120, 22), "\u6784\u7b51\u63a8\u8350", ts);
            DrawTitleHint(px + 10, py + 6, "\u6784\u7b51\u63a8\u8350", ts);

            var smallBtn = new GUIStyle(GUI.skin.button) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUI.enabled = canHideOtherBuilds;
            if (GUI.Button(new Rect(px + pw - 86, py + 4, 50, 20), _hideOtherBuilds ? "\u5168\u90e8" : "\u53ea\u663e", smallBtn))
                _hideOtherBuilds = !_hideOtherBuilds;
            GUI.enabled = true;

            if (_recCollapsed) return;

            var heroText = heroes.Count > 0 ? GetHeroAbbrev(heroes[0]) : "?";
            var hs = new GUIStyle(s) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
            hs.normal.textColor = heroes.Count > 0 ? GetHeroColor(heroes[0]) : Color.white;
            GUI.Label(new Rect(px + 10, py + 36, pw - 20, 28), string.Format("{0}     {1}", FormatHeroDisplayName(heroes.Count > 0 ? heroes[0] : ""), FormatDayLabel(_currentDay)), hs);

            var contentY = py + contentYRel;
            var scrollH = Mathf.Max(24f, py + ph - contentY - bottomControlsH);
            _recScroll = GUI.BeginScrollView(new Rect(px + 5, contentY, pw - 10, scrollH), _recScroll,
                new Rect(0, 0, pw - 30, contentH));

            float ry = 5;
            float barMaxW = pw - 75;
            int drawCount = Mathf.Min(visibleResults.Count, 10);
            for (int i = 0; i < drawCount; i++)
            {
                var mr = visibleResults[i];
                var score = mr.MatchScore * 100f;

                bool isSelected = _selectedBuildName == mr.Template.BuildName;
                bool dimOtherBuild = !string.IsNullOrEmpty(_selectedBuildName) && !isSelected;
                float rowAlpha = dimOtherBuild ? 0.35f : 1f;
                var cs = new GUIStyle(s) { fontSize = 13 };
                var titleColor = isSelected ? new Color(0.2f, 1f, 0.4f) :
                    (i == 0 && string.IsNullOrEmpty(_selectedBuildName)) ? new Color(1f, 0.8f, 0.2f) : Color.white;
                cs.normal.textColor = WithAlpha(titleColor, rowAlpha);
                GUI.Label(new Rect(10, ry, pw - 30, 18), string.Format("{0}. {1}", i + 1, Translate(mr.Template.BuildName)), cs);
                ry += 20;

                var selBtnRect = new Rect(10 + barMaxW - 32, ry - 18, 30, 16);
                if (GUI.Button(selBtnRect, isSelected ? "-" : "+", GUI.skin.button))
                {
                    _selectedBuildName = isSelected ? "" : mr.Template.BuildName;
                    Config[CfgSecGeneral, CfgBuild].BoxedValue = _selectedBuildName;
                    Config.Save();
                    InvalidateRecommendationCache();
                }
                var barH = 16f;
                DrawRect(new Rect(10, ry, barMaxW, barH), new Color(0.15f, 0.15f, 0.15f, 0.8f * rowAlpha));
                var barFillW = barMaxW * (score / 100f);
                if (barFillW > 0)
                {
                    var fc = score >= 70f ? new Color(0.1f, 0.9f, 0.2f) : score >= 40f ? new Color(0.8f, 0.8f, 0.1f) : new Color(0.8f, 0.3f, 0.1f);
                    DrawRect(new Rect(10, ry, barFillW, barH), WithAlpha(fc, rowAlpha));
                }
                var ps2 = new GUIStyle(s) { fontSize = 11, alignment = TextAnchor.MiddleRight };
                ps2.normal.textColor = WithAlpha(Color.white, rowAlpha);
                GUI.Label(new Rect(10, ry, barMaxW - 4, barH), string.Format("{0:F0}%", score), ps2);
                ry += barH + 4;

                var coreOwnedSet = new HashSet<string>(mr.CoreOwned);
                var coreMissingSet = new HashSet<string>(mr.CoreMissing);
                var coreUnavailSet = new HashSet<string>();
                var coreOrdered = BuildOrderedRecommendationItems(mr.Template.CoreItems, coreOwnedSet, coreMissingSet, coreUnavailSet);
                var flexOwnedSet = new HashSet<string>(mr.FlexOwned);
                var flexMissingSet = new HashSet<string>(mr.FlexMissing);
                var flexUnavailSet = new HashSet<string>();
                var flexOrdered = BuildOrderedRecommendationItems(mr.Template.FlexItems, flexOwnedSet, flexMissingSet, flexUnavailSet);
                var scOwnedSet = new HashSet<string>(mr.SkillCoreOwned);
                var scMissingSet = new HashSet<string>(mr.SkillCoreMissing);
                var scUnavailSet = new HashSet<string>();
                var scOrdered = BuildOrderedRecommendationItems(mr.Template.CoreSkills, scOwnedSet, scMissingSet, scUnavailSet);
                var sfOwnedSet = new HashSet<string>(mr.SkillFlexOwned);
                var sfMissingSet = new HashSet<string>(mr.SkillFlexMissing);
                var sfUnavailSet = new HashSet<string>();
                var sfOrdered = BuildOrderedRecommendationItems(mr.Template.FlexSkills, sfOwnedSet, sfMissingSet, sfUnavailSet);

                if (coreOrdered.Count > 0)
                    DrawItemsLine(15, ref ry, pw - 30, "", coreOrdered, coreOwnedSet, mr.ShopCoreMatches, coreUnavailSet, WithAlpha(new Color(0.2f, 1f, 0.3f), rowAlpha), WithAlpha(new Color(0.2f, 1f, 0.5f), rowAlpha));
                if (flexOrdered.Count > 0)
                    DrawItemsLine(15, ref ry, pw - 30, "", flexOrdered, flexOwnedSet, mr.ShopFlexMatches, flexUnavailSet, WithAlpha(new Color(1f, 0.7f, 0.3f), rowAlpha), WithAlpha(new Color(0.5f, 1f, 0.5f), rowAlpha));
                if (scOrdered.Count > 0)
                    DrawItemsLine(15, ref ry, pw - 30, "", scOrdered, scOwnedSet, new List<string>(), scUnavailSet, WithAlpha(new Color(0.4f, 0.5f, 1f), rowAlpha), WithAlpha(new Color(0.5f, 1f, 0.5f), rowAlpha));
                if (sfOrdered.Count > 0)
                    DrawItemsLine(15, ref ry, pw - 30, "", sfOrdered, sfOwnedSet, new List<string>(), sfUnavailSet, WithAlpha(new Color(0.3f, 1f, 0.5f), rowAlpha), WithAlpha(new Color(0.5f, 1f, 0.5f), rowAlpha));
                ry += 5;
            }

            if (visibleResults.Count == 0)
            { s.normal.textColor = Color.gray; GUI.Label(new Rect(10, ry, 300, 18), "\u6682\u65e0\u5339\u914d\u6784\u7b51", s); }
            GUI.EndScrollView();

            if (GUI.Button(new Rect(px + pw - 125, py + ph - 25, 55, 20), "F7\u6355\u83b7", GUI.skin.button)) StartCaptureMode();
            if (GUI.Button(new Rect(px + pw - 65, py + ph - 25, 55, 20), "\u9884\u89c8\u5668", GUI.skin.button)) OpenPreviewer();
        }

        private List<BuildMatchResult> GetVisibleMatchResults(List<BuildMatchResult> displayResults)
        {
            if (_hideOtherBuilds && displayResults.Count > 1)
                return displayResults.Take(1).ToList();
            return displayResults;
        }

        private List<string> GetVisibleHeroNames()
        {
            var result = new List<string>();
            if (!string.IsNullOrEmpty(_detectedHero))
            {
                result.Add(_detectedHero);
                return result;
            }
            var heroes = CollectHeroNames();
            if (heroes.Count > 0) result.Add(heroes[0]);
            return result;
        }

        private float CountHeroRows(List<string> heroes, float panelWidth, GUIStyle style)
        {
            if (heroes == null || heroes.Count == 0) return 1f;
            float rows = 1f;
            float hx = 10f;
            foreach (var h in heroes)
            {
                var abbrev = GetHeroAbbrev(h);
                var hbw = style.CalcSize(new GUIContent(abbrev)).x + 8;
                if (hx + hbw > panelWidth - 10f) { hx = 10f; rows += 1f; }
                hx += hbw + 1f;
            }
            return rows;
        }

        private List<string> BuildOrderedRecommendationItems(List<string> templateItems, HashSet<string> ownedSet, HashSet<string> missingSet, HashSet<string> unavailableSet)
        {
            var ordered = new List<string>();
            if (templateItems == null) return ordered;
            foreach (var item in templateItems)
            {
                if (ownedSet.Contains(item)) ordered.Add(item);
                else if (missingSet.Contains(item))
                {
                    ordered.Add(item);
                    if (!ItemCanAppearOnDay(item, _currentDay)) unavailableSet.Add(item);
                }
            }
            return ordered;
        }

        private float EstimateRecommendationContentHeight(List<BuildMatchResult> results, float maxW)
        {
            if (results == null || results.Count == 0) return 28f;
            var ms = new GUIStyle(_labelStyle) { fontSize = 11, alignment = TextAnchor.UpperLeft };
            float height = 5f;
            int count = Mathf.Min(results.Count, 10);
            for (int i = 0; i < count; i++)
            {
                var mr = results[i];
                height += 20f + 16f + 4f;
                var coreUnavail = new HashSet<string>();
                var flexUnavail = new HashSet<string>();
                var scUnavail = new HashSet<string>();
                var sfUnavail = new HashSet<string>();
                var core = BuildOrderedRecommendationItems(mr.Template.CoreItems, new HashSet<string>(mr.CoreOwned), new HashSet<string>(mr.CoreMissing), coreUnavail);
                var flex = BuildOrderedRecommendationItems(mr.Template.FlexItems, new HashSet<string>(mr.FlexOwned), new HashSet<string>(mr.FlexMissing), flexUnavail);
                var sc = BuildOrderedRecommendationItems(mr.Template.CoreSkills, new HashSet<string>(mr.SkillCoreOwned), new HashSet<string>(mr.SkillCoreMissing), scUnavail);
                var sf = BuildOrderedRecommendationItems(mr.Template.FlexSkills, new HashSet<string>(mr.SkillFlexOwned), new HashSet<string>(mr.SkillFlexMissing), sfUnavail);
                height += EstimateItemsLineHeight(core, maxW, ms);
                height += EstimateItemsLineHeight(flex, maxW, ms);
                height += EstimateItemsLineHeight(sc, maxW, ms);
                height += EstimateItemsLineHeight(sf, maxW, ms);
                height += 5f;
            }
            return height + 8f;
        }

        private float EstimateItemsLineHeight(List<string> orderedItems, float maxW, GUIStyle style)
        {
            if (orderedItems == null || orderedItems.Count == 0) return 0f;
            float cx = 20f;
            float h = 16f;
            for (int i = 0; i < orderedItems.Count; i++)
            {
                var text = Translate(orderedItems[i]) + (i < orderedItems.Count - 1 ? " " : "");
                var sz = style.CalcSize(new GUIContent(text));
                cx += sz.x + 2f;
                if (cx > maxW - 10f) { cx = 20f; h += 16f; }
            }
            return h;
        }

        private void DrawItemsLine(float x, ref float y, float maxW, string prefix,
            List<string> orderedItems, HashSet<string> ownedSet, List<string> inShop, HashSet<string> unavailable,
            Color lineColor, Color shopColor)
        {
            var ms = new GUIStyle(_labelStyle) { fontSize = 11, alignment = TextAnchor.UpperLeft };
            float cx = x + 5;

            for (int i = 0; i < orderedItems.Count; i++)
            {
                var item = orderedItems[i];
                var disp = Translate(item);
                bool isOwned = ownedSet.Contains(item);
                bool inStore = inShop.Contains(item);
                bool isUnavail = unavailable.Contains(item);

                if (isOwned)
                    ms.normal.textColor = new Color(lineColor.r, lineColor.g, lineColor.b, 0.3f);   // 已拥有=30%透明
                else if (isUnavail)
                    ms.normal.textColor = new Color(0.3f, 0.3f, 0.3f, 0.3f);                        // 刷不出=30%灰色
                else if (inStore)
                    ms.normal.textColor = new Color(shopColor.r, shopColor.g, shopColor.b, 0.6f + 0.4f * Mathf.Sin(Time.time * 5f));
                else
                    ms.normal.textColor = lineColor;

                var itemText = (i < orderedItems.Count - 1) ? disp + " " : disp;
                var sz = ms.CalcSize(new GUIContent(itemText));
                GUI.Label(new Rect(cx, y, sz.x + 5, 16), itemText, ms);
                if (inStore && !isUnavail && !isOwned)
                {
                    var ss = new GUIStyle(ms) { fontSize = 10 };
                    ss.normal.textColor = new Color(1f, 0.9f, 0.1f);
                    GUI.Label(new Rect(cx + sz.x - 2, y - 2, 20, 16), "*", ss);
                }
                cx += sz.x + 2;
                if (cx > x + maxW - 10) { cx = x + 5; y += 16; }
            }
            y += 16;
        }

        // ==================== F10 阵容管理 ====================

        private void DrawBuildManagerPanel()
        {
            var px = _mgrPanelX; var py = _mgrPanelY; var pw = 380f;
            var ph = _mgrCollapsed ? 28f : 400f;
            py = ClampPanelY(py, ph);
            _mgrPanelX = px; _mgrPanelY = py;
            var mgrRect = new Rect(px, py, pw, ph);
            DrawRoundedRect(mgrRect, GetPanelBgColor());
            HandleCtrlDrag(mgrRect, ref _mgrPanelX, ref _mgrPanelY, CfgMgrX, CfgMgrY, ref _draggingMgr);
            px = _mgrPanelX; py = ClampPanelY(_mgrPanelY, ph);
            var mgrTitleBar = new Rect(px, py, pw, 28f);
            if (IsTitleDoubleClicked(mgrTitleBar))
            {
                _mgrCollapsed = !_mgrCollapsed;
                return;
            }

            var s = new GUIStyle(_labelStyle) { fontSize = 12, alignment = TextAnchor.UpperLeft };
            s.normal.textColor = Color.white;
            var ts = new GUIStyle(s) { fontSize = 14, fontStyle = FontStyle.Bold };
            ts.normal.textColor = new Color(0.3f, 0.8f, 1f);

            GUI.Label(new Rect(px + 10, py + 5, pw - 90, 22), "\u6784\u7b51\u7ba1\u7406", ts);
            DrawTitleHint(px + 10, py + 6, "\u6784\u7b51\u7ba1\u7406", ts);
            if (_mgrCollapsed) return;

            if (GUI.Button(new Rect(px + pw - 84, py + 5, 50, 20), "\u65b0\u5efa"))
            {
                StartEmptyCaptureMode();
            }
            var heroBuilds = string.IsNullOrEmpty(_detectedHero)
                ? _builds : _builds.FindAll(b => b.HeroName == _detectedHero || string.IsNullOrEmpty(b.HeroName));
            if (heroBuilds.Count == 0) heroBuilds = _builds;
            GUI.Label(new Rect(px + 10, py + 28, pw - 20, 18),
                string.Format("\u82f1\u96c4: {0} | {1}/{2} \u6784\u7b51", string.IsNullOrEmpty(_detectedHero) ? "?" : _detectedHero, heroBuilds.Count, _builds.Count), s);

            float listY = py + 48;

            var visibleBuilds = string.IsNullOrEmpty(_selectedBuildForEdit)
                ? heroBuilds : heroBuilds.FindAll(b => b.BuildName == _selectedBuildForEdit);
            float scrollH = ph - (listY - py) - 30;
            _buildListScroll = GUI.BeginScrollView(new Rect(px + 5, listY, pw - 15, scrollH), _buildListScroll,
                new Rect(0, 0, pw - 35, visibleBuilds.Count * 360 + 10));

            float ry = 5;
            foreach (var build in visibleBuilds)
            {
                var isSelected = _selectedBuildForEdit == build.BuildName;
                var expandH = isSelected ? 330f : 22f;
                DrawRect(new Rect(0, ry, pw - 35, expandH),
                    isSelected ? new Color(0.15f, 0.15f, 0.25f, 0.7f) : new Color(0.1f, 0.1f, 0.15f, 0.5f));

                GUI.Label(new Rect(5, ry + 2, 120, 18), string.Format("{0} ({1})", Translate(build.BuildName), build.HeroName), s);
                if (GUI.Button(new Rect(250, ry + 1, 35, 18), isSelected ? "\u6536\u8d77" : "\u7f16\u8f91"))
                    _selectedBuildForEdit = isSelected ? "" : build.BuildName;
                if (GUI.Button(new Rect(290, ry + 1, 35, 18), "\u5220\u9664"))
                { _builds.Remove(build); SaveBuildsAtomic(); _selectedBuildForEdit = ""; break; }

                if (isSelected)
                {
                    var ns = new GUIStyle(s) { fontSize = 11 };
                    ns.normal.textColor = new Color(1f, 0.5f, 0.5f);
                    GUI.Label(new Rect(15, ry + 26, 330, 16), "\u6838\u5fc3\u7269\u54c1:", ns);
                    float iy = ry + 42;
                    DrawItemRow(build.CoreItems, build.FlexItems, ref iy, ns);
                    ns.normal.textColor = new Color(1f, 0.7f, 0.3f);
                    GUI.Label(new Rect(15, iy, 330, 16), "\u7075\u6d3b\u7269\u54c1:", ns);
                    iy += 16;
                    DrawItemRowReverse(build.FlexItems, build.CoreItems, ref iy, ns);

                    GUI.Label(new Rect(15, iy, 50, 18), "\u52a0\u7269:", s);
                    _newItemInput = GUI.TextField(new Rect(55, iy, 140, 20), _newItemInput ?? "", 30);
                    if (GUI.Button(new Rect(198, iy, 35, 20), "\u6838\u5fc3"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput))
                        {
                            var eng = ReverseTranslate(_newItemInput);
                            if (!build.CoreItems.Contains(eng)) { build.CoreItems.Add(eng); SaveBuildsAtomic(); _newItemInput = ""; }
                        }
                    }
                    if (GUI.Button(new Rect(236, iy, 35, 20), "\u7075\u6d3b"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput))
                        {
                            var eng = ReverseTranslate(_newItemInput);
                            if (!build.FlexItems.Contains(eng)) { build.FlexItems.Add(eng); SaveBuildsAtomic(); _newItemInput = ""; }
                        }
                    }
                    iy += 22;

                    ns.normal.textColor = new Color(0.4f, 0.5f, 1f);
                    GUI.Label(new Rect(15, iy, 330, 16), "\u6838\u5fc3\u6280\u80fd:", ns);
                    iy += 16;
                    DrawItemRow(build.CoreSkills, build.FlexSkills, ref iy, ns);
                    ns.normal.textColor = new Color(0.5f, 0.6f, 1f);
                    GUI.Label(new Rect(15, iy, 330, 16), "\u7075\u6d3b\u6280\u80fd:", ns);
                    iy += 16;
                    DrawItemRowReverse(build.FlexSkills, build.CoreSkills, ref iy, ns);

                    GUI.Label(new Rect(15, iy, 50, 18), "\u52a0\u6280:", s);
                    _newItemInput = GUI.TextField(new Rect(55, iy, 140, 20), _newItemInput ?? "", 30);
                    if (GUI.Button(new Rect(198, iy, 35, 20), "\u6838\u5fc3"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput))
                        {
                            var eng = ReverseTranslate(_newItemInput);
                            if (!build.CoreSkills.Contains(eng)) { build.CoreSkills.Add(eng); SaveBuildsAtomic(); _newItemInput = ""; }
                        }
                    }
                    if (GUI.Button(new Rect(236, iy, 35, 20), "\u7075\u6d3b"))
                    {
                        if (!string.IsNullOrEmpty(_newItemInput))
                        {
                            var eng = ReverseTranslate(_newItemInput);
                            if (!build.FlexSkills.Contains(eng)) { build.FlexSkills.Add(eng); SaveBuildsAtomic(); _newItemInput = ""; }
                        }
                    }
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
                if (GUI.Button(new Rect(195, iy, 25, 15), ">")) { dst.Add(src[i]); src.RemoveAt(i); SaveBuildsAtomic(); }
                if (GUI.Button(new Rect(222, iy, 25, 15), "X")) { src.RemoveAt(i); SaveBuildsAtomic(); }
                iy += 17;
            }
        }

        private void DrawItemRowReverse(List<string> src, List<string> dst, ref float iy, GUIStyle ns)
        {
            for (int i = 0; i < src.Count; i++)
            {
                GUI.Label(new Rect(25, iy, 160, 16), Translate(src[i]), ns);
                if (GUI.Button(new Rect(195, iy, 25, 15), ">")) { dst.Add(src[i]); src.RemoveAt(i); SaveBuildsAtomic(); }
                if (GUI.Button(new Rect(222, iy, 25, 15), "X")) { src.RemoveAt(i); SaveBuildsAtomic(); }
                iy += 17;
            }
        }

        // ==================== 标签收集 ====================

        private static string GetTransformContext(Transform t)
        {
            try
            {
                var names = new List<string>();
                Transform cur = t;
                for (int d = 0; cur != null && d < 12; d++) { names.Add(cur.name ?? ""); cur = cur.parent; }
                return string.Join("/", names.ToArray());
            }
            catch { return ""; }
        }

        private bool IsPlayerSkillTransform(Transform t, Camera cam)
        {
            if (t == null) return false;
            var ctx = GetTransformContext(t);
            if (ctx.IndexOf("Opponent", StringComparison.OrdinalIgnoreCase) >= 0
                || ctx.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0
                || ctx.IndexOf("Monster", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            try
            {
                var bm = t.GetComponentInParent<BoardManager>();
                if (bm != null) return bm == BoardManager.Instance;
            }
            catch { }

            if (ctx.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            try
            {
                if (cam != null)
                {
                    var sp = cam.WorldToScreenPoint(t.position);
                    if (sp.z > 0) return sp.y <= Screen.height * 0.65f;
                }
            }
            catch { }
            return true;
        }
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
                                if (!IsPlayerSkillTransform(cc.transform, cam)) continue;
                                sp.y -= _skillOffsetY;
                                _skillLabels.Add(new OverlayLabel { Text = GetCardName(cd), Tier = cd.Tier.ToString(), ScreenPos = sp, IsItem = true });
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
                        foreach (var mgr in spm)
                        {
                            var list = _skillListField.GetValue(mgr) as IList;
                            if (list == null) continue;
                            foreach (var obj in list)
                            {
                                try
                                {
                                    var r = obj as TheBazaar.SkillProxyRenderer;
                                    if (r == null || r.Card == null) continue;
                                    if (!IsPlayerSkillTransform(r.transform, cam)) continue;
                                    var sp = cam.WorldToScreenPoint(r.transform.position);
                                    sp.y -= _skillOffsetY;
                                    _skillLabels.Add(new OverlayLabel { Text = GetCardName(r.Card), Tier = r.Card.Tier.ToString(), ScreenPos = sp, IsItem = true });
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

        private void ScanEventRecommendations(Camera cam)
        {
            // Event recommendation text is handled by RateEvent so that non-item
            // events never leak note-only recommendations into overlays.
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
                    if (rating == null) rating = RateEvent(shopName);
                    if (rating == null) rating = RateLevelUpReward(shopName);
                    // 事件推荐文本（仅当已匹配到事件时才显示）
                    if (rating == null && _eventNotes.Count > 0)
                        _logger.LogInfo(string.Format("[BoardReader] 无匹配: {0}", shopName));
                    _shopLabels.Add(new OverlayLabel { Text = Translate(shopName), Tier = "Invalid", ScreenPos = sp, SubText = rating ?? "", HoverData = "" });
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

        private string GetHeroAbbrev(string hero)
        {
            var h = hero.ToLower();
            if (h.Contains("vanessa")) return "VAN";
            if (h.Contains("pygmalien")) return "PYG";
            if (h.Contains("dooley")) return "DOO";
            if (h.Contains("mak")) return "MAK";
            if (h.Contains("stelle")) return "STE";
            if (h.Contains("jules")) return "JUL";
            if (h.Contains("karnok")) return "KAR";
            return hero.Length > 3 ? hero.Substring(0, 3).ToUpper() : hero.ToUpper();
        }
        private string FormatHeroDisplayName(string hero)
        {
            var abbr = string.IsNullOrEmpty(hero) ? "?" : GetHeroAbbrev(hero);
            if (!_useChinese) return abbr;
            if (abbr == "VAN") return "\u6d77\u5c0f\u59b9";
            if (abbr == "PYG") return "\u732a\u732a";
            if (abbr == "DOO") return "\u9e21\u7172";
            if (abbr == "KAR") return "\u5361\u5361";
            if (abbr == "STE") return "\u9ed1\u59b9";
            if (abbr == "JUL") return "\u53a8\u5a18";
            if (abbr == "MAK") return "\u9a6c\u514b";
            return abbr;
        }

        private string FormatDayLabel(int day)
        {
            if (day < 1) return "?";
            return _useChinese ? ToChineseNumber(day) + "\u5929" : "Day " + day;
        }

        private string ToChineseNumber(int n)
        {
            string[] nums = { "", "\u4e00", "\u4e8c", "\u4e09", "\u56db", "\u4e94", "\u516d", "\u4e03", "\u516b", "\u4e5d" };
            if (n <= 10) return n == 10 ? "\u7b2c\u5341" : "\u7b2c" + nums[n];
            if (n < 20) return "\u7b2c\u5341" + nums[n - 10];
            if (n == 20) return "\u7b2c\u4e8c\u5341";
            return "\u7b2c" + n.ToString();
        }
        private Color GetHeroColor(string hero)
        {
            var h = hero.ToLower();
            if (h.Contains("vanessa")) return new Color(1f, 0.3f, 0.3f);
            if (h.Contains("pygmalien")) return new Color(0.3f, 0.5f, 1f);
            if (h.Contains("dooley")) return new Color(1f, 0.6f, 0.1f);
            if (h.Contains("mak")) return new Color(0.3f, 1f, 0.4f);
            if (h.Contains("stelle")) return new Color(1f, 0.9f, 0.2f);
            if (h.Contains("jules")) return new Color(0.7f, 0.3f, 1f);
            if (h.Contains("karnok")) return new Color(0.3f, 0.7f, 1f);
            return Color.gray;
        }

        private List<string> CollectHeroNames()
        {
            var heroes = new HashSet<string>();
            foreach (var b in _builds)
                if (!string.IsNullOrEmpty(b.HeroName)) heroes.Add(b.HeroName);
            var list = new List<string>(heroes);
            list.Sort();
            return list;
        }

        // 从 game_state.json 或 board_latest.json 读取天数（DTO 回退）
        private void RefreshDayFromFile()
        {
            try
            {
                var bdDir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
                // 1. 先试 game_state.json（StateExporter）
                var gsPath = Path.Combine(bdDir, "game_state.json");
                if (File.Exists(gsPath))
                {
                    var json = File.ReadAllText(gsPath, Encoding.UTF8);
                    var gs = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    if (gs != null && gs.ContainsKey("day"))
                    {
                        var newDay = Convert.ToInt32(gs["day"]);
                        var newHero = gs.ContainsKey("hero") ? gs["hero"]?.ToString() ?? "" : "";
                        // 新对局检测：天数变小或英雄变了 → 清缓存
                        if ((newDay < _currentDay && _currentDay > 0) || (!string.IsNullOrEmpty(newHero) && !string.IsNullOrEmpty(_detectedHero) && newHero != _detectedHero))
                            ClearOwnedItemCaches();
                        _currentDay = newDay;
                        SetDetectedHero(newHero);
                        _logger.LogInfo(string.Format("[BoardReader] Day from game_state.json: {0}", _currentDay));
                        return;
                    }
                }
                // 2. 再试 board_latest.json（F5 导出）
                var blPath = Path.Combine(bdDir, "board_latest.json");
                if (File.Exists(blPath))
                {
                    var json = File.ReadAllText(blPath, Encoding.UTF8);
                    var bl = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    if (bl != null && bl.ContainsKey("Day"))
                    {
                        _currentDay = Convert.ToInt32(bl["Day"]);
                        var newHero = bl.ContainsKey("Hero") ? bl["Hero"]?.ToString() ?? "" : "";
                        SetDetectedHero(newHero);
                        _logger.LogInfo(string.Format("[BoardReader] Day from board_latest.json: {0}", _currentDay));
                        return;
                    }
                }
                // 3. 最后 UI 扫描
                var ui = TryReadUiDay();
                if (ui.HasValue) { _currentDay = ui.Value; _logger.LogInfo(string.Format("[BoardReader] Day from UI: {0}", _currentDay)); }
            }
            catch (Exception ex) { _logger.LogInfo("[BoardReader] RefreshDay error: " + ex.Message); }
        }

        // 从拦截的 GameStateSync DTO 读取天数/金币/收入等
        private void ReadGameStateFromDto()
        {
            if (!GameStateDirty || LatestGameStateDto == null) return;
            GameStateDirty = false;
            try
            {
                var dto = LatestGameStateDto;
                // Run.Day
                var run = GetField(dto, "Run");
                if (run != null) { var v = GetField(run, "Day"); if (v != null) _currentDay = Convert.ToInt32(v); }
                // Player.Hero
                var player = GetField(dto, "Player");
                if (player != null) { var v = GetField(player, "Hero"); if (v != null && !string.IsNullOrEmpty(v.ToString())) SetDetectedHero(v.ToString()); }
                // Player.Attributes → 两遍法：先精确匹配，再模糊匹配回退
                if (player != null)
                {
                    var attrs = GetField(player, "Attributes") as System.Collections.IEnumerable;
                    if (attrs != null)
                    {
                        // 第一遍：精确匹配（避免 MaxHealth/MaxGold 干扰）
                        foreach (var item in attrs)
                        {
                            if (item == null) continue;
                            var key = GetProperty(item, "Key");
                            var val = GetProperty(item, "Value");
                            if (key == null || val == null) continue;
                            var ks = key.ToString();
                            int iv; try { iv = Convert.ToInt32(val); } catch { continue; }
                            if (ks.Equals("Gold", StringComparison.OrdinalIgnoreCase)) _currentGold = iv;
                            else if (ks.Equals("Health", StringComparison.OrdinalIgnoreCase)) _currentHealth = iv;
                            else if (ks.Equals("Income", StringComparison.OrdinalIgnoreCase)) _currentIncome = iv;
                            else if (ks.Equals("Prestige", StringComparison.OrdinalIgnoreCase)) _currentPrestige = iv;
                        }
                        // 第二遍：模糊匹配回退（精确未匹配到时）
                        if (_currentGold <= 0 || _currentHealth <= 0 || _currentIncome < 0 || _currentPrestige <= 0)
                        {
                            foreach (var item in attrs)
                            {
                                if (item == null) continue;
                                var key = GetProperty(item, "Key");
                                var val = GetProperty(item, "Value");
                                if (key == null || val == null) continue;
                                var ks = key.ToString();
                                int iv; try { iv = Convert.ToInt32(val); } catch { continue; }
                                if (_currentGold <= 0 && ks.IndexOf("Gold", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0) _currentGold = iv;
                                if (_currentHealth <= 0 && ks.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0 && ks.IndexOf("Regen", StringComparison.OrdinalIgnoreCase) < 0) _currentHealth = iv;
                                if (_currentIncome < 0 && ks.IndexOf("Income", StringComparison.OrdinalIgnoreCase) >= 0) _currentIncome = iv;
                                if (_currentPrestige <= 0 && ks.IndexOf("Prestige", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0) _currentPrestige = iv;
                            }
                        }
                    }
                }
                _logger.LogInfo(string.Format("[BoardReader] GameState: Hero={0} Day={1} Gold={2} Health={3} Income={4} Prestige={5}",
                    _detectedHero, _currentDay, _currentGold, _currentHealth, _currentIncome, _currentPrestige));
            }
            catch (Exception ex) { _logger.LogInfo("[BoardReader] ReadGameState error: " + ex.Message); }
        }

        private static object GetField(object target, string name)
        {
            if (target == null) return null;
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? null : f.GetValue(target);
        }

        private static object GetProperty(object target, string name)
        {
            if (target == null) return null;
            var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p == null ? null : p.GetValue(target, null);
        }

        private void DumpRunManagerProps()
        {
            try
            {
                var asm = typeof(BoardManager).Assembly;
                var allTypes = asm.GetTypes();
                // RunManager — 用 GetTypes().FirstOrDefault 因为 IL2CPP 可能剥离 GetType()
                var rmType = allTypes.FirstOrDefault(t => t.Name == "RunManager");
                if (rmType != null) {
                    var rmIp = rmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (rmIp != null) {
                        var rmInst = rmIp.GetValue(null, null);
                        if (rmInst != null) {
                            var props = rmType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var sb = new System.Text.StringBuilder("[BoardReader] RunManager props: ");
                            foreach (var p in props)
                            { try { sb.Append(p.Name + "=" + (p.GetValue(rmInst, null) ?? "null") + ", "); } catch { } }
                            _logger.LogInfo(sb.ToString());
                        }
                    }
                }
                // DayManager
                var dmType = allTypes.FirstOrDefault(t => t.Name == "DayManager");
                if (dmType != null) {
                    var dmIp = dmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (dmIp != null) {
                        var dmInst = dmIp.GetValue(null, null);
                        if (dmInst != null) {
                            var props = dmType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var sb = new System.Text.StringBuilder("[BoardReader] DayManager props: ");
                            foreach (var p in props)
                            { try { sb.Append(p.Name + "=" + (p.GetValue(dmInst, null) ?? "null") + ", "); } catch { } }
                            _logger.LogInfo(sb.ToString());
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogInfo("[BoardReader] DumpRunManager: " + ex.Message); }
        }

        private string DetectHero()
        {
            try
            {
                var asm = typeof(BoardManager).Assembly;
                var allTypes = asm.GetTypes();
                var rmType = allTypes.FirstOrDefault(t => t.Name == "RunManager");
                if (rmType != null)
                {
                    var ip = rmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ip != null)
                    {
                        var inst = ip.GetValue(null, null);
                        if (inst != null)
                        {
                            // 检测天数
                            if (_currentDay < 0)
                            {
                                foreach (var pn in new[] { "Day", "CurrentDay", "DayNumber", "RunDay", "Round", "CurrentRound" })
                                {
                                    try {
                                        var dp = rmType.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (dp != null) { var dv = dp.GetValue(inst, null); if (dv != null) { _currentDay = Convert.ToInt32(dv); break; } }
                                    } catch { }
                                }
                            }
                            // 检测英雄
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
            // 直接复用 GatherAllItemsWithTier 的缓存逻辑
            var result = new List<string>();
            foreach (var kv in GatherAllItemsWithTier())
                result.Add(kv.Key);
            return result;
        }

        private void ClearOwnedItemCaches()
        {
            _ownedItemsCache.Clear();
            _ownedBoardItemsCache.Clear();
            _ownedStorageItemsCache.Clear();
            _lastLiveBoardItems.Clear();
            _lastLiveStorageItems.Clear();
            _ownedItemsResultCache.Clear();
            _ownedItemsResultCacheFrame = -1;
            _lastLiveScanSawStorage = false;
            _backpackOpen = false;
        }

        private List<KeyValuePair<string, string>> GatherAllItemsWithTier()
        {
            if (_ownedItemsResultCacheFrame == Time.frameCount)
                return new List<KeyValuePair<string, string>>(_ownedItemsResultCache);

            var fresh = ScanLiveOwnedItemsWithTier();
            if (fresh != null && fresh.Count > 0)
            {
                _ownedBoardItemsCache.Clear();
                foreach (var kv in _lastLiveBoardItems)
                    _ownedBoardItemsCache[kv.Key] = kv.Value;

                if (_lastLiveScanSawStorage)
                {
                    _ownedStorageItemsCache.Clear();
                    foreach (var kv in _lastLiveStorageItems)
                        _ownedStorageItemsCache[kv.Key] = kv.Value;
                }
            }
            else if (_ownedBoardItemsCache.Count == 0 && _ownedStorageItemsCache.Count == 0)
            {
                var fallback = TryReadOwnedItemsFromGameState();
                if (fallback == null || fallback.Count == 0)
                {
                    fallback = new Dictionary<string, string>();
                    lock (TrackedCards)
                    {
                        foreach (var kv in TrackedCards)
                        {
                            var tc = kv.Value;
                            if (tc.IsPlayer && tc.Type == "Item" && !string.IsNullOrEmpty(tc.Name))
                            {
                                var key = BoardCardName(tc.Name).ToLower();
                                fallback[key] = tc.Tier ?? "Bronze";
                            }
                        }
                    }
                }

                foreach (var kv in fallback)
                    _ownedBoardItemsCache[kv.Key] = kv.Value;
            }

            _ownedItemsCache.Clear();
            foreach (var kv in _ownedBoardItemsCache)
                _ownedItemsCache[kv.Key] = kv.Value;
            foreach (var kv in _ownedStorageItemsCache)
                _ownedItemsCache[kv.Key] = kv.Value;

            var result = new List<KeyValuePair<string, string>>();
            foreach (var kv in _ownedItemsCache)
                result.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            _ownedItemsResultCache = new List<KeyValuePair<string, string>>(result);
            _ownedItemsResultCacheFrame = Time.frameCount;
            return result;
        }

        private Dictionary<string, string> ScanLiveOwnedItemsWithTier()
        {
            var owned = new Dictionary<string, string>();
            _lastLiveBoardItems.Clear();
            _lastLiveStorageItems.Clear();
            _lastLiveScanSawStorage = false;
            _backpackOpen = false;

            try
            {
#pragma warning disable 0618
                var all = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (all == null) return owned;

                foreach (var cc in all)
                {
                    try
                    {
                        if (cc == null || cc.gameObject == null || !cc.gameObject.activeInHierarchy)
                            continue;

                        var cd = cc.CardData;
                        if (cd == null || cd.Type.ToString() != "Item")
                            continue;

                        string section = GetCardSection(cd) ?? "";
                        string uiCtx = GetUiContext(cc) ?? "";
                        bool looksShop = section.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0
                            || section.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0
                            || section.IndexOf("Reward", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("OpponentItemSocket_", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (looksShop)
                            continue;

                        bool isStorage = section.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) >= 0
                            || section.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("PlayerStorageSocket_", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) >= 0
                            || uiCtx.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool isOwned = false;
                        try { isOwned = IsPlayerBoardFor(cc); }
                        catch { }
                        if (!isOwned)
                        {
                            isOwned = section.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0
                                || isStorage
                                || uiCtx.IndexOf("PlayerItemSocket_", StringComparison.OrdinalIgnoreCase) >= 0
                                || IsPlayerItem(cc.transform);
                        }
                        if (!isOwned)
                            continue;

                        var name = GetCardNameStatic(cd);
                        if (string.IsNullOrEmpty(name) || name == "???")
                        {
                            var templateId = cd.TemplateId != null ? cd.TemplateId.ToString() : "";
                            name = ResolveCardNameByTemplateId(templateId, cd);
                        }
                        if (string.IsNullOrEmpty(name) || name == "???")
                            continue;

                        var key = BoardCardName(name).ToLower();
                        var tier = cd.Tier.ToString();
                        owned[key] = tier;
                        if (isStorage)
                        {
                            _lastLiveScanSawStorage = true;
                            _backpackOpen = true;
                            _lastLiveStorageItems[key] = tier;
                        }
                        else
                        {
                            _lastLiveBoardItems[key] = tier;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return owned;
        }

        private Dictionary<string, string> TryReadOwnedItemsFromGameState()
        {
            if (_templateIdToName.Count <= 0) return null;

            try
            {
                var gsPath = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "game_state.json");
                if (!File.Exists(gsPath))
                    gsPath = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "BoardData", "game_state.json");
                if (!File.Exists(gsPath)) return null;

                var gsJson = File.ReadAllText(gsPath, Encoding.UTF8);
                var gs = JsonConvert.DeserializeObject<Dictionary<string, object>>(gsJson);
                if (gs == null) return null;

                var ownedItems = gs.ContainsKey("owned_items") ? gs["owned_items"] as JArray : null;
                if (ownedItems == null && gs.ContainsKey("owned_cards"))
                    ownedItems = gs["owned_cards"] as JArray;
                if (ownedItems == null) return null;

                var owned = new Dictionary<string, string>();
                foreach (var item in ownedItems)
                {
                    try
                    {
                        if (!string.Equals(item.Value<string>("card_type"), "Item", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var tid = item.Value<string>("template_id");
                        var rarity = item.Value<string>("rarity") ?? "Bronze";
                        if (!string.IsNullOrEmpty(tid) && _templateIdToName.ContainsKey(tid))
                        {
                            var key = _templateIdToName[tid].ToLower();
                            owned[key] = rarity;
                        }
                    }
                    catch { }
                }
                return owned;
            }
            catch { return null; }
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
                EnsureBuildsDatabaseFile();
                if (File.Exists(_buildsPath))
                    _builds = ReadBuildTemplatesFromFile(_buildsPath);
                else
                    _builds = new List<BuildTemplate>();
                NormalizeBuilds();
                MarkBuildsChanged();
            }
            catch (Exception ex)
            {
                _logger.LogError(string.Format("[BoardReader] Load builds failed: {0}", ex));
                _builds = new List<BuildTemplate>();
            }
        }

        private void EnsureBuildsDatabaseFile()
        {
            var dir = Path.GetDirectoryName(_buildsPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(_buildsPath)) return;

            var legacyCommunityPath = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "community_builds.json");
            if (File.Exists(legacyCommunityPath))
            {
                var migrated = ReadBuildTemplatesFromFile(legacyCommunityPath);
                WriteJsonAtomic(_buildsPath, JsonConvert.SerializeObject(migrated, Formatting.Indented));
                _logger.LogInfo(string.Format("[BoardReader] Migrated community builds to builds.json: {0}", migrated.Count));
                return;
            }

            var legacyConfigPath = Path.Combine(Paths.ConfigPath, "BazaarBoardReader_Builds.json");
            if (File.Exists(legacyConfigPath))
            {
                var migrated = ReadBuildTemplatesFromFile(legacyConfigPath);
                WriteJsonAtomic(_buildsPath, JsonConvert.SerializeObject(migrated, Formatting.Indented));
                _logger.LogInfo(string.Format("[BoardReader] Migrated config builds to builds.json: {0}", migrated.Count));
            }
        }

        private List<BuildTemplate> ReadBuildTemplatesFromFile(string path)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var trimmed = (json ?? "").TrimStart();
            if (trimmed.StartsWith("["))
                return JsonConvert.DeserializeObject<List<BuildTemplate>>(json) ?? new List<BuildTemplate>();

            var community = JsonConvert.DeserializeObject<Dictionary<string, CommunityBuild>>(json);
            return ConvertCommunityBuilds(community);
        }

        private List<BuildTemplate> ConvertCommunityBuilds(Dictionary<string, CommunityBuild> builds)
        {
            var result = new List<BuildTemplate>();
            if (builds == null) return result;
            foreach (var kv in builds)
            {
                var cb = kv.Value;
                if (cb == null) continue;
                result.Add(new BuildTemplate
                {
                    HeroName = cb.hero ?? "",
                    BuildName = cb.display_name ?? kv.Key,
                    CoreItems = cb.core_cards ?? new List<string>(),
                    FlexItems = (cb.transition_cards ?? new List<string>()).Concat(cb.optional_cards ?? new List<string>()).ToList(),
                    CoreSkills = new List<string>(),
                    FlexSkills = new List<string>()
                });
            }
            return result;
        }

        private void NormalizeBuilds()
        {
            foreach (var b in _builds)
            {
                if (b.CoreItems == null) b.CoreItems = new List<string>();
                if (b.FlexItems == null) b.FlexItems = new List<string>();
                if (b.CoreSkills == null) b.CoreSkills = new List<string>();
                if (b.FlexSkills == null) b.FlexSkills = new List<string>();
                if (b.HeroName == null) b.HeroName = "";
                if (b.BuildName == null) b.BuildName = "";
            }
        }

        private void CleanBuilds()
        {
            NormalizeBuilds();
            int removed = 0;
            foreach (var b in _builds)
            {
                removed += b.CoreItems.RemoveAll(i => string.IsNullOrEmpty(i));
                removed += b.FlexItems.RemoveAll(i => string.IsNullOrEmpty(i));
                removed += b.CoreSkills.RemoveAll(i => string.IsNullOrEmpty(i));
                removed += b.FlexSkills.RemoveAll(i => string.IsNullOrEmpty(i));
            }
            if (removed > 0) { SaveBuildsAtomic(); _logger.LogInfo(string.Format("[BoardReader] Clean empty build entries: {0}", removed)); }
        }

        private void SaveBuildsAtomic()
        {
            try
            {
                NormalizeBuilds();
                WriteJsonAtomic(_buildsPath, JsonConvert.SerializeObject(_builds, Formatting.Indented));
                MarkBuildsChanged();
            }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] Save builds failed: {0}", ex)); }
        }

        private static void WriteJsonAtomic(string path, string json)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private BuildTemplate GetBestBuildTemplate()
        {
            if (_bestBuildCacheFrame == Time.frameCount
                && _bestBuildCacheHero == _detectedHero
                && _bestBuildCacheBuild == _selectedBuildName
                && _bestBuildCacheBuildsVersion == _buildsVersion)
                return _bestBuildCache;

            BuildTemplate bestBuild = null;
            if (!string.IsNullOrEmpty(_selectedBuildName))
                bestBuild = _builds.Find(b => b.BuildName == _selectedBuildName);
            if (bestBuild == null)
            {
                RefreshRecommendationMatchesIfDue();
                if (_matchResults != null && _matchResults.Count > 0)
                    bestBuild = _matchResults[0].Template;
            }
            if (bestBuild == null)
            {
                var matchResults = MatchBuilds(_detectedHero, GatherAllItemNames());
                if (matchResults.Count > 0) bestBuild = matchResults[0].Template;
            }

            _bestBuildCache = bestBuild;
            _bestBuildCacheFrame = Time.frameCount;
            _bestBuildCacheHero = _detectedHero;
            _bestBuildCacheBuild = _selectedBuildName;
            _bestBuildCacheBuildsVersion = _buildsVersion;
            return bestBuild;
        }

        private List<BuildMatchResult> MatchBuilds(string heroName, List<string> currentItems)
        {
            var results = new List<BuildMatchResult>();
            var ownedSet = new HashSet<string>(currentItems, StringComparer.OrdinalIgnoreCase);
            var ownedSkills = GatherPlayerSkillNames();
            var skillSet = new HashSet<string>(ownedSkills, StringComparer.OrdinalIgnoreCase);

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
                    SkillCoreOwned = sco, SkillCoreMissing = scm, SkillFlexOwned = sfo, SkillFlexMissing = sfm
                });
            }
            results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
            return results;
        }

        private List<string> GatherPlayerSkillNames()
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            var cam = Camera.main ?? Camera.current;
            try
            {
#pragma warning disable 0618
                var renderers = UnityEngine.Object.FindObjectsOfType<TheBazaar.SkillProxyRenderer>();
#pragma warning restore 0618
                if (renderers != null)
                {
                    foreach (var r in renderers)
                    {
                        try
                        {
                            if (r == null || r.Card == null) continue;
                            if (!IsPlayerSkillTransform(r.transform, cam)) continue;
                            var name = GetCardNameStatic(r.Card);
                            if (string.IsNullOrEmpty(name) || name == "???") continue;
                            if (seen.Add(name)) result.Add(name);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        // ==================== ??? ====================

        private static bool IsChineseChar(char c) { return c >= 0x4e00 && c <= 0x9fff; }
        private static bool HasChinese(string s) { foreach (var c in s) if (IsChineseChar(c)) return true; return false; }

        private void LoadTranslations()
        {
            // 从 JSON 文件加载翻译（由 extract_translations.py 从游戏缓存生成）
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "translations_zh_cn.json");
                if (!File.Exists(path))
                    path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "translations_zh_cn.json"); // 旧路径回退
                if (!File.Exists(path))
                    path = Path.Combine(Path.GetDirectoryName(typeof(BazaarBoardReaderPlugin).Assembly.Location), "translations_zh_cn.json");
                if (File.Exists(path))
                {
                    var wrapper = JsonConvert.DeserializeObject<TranslationData>(File.ReadAllText(path, Encoding.UTF8));
                    if (wrapper != null && wrapper.by_name != null)
                    {
                        _translations = wrapper.by_name;
                        // 构建反向翻译字典（中文→英文），用于阵容管理中用户输入中文时转回英文
                        _reverseTranslations.Clear();
                        foreach (var kv in _translations)
                        {
                            if (!string.IsNullOrEmpty(kv.Value) && !_reverseTranslations.ContainsKey(kv.Value))
                                _reverseTranslations[kv.Value] = kv.Key;
                        }
                        _logger.LogInfo(string.Format("[BoardReader] translations loaded: {0}, reverse: {1}", _translations.Count, _reverseTranslations.Count));
                    }
                }
                else _logger.LogWarning("[BoardReader] translations_zh_cn.json not found");
            }
            catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 翻译失败: {0}", ex)); }
        }

        // ==================== 商店推荐 ====================

        private void LoadShopRecommendationData()
        {
            try
            {
                var dataDir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
                // 加载 cards.json
                var cardsPath = Path.Combine(dataDir, "cards.json");
                if (File.Exists(cardsPath))
                {
                    var wrapper = JsonConvert.DeserializeObject<CardsData>(File.ReadAllText(cardsPath, Encoding.UTF8));
                    if (wrapper != null && wrapper.cards != null)
                    {
                        foreach (var kv in wrapper.cards)
                        {
                            var c = kv.Value; if (c == null) continue;
                            // 过滤：排除DEBUG/TEMPLATE/Package/技能
                            if (kv.Key.Contains("[DEBUG]") || kv.Key.Contains("[TEMPLATE]")) continue;
                            if (c.hidden_tags != null && c.hidden_tags.Contains("Package")) continue;
                            if (c.type == "Skill") continue;
                            // 社区团队测试卡
                            if (kv.Key.IndexOf("[Community Team]", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            // 黑名单：初始Core/无效/抽奖物品
                            string nameLower = (c.internal_name ?? "").ToLower();
                            if (nameLower == "armored core" || nameLower == "companion core" || nameLower == "critical core"
                                || nameLower == "focused core" || nameLower == "ignition core" || nameLower == "launcher core"
                                || nameLower == "the core" || nameLower == "weaponized core" || nameLower == "oblivion core"
                                || nameLower == "assembly line" || nameLower == "augment reagents" || nameLower == "unused card"
                                || nameLower == "magician's top hat" || nameLower == "blue gumball" || nameLower == "green gumball"
                                || nameLower == "red gumball" || nameLower == "yellow gumball") continue;
                            // Loot 水晶（探险奖品）
                            if (c.tags != null && c.tags.Contains("Loot") && c.internal_name != null && c.internal_name.Contains("Crystal")) continue;
                            // Package 奖励包
                            if (c.internal_name != null && c.internal_name.IndexOf("'s Package", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            _cardDb[kv.Key.ToLower()] = c;
                            // 构建名称→tiers索引（用于天数-等级过滤，同时按 internal_name 和 key 索引）
                            if (c.internal_name != null && c.tiers != null)
                                _cardNameToTiers[c.internal_name.ToLower()] = c.tiers;
                            if (c.tiers != null)
                                _cardNameToTiers[kv.Key.ToLower()] = c.tiers;
                        }
                    }
                }
                // 加载卡牌描述（用于描述关键词匹配，如 MaxHealth）
                try
                {
                    var genPath = Path.Combine(dataDir, "cards_generated.json");
                    if (File.Exists(genPath))
                    {
                        var genJson = File.ReadAllText(genPath, Encoding.UTF8);
                        var genCards = JsonConvert.DeserializeObject<Dictionary<string, CardDescEntry>>(genJson);
                        if (genCards != null)
                        {
                            int descLoaded = 0;
                            foreach (var kv in genCards)
                            {
                                if (kv.Value == null || string.IsNullOrEmpty(kv.Value.internal_name)) continue;
                                var key = kv.Value.internal_name.ToLower();
                                if (_cardDb.ContainsKey(key))
                                {
                                    _cardDb[key].description = kv.Value.description ?? "";
                                    descLoaded++;
                                }
                                // 构建 template_id → internal_name 映射
                                if (!string.IsNullOrEmpty(kv.Value.template_id) && !_templateIdToName.ContainsKey(kv.Value.template_id))
                                    _templateIdToName[kv.Value.template_id] = kv.Value.internal_name;
                            }
                            _logger.LogInfo(string.Format("[BoardReader] 描述:{0} template_id映射:{1}", descLoaded, _templateIdToName.Count));
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(string.Format("[BoardReader] 描述加载失败: {0}", ex)); }

                // 加载 merchants.json
                var merchantsPath = Path.Combine(dataDir, "merchants.json");
                if (File.Exists(merchantsPath))
                {
                    var mlist = JsonConvert.DeserializeObject<List<MerchantEntry>>(File.ReadAllText(merchantsPath, Encoding.UTF8));
                    if (mlist != null)
                    {
                        foreach (var m in mlist)
                        {
                            if (!string.IsNullOrEmpty(m.name))
                                _merchants[m.name.ToLower()] = m;
                        }
                    }
                }
                _logger.LogInfo(string.Format("[BoardReader] 商人:{0} 卡:{1}", _merchants.Count, _cardDb.Count));
            }
            catch (Exception ex) { _logger.LogWarning(string.Format("[BoardReader] 数据加载: {0}", ex)); }
        }

        // 扩展标签匹配：精确 + Reference变体 + 描述关键词（如 MaxHealth→"max health"）
        private bool CardMatchesMerchantTags(CardDataEntry card, List<string> allCardTags, List<string> merchantTags)
        {
            if (merchantTags == null || merchantTags.Count == 0) return true;

            foreach (var mt in merchantTags)
            {
                // 1. 精确匹配
                if (allCardTags.Any(ct => ct.Equals(mt, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // 2. Reference 变体（如 HealReference 匹配 merchant tag="Heal"，Health 不匹配 Heal）
                string refTag = mt + "Reference";
                if (allCardTags.Any(ct => ct.Equals(refTag, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // 2b. 复数→单数 (Toys→Toy, Friends→Friend)
                if (mt.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                {
                    string singular = mt.Substring(0, mt.Length - 1);
                    if (allCardTags.Any(ct => ct.Equals(singular, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    if (allCardTags.Any(ct => ct.Equals(singular + "Reference", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // 3. MaxHealth → Health 系列映射
                if (mt.Equals("MaxHealth", StringComparison.OrdinalIgnoreCase))
                {
                    if (allCardTags.Any(ct => ct.StartsWith("Health", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // 4. 描述关键词匹配（MaxHealth 等卡片无对应标签的）
                if (!string.IsNullOrEmpty(card.description))
                {
                    var descLower = card.description.ToLower();
                    if (mt.Equals("MaxHealth", StringComparison.OrdinalIgnoreCase))
                    {
                        if (descLower.Contains("max health"))
                            return true;
                    }
                }
            }
            return false;
        }

        // 判断卡牌在当前天数是否可能出现在商店
        // 等级概率权重表
        private static readonly Dictionary<int, Dictionary<string, float>> TierProbByDay = new Dictionary<int, Dictionary<string, float>>
        {
            {1, new Dictionary<string,float>{{"Bronze",1f},{"Silver",0f},{"Gold",0f},{"Diamond",0f}}},
            {2, new Dictionary<string,float>{{"Bronze",0.9f},{"Silver",0.1f},{"Gold",0f},{"Diamond",0f}}},
            {3, new Dictionary<string,float>{{"Bronze",0.7f},{"Silver",0.3f},{"Gold",0f},{"Diamond",0f}}},
            {4, new Dictionary<string,float>{{"Bronze",0.5f},{"Silver",0.5f},{"Gold",0f},{"Diamond",0f}}},
            {5, new Dictionary<string,float>{{"Bronze",0.25f},{"Silver",0.75f},{"Gold",0f},{"Diamond",0f}}},
            {6, new Dictionary<string,float>{{"Bronze",0f},{"Silver",0.95f},{"Gold",0.05f},{"Diamond",0f}}},
            {7, new Dictionary<string,float>{{"Bronze",0f},{"Silver",0.8f},{"Gold",0.2f},{"Diamond",0f}}},
            {8, new Dictionary<string,float>{{"Bronze",0f},{"Silver",0.45f},{"Gold",0.5f},{"Diamond",0.05f}}},
            {9, new Dictionary<string,float>{{"Bronze",0f},{"Silver",0.35f},{"Gold",0.55f},{"Diamond",0.1f}}},
            {10,new Dictionary<string,float>{{"Bronze",0f},{"Silver",0.2f},{"Gold",0.65f},{"Diamond",0.15f}}},
        };

        private float GetWeightedScore(List<string> items, int baseScore)
        {
            float total = 0f;
            foreach (var item in items)
            {
                var key = item.ToLower();
                CardDataEntry card;
                if (_cardDb.TryGetValue(key, out card))
                    total += baseScore * GetCardWeight(card, _currentDay);
                else
                    total += baseScore; // 未找到卡牌默认满分
            }
            return total;
        }

        private float GetCardWeight(CardDataEntry card, int day)
        {
            if (day < 1 || day > 10) return 1f;
            if (card.tiers == null || card.tiers.Count == 0) return 0f;
            var probs = TierProbByDay[day];
            float bestProb = 0f;
            foreach (var tier in card.tiers)
            {
                float p;
                if (probs.TryGetValue(tier, out p) && p > bestProb) bestProb = p;
            }
            if (bestProb >= 0.5f) return 1f;
            if (bestProb >= 0.2f) return 0.5f;
            if (bestProb > 0f) return 0.25f;
            return 0f;
        }

        private bool CardCanAppearOnDay(CardDataEntry card, int day)
        {
            if (day < 1) return true;
            if (card.tiers == null || card.tiers.Count == 0) return false;
            foreach (var tier in card.tiers)
            {
                int minDay;
                if (TierMinDay.TryGetValue(tier, out minDay) && day >= minDay) return true;
            }
            return false;
        }

        // 根据英文名判断物品能否在当前天数出现
        private bool ItemCanAppearOnDay(string itemName, int day)
        {
            if (day < 1) return true;
            var key = itemName.ToLower();

            // 1. 先从 _cardNameToTiers 查找（按 internal_name 索引）
            List<string> tiers;
            if (_cardNameToTiers.TryGetValue(key, out tiers) && tiers.Count > 0)
            {
                foreach (var tier in tiers)
                {
                    int minDay;
                    if (TierMinDay.TryGetValue(tier, out minDay) && day >= minDay) return true;
                }
                return false; // 有tier信息但当前天数不满足
            }

            // 2. 再从 _cardDb 按 key 查找（兜底）
            CardDataEntry card;
            if (_cardDb.TryGetValue(key, out card) && card.tiers != null && card.tiers.Count > 0)
            {
                foreach (var tier in card.tiers)
                {
                    int minDay;
                    if (TierMinDay.TryGetValue(tier, out minDay) && day >= minDay) return true;
                }
                return false;
            }

            // 3. 遍历 _cardDb 按 internal_name 查找（兜底）
            foreach (var kv in _cardDb)
            {
                if (kv.Value.internal_name != null && kv.Value.internal_name.ToLower() == key
                    && kv.Value.tiers != null && kv.Value.tiers.Count > 0)
                {
                    foreach (var tier in kv.Value.tiers)
                    {
                        int minDay;
                        if (TierMinDay.TryGetValue(tier, out minDay) && day >= minDay) return true;
                    }
                    return false;
                }
            }

            return true; // 完全找不到tier信息，默认显示（不过滤）
        }

        private string RateShop(string shopName)
        {
            if (_merchants.Count == 0 || _cardDb.Count == 0) return null;
            var key = shopName.ToLower();
            // 去掉等级后缀 (Gold) (Silver) 等
            var parenIdx = key.IndexOf('(');
            if (parenIdx > 0) key = key.Substring(0, parenIdx).Trim();
            MerchantEntry merchant = null;
            _merchants.TryGetValue(key, out merchant);
            if (merchant == null) return null;
            if (IsSpecialShopWithoutDedicatedRecommendation(shopName) || IsSpecialShopWithoutDedicatedRecommendation(merchant.name)) return null;
            _logger.LogInfo(string.Format("[BoardReader] 匹配: {0} → {1}", shopName, merchant.name));
            if (string.IsNullOrEmpty(_detectedHero)) return null;

            // 动态构建该英雄在此商店的物品池
            var pool = new List<string>();
            // 中立商店
            bool isNeutralShop = merchant.tags != null && merchant.tags.Any(t => t.Equals("Neutral", StringComparison.OrdinalIgnoreCase));
            // The Tester: 出售所有英雄的科技物品
            bool isCrossHero = merchant.cross_hero || merchant.name.Equals("The Tester", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in _cardDb)
            {
                var card = kv.Value;
                // 所有商店不卖传说物品
                if (card.tiers != null && card.tiers.Contains("Legendary")) continue;

                var cardHeroes = card.heroes ?? new List<string>();
                bool cardIsNeutral = cardHeroes.Count == 1 && cardHeroes[0].Equals("Common", StringComparison.OrdinalIgnoreCase);

                // 中立物品只在专门的中立商店或 cross_hero 商店出现
                if (cardIsNeutral && !isNeutralShop && !isCrossHero) continue;
                // 中立商店：仅卖中立物品
                if (isNeutralShop && !cardIsNeutral) continue;

                // The Antiquarian: 仅 VAN PYG DOO MAK KAR
                if (merchant.name.Equals("The Antiquarian", StringComparison.OrdinalIgnoreCase))
                {
                    string[] antiqAllowed = { "Vanessa", "Pygmalien", "Dooley", "Mak", "Karnok" };
                    if (!antiqAllowed.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;
                }

                // 英雄过滤（非中立、非中立商店）
                if (!cardIsNeutral && !isNeutralShop)
                {
                    if (isCrossHero) { }
                    else if (merchant.heroes != null && merchant.heroes.Count == 1 && merchant.heroes[0].Equals("Common", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cardHeroes.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    else
                    {
                        if (!cardHeroes.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;
                        if (merchant.heroes == null || !merchant.heroes.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                }

                // 品质商店 (Silvia/Goldie/Luxe)
                string shopLower = merchant.name.ToLower();
                if (shopLower == "silvia" || shopLower == "goldie" || shopLower == "luxe")
                {
                    string allowedTier = shopLower == "silvia" ? "Silver" : shopLower == "goldie" ? "Gold" : "Diamond";
                    if (card.tiers == null || !card.tiers.Any(t => t.Equals(allowedTier, StringComparison.OrdinalIgnoreCase))) continue;
                }

                // size过滤
                if (!string.IsNullOrEmpty(merchant.size))
                {
                    var sizes = merchant.size.Split(',').Select(s => s.Trim()).ToList();
                    if (!sizes.Any(s => (card.size ?? "").Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
                }
                // 合并标签（显式tags + hidden_tags，后者含功能关键字如Burn/Shield/Heal等）
                var allCardTags = new List<string>();
                if (card.tags != null) allCardTags.AddRange(card.tags);
                if (card.hidden_tags != null) allCardTags.AddRange(card.hidden_tags);
                // tags过滤（精确 + Reference变体 + 描述关键词）
                if (merchant.tags != null && merchant.tags.Count > 0)
                {
                    if (!CardMatchesMerchantTags(card, allCardTags, merchant.tags)) continue;
                }
                // 排除标签
                if (merchant.exclude_tags != null && merchant.exclude_tags.Count > 0)
                {
                    if (merchant.exclude_tags.Any(t => allCardTags.Contains(t))) continue;
                }
                // 天数-等级过滤：当前天数刷不出的物品不计入
                if (!CardCanAppearOnDay(card, _currentDay)) continue;
                pool.Add(kv.Value.internal_name ?? kv.Key);
            }
            if (pool.Count == 0) return null;

            var bestBuild = GetBestBuildTemplate();
            if (bestBuild == null) return null;

            // 构建小写物品池，统计阵容物品在该商店的占比
            var poolLower = new HashSet<string>();
            foreach (var p in pool) poolLower.Add(p.ToLower());
            int buildItemsInPool = 0;
            foreach (var item in bestBuild.CoreItems)
                if (poolLower.Contains(item.ToLower())) buildItemsInPool++;
            foreach (var item in bestBuild.FlexItems)
                if (poolLower.Contains(item.ToLower())) buildItemsInPool++;

            // 收集已拥有物品及品质
            var ownedTiers = new Dictionary<string, string>();
            foreach (var kv in GatherAllItemsWithTier())
                ownedTiers[kv.Key.ToLower()] = kv.Value ?? "Bronze";

            // 分类：已拥有非钻石 → *前缀（50%透明），未拥有 → 正常显示，钻石 → 不显示
            var coreOwned = new List<string>();    // 已拥有未钻石 → *前缀
            var coreMissing = new List<string>();  // 未拥有 → 正常
            var flexOwned = new List<string>();
            var flexMissing = new List<string>();
            foreach (var item in bestBuild.CoreItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiers.TryGetValue(item.ToLower(), out tier) && tier == "Diamond")
                    continue; // 已钻石不显示
                if (ownedTiers.ContainsKey(item.ToLower()))
                    coreOwned.Add(item);   // 已拥有未钻石 → 可升级
                else
                    coreMissing.Add(item); // 未拥有 → 需要获取
            }
            foreach (var item in bestBuild.FlexItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiers.TryGetValue(item.ToLower(), out tier) && tier == "Diamond")
                    continue;
                if (ownedTiers.ContainsKey(item.ToLower()))
                    flexOwned.Add(item);
                else
                    flexMissing.Add(item);
            }

            bool coreEmpty = coreOwned.Count == 0 && coreMissing.Count == 0;
            bool flexEmpty = flexOwned.Count == 0 && flexMissing.Count == 0;

            int totalHits = coreOwned.Count + coreMissing.Count + flexOwned.Count + flexMissing.Count;
            var hitItems = new List<string>();
            hitItems.AddRange(coreOwned); hitItems.AddRange(coreMissing); hitItems.AddRange(flexOwned); hitItems.AddRange(flexMissing);
            int hitPct = CalculateWeightedHitPercent(pool, hitItems, false);
            float score = GetWeightedScore(coreMissing, 3) + GetWeightedScore(coreOwned, 3)
                        + GetWeightedScore(flexMissing, 1) + GetWeightedScore(flexOwned, 1);
            var rec = GetEventRecommendation(shopName);
            if (totalHits <= 0) return BuildNoMatchRating(pool.Count, rec);
            string stars = (score >= 9f ? "\u2605\u2605\u2605" : score >= 6f ? "\u2605\u2605" : "\u2605") + " " + hitPct + "% (" + totalHits + "/" + pool.Count + ")";
            var lines = new List<string> { stars };
            var coreLine = new List<string>();
            coreLine.AddRange(TranslateEach(coreMissing));
            coreLine.AddRange(TranslateEach(coreOwned).Select(s => "*" + s));
            lines.Add(coreLine.Count > 0
                ? string.Join(",", coreLine.Take(4).ToArray()) + (coreLine.Count > 4 ? "..." : "")
                : "-");
            var flexLine = new List<string>();
            flexLine.AddRange(TranslateEach(flexMissing));
            flexLine.AddRange(TranslateEach(flexOwned).Select(s => "*" + s));
            lines.Add(flexLine.Count > 0
                ? string.Join(",", flexLine.Take(4).ToArray()) + (flexLine.Count > 4 ? "..." : "")
                : "-");
            // 商店推荐文本
            if (!string.IsNullOrEmpty(rec))
                lines.Add("\u25c6 " + rec);
            return string.Join("\n", lines.ToArray());
        }

        // 事件评分（item_reward 等）
        private Dictionary<string, object> _eventLookup = null;
        private void LoadEventData()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "events.json");
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path, Encoding.UTF8);
                var raw = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(json);
                _eventLookup = new Dictionary<string, object>();
                if (raw != null)
                {
                    foreach (var kv in raw)
                        foreach (var evt in kv.Value)
                        {
                            var name = evt.ContainsKey("name") ? evt["name"].ToString() : "";
                            if (!string.IsNullOrEmpty(name))
                                _eventLookup[name.ToLower()] = evt;
                        }
                }
                _logger.LogInfo(string.Format("[BoardReader] event data loaded: {0}", _eventLookup.Count));
            }
            catch (Exception ex) { _logger.LogWarning("[BoardReader] 事件加载: " + ex.Message); }
        }

        private bool IsSpecialShopWithoutDedicatedRecommendation(string name)
        {
            var baseName = NormalizeEventBaseName(name);
            if (baseName == "Gumball Machine"
                || baseName == "Likit"
                || baseName == "Eating Contest"
                || baseName == "Pearl's Dig Site"
                || baseName == "The Travel Agent")
                return true;
            return false;
        }

        private bool IsSpecialShopWithoutDedicatedRecommendation(Dictionary<string, object> evt, string shopName)
        {
            if (IsSpecialShopWithoutDedicatedRecommendation(shopName)) return true;
            if (evt == null) return false;
            if (evt.ContainsKey("name") && IsSpecialShopWithoutDedicatedRecommendation(evt["name"] != null ? evt["name"].ToString() : "")) return true;
            if (evt.ContainsKey("source_internal_name") && IsSpecialShopWithoutDedicatedRecommendation(evt["source_internal_name"] != null ? evt["source_internal_name"].ToString() : "")) return true;
            return false;
        }
        private bool IsLootRewardEvent(Dictionary<string, object> evt, string shopName)
        {
            var names = new List<string>();
            if (!string.IsNullOrEmpty(shopName)) names.Add(shopName);
            if (evt != null)
            {
                if (evt.ContainsKey("name") && evt["name"] != null) names.Add(evt["name"].ToString());
                if (evt.ContainsKey("source_internal_name") && evt["source_internal_name"] != null) names.Add(evt["source_internal_name"].ToString());
                if (evt.ContainsKey("notes") && evt["notes"] != null)
                {
                    var notes = evt["notes"].ToString();
                    if (notes.IndexOf("Loot item", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            foreach (var raw in names)
            {
                var baseName = NormalizeEventBaseName(raw);
                if (baseName == "Extract Extract"
                    || baseName == "Scrap Salvage"
                    || baseName == "Sharpening Kit"
                    || baseName == "Moonlit Meadow"
                    || baseName == "Cinder Chase"
                    || baseName == "Procure Medkit")
                    return true;
            }
            return false;
        }

        private string NormalizeEventBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var result = name;
            if (result.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - 7);
            var dash = result.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0 && dash + 3 < result.Length)
                result = result.Substring(dash + 3);
            var paren = result.IndexOf('(');
            if (paren > 0)
                result = result.Substring(0, paren);
            return result.Trim();
        }
        private string RateLevelUpReward(string shopName)
        {
            if (_cardDb.Count == 0 || string.IsNullOrEmpty(_detectedHero)) return null;

            CardDataEntry evtCard;
            var eventName = ResolveLevelUpEventName(shopName, out evtCard);
            if (evtCard == null || string.IsNullOrEmpty(eventName)) return null;
            if (!string.Equals(evtCard.type, "EventEncounter", StringComparison.OrdinalIgnoreCase)) return null;
            if (eventName.IndexOf("Level Up", StringComparison.OrdinalIgnoreCase) < 0) return null;

            // 升级奖励里的战利品不显示构筑推荐。
            if (eventName.IndexOf(" Loot", StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("Loot ", StringComparison.OrdinalIgnoreCase) >= 0
                || ((evtCard.display_name ?? "").IndexOf("战利品", StringComparison.OrdinalIgnoreCase) >= 0))
                return null;

            // Named rewards such as Adira/Bjorn/Zara are skill-learning rewards; keep them skipped
            // until the skill recommendation module is added.
            bool isFriend = eventName.IndexOf("BeFriend", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isItemReward = eventName.IndexOf(" item", StringComparison.OrdinalIgnoreCase) >= 0;
            var rewardTags = GetLevelUpRewardTags(eventName, evtCard);
            if (isFriend && !rewardTags.Any(t => t.Equals("Friend", StringComparison.OrdinalIgnoreCase)))
                rewardTags.Add("Friend");
            if (!isFriend && !isItemReward && rewardTags.Count == 0) return null;

            var allowedEventHeroes = evtCard.heroes ?? new List<string>();
            if (allowedEventHeroes.Count > 0
                && !allowedEventHeroes.Any(h => h.Equals("Common", StringComparison.OrdinalIgnoreCase))
                && !allowedEventHeroes.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase)))
                return null;

            var allowedTiers = GetLevelUpRewardTiers(eventName, evtCard.starting_tier);
            if (allowedTiers.Count == 0) return null;

            var pool = new List<string>();
            foreach (var kv in _cardDb)
            {
                var card = kv.Value;
                if (card == null) continue;
                if (!string.Equals(card.type, "Item", StringComparison.OrdinalIgnoreCase)) continue;
                if (card.tiers == null || card.tiers.Count == 0) continue;
                if (card.tiers.Any(t => t.Equals("Legendary", StringComparison.OrdinalIgnoreCase))) continue;
                if (!allowedTiers.Any(tier => card.tiers.Any(t => t.Equals(tier, StringComparison.OrdinalIgnoreCase)))) continue;

                var ch = card.heroes ?? new List<string>();
                if (!ch.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;

                if (rewardTags.Count > 0)
                {
                    var allTags = new List<string>();
                    if (card.tags != null) allTags.AddRange(card.tags);
                    if (card.hidden_tags != null) allTags.AddRange(card.hidden_tags);
                    if (!rewardTags.Any(tag => allTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)
                        || t.Equals(tag + "Reference", StringComparison.OrdinalIgnoreCase)))) continue;
                }

                pool.Add(card.internal_name ?? kv.Key);
            }
            return RenderBuildPoolRating(pool, null, true);
        }

        private List<string> GetLevelUpRewardTags(string eventName, CardDataEntry evtCard)
        {
            var tags = new List<string>();
            Action<string> add = delegate(string tag)
            {
                if (!string.IsNullOrEmpty(tag) && !tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) tags.Add(tag);
            };
            var text = ((eventName ?? "") + " " + (evtCard.display_name ?? "") + " " + (evtCard.description ?? "")).ToLowerInvariant();

            if (text.Contains("tool up") || text.Contains("tool") || text.Contains("工具")) add("Tool");
            if (text.Contains("rageforge") || text.Contains("burn") || text.Contains("灼烧")) add("Burn");
            if (text.Contains("arms locker") || text.Contains("weapon") || text.Contains("武器")) add("Weapon");
            if (text.Contains("potion rack") || text.Contains("potion") || text.Contains("药水")) add("Potion");
            if (text.Contains("reagent shelf") || text.Contains("reagent") || text.Contains("原料") || text.Contains("试剂")) add("Reagent");
            if (text.Contains("ammo cache") || text.Contains("ammo") || text.Contains("弹药")) add("Ammo");
            if (text.Contains("meal prep") || text.Contains("food") || text.Contains("食物")) add("Food");
            if (text.Contains("go fishing") || text.Contains("aquatic") || text.Contains("水系")) add("Aquatic");
            if (text.Contains("take flight") || text.Contains("flying") || text.Contains("飞行")) add("Flying");
            if (text.Contains("coin collector") || text.Contains("economic") || text.Contains("经济")) add("Economic");
            if (text.Contains("candy collector") || text.Contains("toy") || text.Contains("玩具")) add("Toy");
            if (text.Contains("befriend") || text.Contains("friend") || text.Contains("朋友") || text.Contains("伙伴")) add("Friend");
            if (text.Contains("property") || text.Contains("地产")) add("Property");
            if (text.Contains("shield") || text.Contains("护盾")) add("Shield");
            if (text.Contains("poison") || text.Contains("毒")) add("Poison");
            if (text.Contains("freeze") || text.Contains("冻结")) add("Freeze");
            if (text.Contains("haste") || text.Contains("加速")) add("Haste");
            if (text.Contains("slow") || text.Contains("减速")) add("Slow");
            if (text.Contains("heal") || text.Contains("治疗")) add("Heal");
            if (text.Contains("regen") || text.Contains("回复")) add("Regen");
            if (text.Contains("tech") || text.Contains("科技")) add("Tech");
            return tags;
        }

        private string ResolveLevelUpEventName(string shopName, out CardDataEntry evtCard)
        {
            evtCard = null;
            if (string.IsNullOrEmpty(shopName)) return null;

            var candidates = new List<string>();
            candidates.Add(shopName);
            if (shopName.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                candidates.Add(shopName.Substring(0, shopName.Length - 7));

            string mapped;
            var lower = shopName.ToLower();
            if (_tidToName.TryGetValue(shopName, out mapped)) candidates.Add(mapped);
            if (_tidToName.TryGetValue(lower, out mapped)) candidates.Add(mapped);
            if (_templateIdToName.TryGetValue(shopName, out mapped)) candidates.Add(mapped);
            if (_templateIdToName.TryGetValue(lower, out mapped)) candidates.Add(mapped);

            foreach (var raw in candidates)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var key = raw.ToLower();
                CardDataEntry card;
                if (_cardDb.TryGetValue(key, out card)) { evtCard = card; return card.internal_name ?? raw; }
                if (raw.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
                {
                    var noClone = raw.Substring(0, raw.Length - 7).ToLower();
                    if (_cardDb.TryGetValue(noClone, out card)) { evtCard = card; return card.internal_name ?? raw; }
                }
            }
            return null;
        }

        private List<string> GetLevelUpRewardTiers(string eventName, string startingTier)
        {
            var tiers = new List<string>();
            if (eventName.IndexOf("Gold or Diamond", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                tiers.Add("Gold"); tiers.Add("Diamond"); return tiers;
            }
            foreach (var tier in new[] { "Bronze", "Silver", "Gold", "Diamond" })
            {
                if (eventName.IndexOf(tier, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tiers.Add(tier);
                    return tiers;
                }
            }
            if (!string.IsNullOrEmpty(startingTier)) tiers.Add(NormalizeTierStr(startingTier));
            return tiers;
        }

        private CardDataEntry FindCardEntryByName(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            CardDataEntry card;
            if (_cardDb.TryGetValue(itemName.ToLower(), out card)) return card;
            foreach (var kv in _cardDb)
            {
                var c = kv.Value;
                if (c != null && !string.IsNullOrEmpty(c.internal_name)
                    && c.internal_name.Equals(itemName, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        private float GetRatingWeight(string itemName, bool fixedWeights)
        {
            if (fixedWeights) return 1f;
            var card = FindCardEntryByName(itemName);
            if (card == null) return 1f;
            var w = GetCardWeight(card, _currentDay);
            return w > 0f ? w : 0f;
        }

        private int CalculateWeightedHitPercent(List<string> pool, List<string> hitItems, bool fixedWeights)
        {
            if (pool == null || pool.Count == 0) return 0;
            var hitSet = new HashSet<string>(hitItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            float totalWeight = 0f;
            float hitWeight = 0f;
            foreach (var item in pool)
            {
                var w = GetRatingWeight(item, fixedWeights);
                totalWeight += w;
                if (hitSet.Contains(item)) hitWeight += w;
            }
            if (totalWeight <= 0f)
                return hitItems != null && pool.Count > 0 ? (int)(hitItems.Count * 100f / pool.Count) : 0;
            return Mathf.Clamp((int)(hitWeight * 100f / totalWeight), 0, 100);
        }

        private bool IsExcavationName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return NormalizeEventBaseName(name).Equals("Excavation", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("\u6316\u6398\u884c\u52a8", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddRewardTag(List<string> tags, string tag)
        {
            if (tags == null || string.IsNullOrEmpty(tag)) return;
            if (!tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) tags.Add(tag);
        }

        private void AddRewardTagsFromNotes(List<string> tags, string notes)
        {
            if (string.IsNullOrEmpty(notes)) return;
            var lower = notes.ToLowerInvariant();
            if (lower.Contains("relic") || notes.Contains("\u9057\u7269")) AddRewardTag(tags, "Relic");
            if (lower.Contains("tool") || notes.Contains("\u5de5\u5177")) AddRewardTag(tags, "Tool");
            if (lower.Contains("weapon") || notes.Contains("\u6b66\u5668")) AddRewardTag(tags, "Weapon");
            if (lower.Contains("friend") || notes.Contains("\u4f19\u4f34") || notes.Contains("\u670b\u53cb")) AddRewardTag(tags, "Friend");
            if (lower.Contains("potion") || notes.Contains("\u836f\u6c34")) AddRewardTag(tags, "Potion");
            if (lower.Contains("food") || notes.Contains("\u98df\u7269")) AddRewardTag(tags, "Food");
            if (lower.Contains("aquatic") || notes.Contains("\u6c34\u7cfb")) AddRewardTag(tags, "Aquatic");
            if (lower.Contains("tech") || notes.Contains("\u79d1\u6280")) AddRewardTag(tags, "Tech");
        }

        private bool IsAnyHeroReward(Dictionary<string, object> evt, string shopName)
        {
            if (IsExcavationName(shopName)) return true;
            if (evt != null)
            {
                if (evt.ContainsKey("name") && IsExcavationName(evt["name"] != null ? evt["name"].ToString() : "")) return true;
                if (evt.ContainsKey("notes") && evt["notes"] != null)
                {
                    var notes = evt["notes"].ToString().ToLowerInvariant();
                    if (notes.Contains("from any hero") || notes.Contains("any hero")) return true;
                }
            }
            return false;
        }

        private string GetEventName(Dictionary<string, object> evt)
        {
            if (evt != null && evt.ContainsKey("name") && evt["name"] != null) return evt["name"].ToString();
            return "";
        }

        private string BuildNoMatchRating(int poolCount, string rec)
        {
            var lines = new List<string>();
            lines.Add("\u2606\u2606\u2606 0% (0/" + Math.Max(0, poolCount) + ")");
            lines.Add("-");
            lines.Add("-");
            if (!string.IsNullOrEmpty(rec)) lines.Add("\u25c6 " + rec);
            return string.Join("\n", lines.ToArray());
        }

        private string ExtractTierFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var lower = text.ToLowerInvariant();
            if (lower.Contains("bronze") || text.Contains("\u94dc") || text.Contains("\u9752\u94dc")) return "Bronze";
            if (lower.Contains("silver") || text.Contains("\u94f6") || text.Contains("\u767d\u94f6")) return "Silver";
            if (lower.Contains("gold") || text.Contains("\u91d1") || text.Contains("\u9ec4\u91d1")) return "Gold";
            if (lower.Contains("diamond") || text.Contains("\u94bb") || text.Contains("\u94bb\u77f3")) return "Diamond";
            return null;
        }

        private string RenderBuildPoolRating(List<string> pool, string rec, bool fixedWeights = false)
        {
            if (pool == null || pool.Count == 0) return null;

            var bestBuild = GetBestBuildTemplate();
            if (bestBuild == null) return null;

            var ownedTiers = new Dictionary<string, string>();
            foreach (var kv in GatherAllItemsWithTier())
                ownedTiers[kv.Key.ToLower()] = kv.Value ?? "Bronze";

            var poolLower = new HashSet<string>();
            foreach (var p in pool) poolLower.Add(p.ToLower());
            var coreHits = new List<string>(); var coreOwned = new List<string>();
            var flexHits = new List<string>(); var flexOwned = new List<string>();

            foreach (var item in bestBuild.CoreItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiers.TryGetValue(item.ToLower(), out tier) && tier == "Diamond") continue;
                if (ownedTiers.ContainsKey(item.ToLower())) coreOwned.Add(item);
                else coreHits.Add(item);
            }
            foreach (var item in bestBuild.FlexItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiers.TryGetValue(item.ToLower(), out tier) && tier == "Diamond") continue;
                if (ownedTiers.ContainsKey(item.ToLower())) flexOwned.Add(item);
                else flexHits.Add(item);
            }

            int needHits = coreHits.Count + coreOwned.Count + flexHits.Count + flexOwned.Count;
            var hitItems = new List<string>();
            hitItems.AddRange(coreHits); hitItems.AddRange(coreOwned); hitItems.AddRange(flexHits); hitItems.AddRange(flexOwned);
            int hitPct = CalculateWeightedHitPercent(pool, hitItems, fixedWeights);
            float score = GetWeightedScore(coreHits, 3) + GetWeightedScore(coreOwned, 3)
                        + GetWeightedScore(flexHits, 1) + GetWeightedScore(flexOwned, 1);
            var lines = new List<string>();
            if (needHits <= 0) return BuildNoMatchRating(pool.Count, rec);
            lines.Add((score >= 9f ? "\u2605\u2605\u2605" : score >= 6f ? "\u2605\u2605" : "\u2605") + " " + hitPct + "% (" + needHits + "/" + pool.Count + ")");

            var cl = new List<string>();
            cl.AddRange(TranslateEach(coreHits));
            cl.AddRange(TranslateEach(coreOwned).Select(s => "*" + s));
            lines.Add(cl.Count > 0 ? string.Join(",", cl.Take(4).ToArray()) + (cl.Count > 4 ? "..." : "") : "-");

            var fl = new List<string>();
            fl.AddRange(TranslateEach(flexHits));
            fl.AddRange(TranslateEach(flexOwned).Select(s => "*" + s));
            lines.Add(fl.Count > 0 ? string.Join(",", fl.Take(4).ToArray()) + (fl.Count > 4 ? "..." : "") : "-");
            if (!string.IsNullOrEmpty(rec)) lines.Add("\u25c6 " + rec);
            return string.Join("\n", lines.ToArray());
        }
        private string RateEvent(string shopName)
        {
            if (_eventLookup == null) LoadEventData();
            if (_eventLookup == null || _eventLookup.Count == 0) return null;
            if (_cardDb.Count == 0 || string.IsNullOrEmpty(_detectedHero)) return null;

            var rawKey = (shopName ?? "").ToLower();
            var key = rawKey;
            object evtObj = null;
            if (!_eventLookup.TryGetValue(rawKey, out evtObj))
            {
                string mappedName;
                if (_tidToName.TryGetValue(shopName, out mappedName))
                    _eventLookup.TryGetValue(mappedName.ToLower(), out evtObj);
                if (evtObj == null && _tidToName.TryGetValue(rawKey, out mappedName))
                    _eventLookup.TryGetValue(mappedName.ToLower(), out evtObj);
            }
            if (evtObj == null)
            {
                var parenIdx = key.IndexOf('(');
                if (parenIdx > 0) key = key.Substring(0, parenIdx).Trim();
                if (!_eventLookup.TryGetValue(key, out evtObj))
                {
                    string mappedName;
                    if (_tidToName.TryGetValue(key, out mappedName))
                        _eventLookup.TryGetValue(mappedName.ToLower(), out evtObj);
                }
            }
            if (evtObj == null) return null;
            var evt = evtObj as Dictionary<string, object>;
            if (evt == null) return null;
            if (IsSpecialShopWithoutDedicatedRecommendation(evt, shopName)) return null;
            if (IsLootRewardEvent(evt, shopName)) return null;

            var eventName = GetEventName(evt);
            bool isExcavation = IsExcavationName(shopName) || IsExcavationName(eventName);
            var etype = evt.ContainsKey("event_type") ? evt["event_type"].ToString() : "";
            if (etype != "item_reward" && !isExcavation) return null;

            var tags = new List<string>();
            if (evt.ContainsKey("reward_tags"))
            {
                var tagList = evt["reward_tags"] as Newtonsoft.Json.Linq.JArray;
                if (tagList != null)
                    foreach (var t in tagList) AddRewardTag(tags, t.ToString());
            }
            if (evt.ContainsKey("notes") && evt["notes"] != null)
                AddRewardTagsFromNotes(tags, evt["notes"].ToString());
            if (isExcavation) AddRewardTag(tags, "Relic");

            bool hasCardReward = false;
            if (evt.ContainsKey("card_reward"))
            {
                var cardReward = evt["card_reward"] as Newtonsoft.Json.Linq.JObject;
                if (cardReward != null)
                {
                    bool enabled;
                    hasCardReward = bool.TryParse((cardReward["enabled"] ?? "").ToString(), out enabled) && enabled;
                    var rewardTags = cardReward["reward_tags"] as Newtonsoft.Json.Linq.JArray;
                    if (rewardTags != null)
                        foreach (var t in rewardTags) AddRewardTag(tags, t.ToString());
                }
            }
            if (!hasCardReward && tags.Count == 0 && !isExcavation) return null;

            bool anyHero = IsAnyHeroReward(evt, shopName);
            var forcedTier = ExtractTierFromText(shopName);
            if (string.IsNullOrEmpty(forcedTier)) forcedTier = ExtractTierFromText(eventName);
            bool fixedTier = !string.IsNullOrEmpty(forcedTier);

            var pool = new List<string>();
            foreach (var kv in _cardDb)
            {
                var card = kv.Value;
                if (card == null) continue;
                if (!string.Equals(card.type, "Item", StringComparison.OrdinalIgnoreCase)) continue;
                if (card.tiers != null && card.tiers.Contains("Legendary")) continue;
                var ch = card.heroes ?? new List<string>();
                bool cardIsNeutral = ch.Any(h => h.Equals("Common", StringComparison.OrdinalIgnoreCase));
                if (!anyHero && !cardIsNeutral && !ch.Any(h => h.Equals(_detectedHero, StringComparison.OrdinalIgnoreCase))) continue;

                if (!string.IsNullOrEmpty(forcedTier))
                {
                    if (card.tiers == null || !card.tiers.Any(t => t.Equals(forcedTier, StringComparison.OrdinalIgnoreCase))) continue;
                }
                else if (!CardCanAppearOnDay(card, _currentDay)) continue;

                if (tags.Count > 0)
                {
                    var allTags = new List<string>();
                    if (card.tags != null) allTags.AddRange(card.tags);
                    if (card.hidden_tags != null) allTags.AddRange(card.hidden_tags);
                    var allLower = allTags.Select(t => t.ToLower()).ToList();
                    var excludeR = tags.Where(t => t.StartsWith("!")).Select(t => t.Substring(1).ToLower()).ToList();
                    var includeR = tags.Where(t => !t.StartsWith("!")).Select(t => t.ToLower()).ToList();
                    string sizeFilter = null;
                    foreach (var sz in new[]{"small","medium","large"})
                        if (includeR.Contains(sz)) { sizeFilter = sz; includeR.Remove(sz); break; }
                    if (excludeR.Count > 0 && excludeR.Any(t => allLower.Contains(t))) continue;
                    if (sizeFilter != null && (card.size ?? "").ToLower() != sizeFilter) continue;
                    if (includeR.Count > 0 && !includeR.Any(t => allLower.Contains(t))) continue;
                }
                pool.Add(card.internal_name ?? kv.Key);
            }
            if (pool.Count == 0) return null;

            var bestBuild = GetBestBuildTemplate();
            var rec = GetEventRecommendation(shopName);
            if (bestBuild == null) return null;

            var ownedTiersEvt = new Dictionary<string, string>();
            foreach (var kv in GatherAllItemsWithTier())
                ownedTiersEvt[kv.Key.ToLower()] = kv.Value ?? "Bronze";

            var poolLower = new HashSet<string>();
            foreach (var p in pool) poolLower.Add(p.ToLower());
            var coreHits = new List<string>(); var coreOwned = new List<string>();
            var flexHits = new List<string>(); var flexOwned = new List<string>();
            foreach (var item in bestBuild.CoreItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiersEvt.TryGetValue(item.ToLower(), out tier) && tier == "Diamond") continue;
                if (ownedTiersEvt.ContainsKey(item.ToLower())) coreOwned.Add(item);
                else coreHits.Add(item);
            }
            foreach (var item in bestBuild.FlexItems)
            {
                if (!poolLower.Contains(item.ToLower())) continue;
                string tier;
                if (ownedTiersEvt.TryGetValue(item.ToLower(), out tier) && tier == "Diamond") continue;
                if (ownedTiersEvt.ContainsKey(item.ToLower())) flexOwned.Add(item);
                else flexHits.Add(item);
            }
            int needHitsEvt = coreHits.Count + coreOwned.Count + flexHits.Count + flexOwned.Count;
            var linesEvt = new List<string>();
            var hitItems = new List<string>();
            hitItems.AddRange(coreHits); hitItems.AddRange(coreOwned); hitItems.AddRange(flexHits); hitItems.AddRange(flexOwned);
            int hitPct = CalculateWeightedHitPercent(pool, hitItems, fixedTier);
            float scoreEvt = GetWeightedScore(coreHits, 3) + GetWeightedScore(coreOwned, 3)
                           + GetWeightedScore(flexHits, 1) + GetWeightedScore(flexOwned, 1);
            if (needHitsEvt <= 0) return BuildNoMatchRating(pool.Count, rec);
            linesEvt.Add((scoreEvt >= 9f ? "\u2605\u2605\u2605" : scoreEvt >= 6f ? "\u2605\u2605" : "\u2605") + " " + hitPct + "% (" + needHitsEvt + "/" + pool.Count + ")");
            var cl = new List<string>();
            cl.AddRange(TranslateEach(coreHits));
            cl.AddRange(TranslateEach(coreOwned).Select(si => "*" + si));
            linesEvt.Add(cl.Count > 0 ? string.Join(",", cl.Take(4).ToArray()) + (cl.Count > 4 ? "..." : "") : "-");
            var fl = new List<string>();
            fl.AddRange(TranslateEach(flexHits));
            fl.AddRange(TranslateEach(flexOwned).Select(si => "*" + si));
            linesEvt.Add(fl.Count > 0 ? string.Join(",", fl.Take(4).ToArray()) + (fl.Count > 4 ? "..." : "") : "-");
            if (!string.IsNullOrEmpty(rec)) linesEvt.Add("\u25c6 " + rec);
            return string.Join("\n", linesEvt.ToArray());
        }

        // ── 事件推荐文本 ──
        private Dictionary<string, string> _eventNotes = new Dictionary<string, string>();
        private Dictionary<string, string> _tidToName = new Dictionary<string, string>(); // template_id → event name

        private void LoadEventNotes()
        {
            try
            {
                var baseDir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
                // 加载备注
                var np = Path.Combine(baseDir, "event_notes.json");
                if (File.Exists(np))
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(np, Encoding.UTF8));
                    _eventNotes = new Dictionary<string, string>();
                    if (raw != null)
                        foreach (var kv in raw)
                        {
                            var k = kv.Key.ToLower();
                            _eventNotes[k] = kv.Value;
                            // 同时去掉后缀存储（Day 1-2, Bronze, etc）
                            var pi = k.IndexOf('(');
                            if (pi > 0)
                            {
                                var basek = k.Substring(0, pi).Trim();
                                if (!_eventNotes.ContainsKey(basek))
                                    _eventNotes[basek] = kv.Value;
                            }
                        }
                }
                // 加载 template_id → name 映射
                var tp = Path.Combine(baseDir, "cards_generated.json");
                if (File.Exists(tp))
                {
                    var gen = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(tp, Encoding.UTF8));
                    if (gen != null)
                        foreach (var kv in gen)
                        {
                            var d = kv.Value as Newtonsoft.Json.Linq.JObject;
                            if (d == null) continue;
                            var tid = d["template_id"]?.ToString();
                            var iname = d["internal_name"]?.ToString();
                            if (!string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(iname) && !_tidToName.ContainsKey(tid))
                                _tidToName[tid] = iname;
                        }
                }
                _logger.LogInfo(string.Format("[BoardReader] 备注:{0} tid映射:{1}", _eventNotes.Count, _tidToName.Count));
            }
            catch (Exception ex) { _logger.LogWarning("[BoardReader] 事件备注加载: " + ex.Message); }
        }

        private string GetEventRecommendation(string eventName, string templateId = null)
        {
            if (_eventNotes.Count == 0) return null;
            string lookupName = eventName;
            if (!string.IsNullOrEmpty(templateId) && _tidToName.TryGetValue(templateId, out var mapped))
                lookupName = mapped;

            // 去后缀: "Invest in Yourself (Day 1-2)" → "invest in yourself"
            var key = lookupName.ToLower();
            var parenIdx = key.IndexOf('(');
            if (parenIdx > 0) key = key.Substring(0, parenIdx).Trim();

            string note = null;
            // 1. 直接小写匹配
            if (!_eventNotes.TryGetValue(key, out note))
            {
                // 2. 翻译名小写匹配
                var zh = Translate(lookupName);
                if (!string.IsNullOrEmpty(zh) && zh != lookupName)
                    _eventNotes.TryGetValue(zh.ToLower(), out note);
            }
            if (string.IsNullOrEmpty(note)) return null;

            var lines = note.Split('\n');
            var matches = new List<string>();
            foreach (var line in lines)
            {
                var ci = line.IndexOf(':');
                if (ci < 0) ci = line.IndexOf('?');
                if (ci < 0) continue;
                var cond = line.Substring(0, ci).Trim().TrimStart('[').TrimEnd(']');
                var text = line.Substring(ci + 1).Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (EvalCondition(cond)) matches.Add(text);
            }
            return matches.Count > 0 ? string.Join(", ", matches) : null;
        }

        private bool EvalCondition(string cond)
        {
            if (string.IsNullOrEmpty(cond)) return true;
            // 金币<10 / 金币>20
            var m = System.Text.RegularExpressions.Regex.Match(cond, @"金币\s*([<>=]+)\s*(\d+)");
            if (m.Success) return CmpNum(GetGold(), m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            // 生命<500
            m = System.Text.RegularExpressions.Regex.Match(cond, @"生命\s*([<>=]+)\s*(\d+)");
            if (m.Success) return CmpNum(GetHealth(), m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            // 收入<5
            m = System.Text.RegularExpressions.Regex.Match(cond, @"收入\s*([<>=]+)\s*(\d+)");
            if (m.Success) return CmpNum(_currentIncome > 0 ? _currentIncome : GetGold(), m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            // 天数<5
            m = System.Text.RegularExpressions.Regex.Match(cond, @"天数\s*([<>=]+)\s*(\d+)");
            if (m.Success) return CmpNum(_currentDay, m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            // 声望<10
            m = System.Text.RegularExpressions.Regex.Match(cond, @"声望\s*([<>=]+)\s*(\d+)");
            if (m.Success) return CmpNum(_currentPrestige, m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            return false;
        }

        private int _gsGold = -1, _gsHealth = -1;
        private List<string> _gsActiveTids = new List<string>();
        private void RefreshGameStateCache()
        {
            try
            {
                if (Time.frameCount % 120 != 0) return;
                var gsPath = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data", "game_state.json");
                if (!File.Exists(gsPath)) return;
                var json = File.ReadAllText(gsPath, Encoding.UTF8);
                var gs = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (gs == null) return;
                if (gs.ContainsKey("gold")) _gsGold = Convert.ToInt32(gs["gold"]);
                if (gs.ContainsKey("health")) _gsHealth = Convert.ToInt32(gs["health"]);
                _gsActiveTids.Clear();
                if (gs.ContainsKey("event_option_template_ids"))
                {
                    var tids = gs["event_option_template_ids"] as Newtonsoft.Json.Linq.JArray;
                    if (tids != null)
                        foreach (var t in tids) _gsActiveTids.Add(t.ToString());
                }
            }
            catch { }
        }

        private int GetGold() { RefreshGameStateCache(); return _currentGold > 0 ? _currentGold : _gsGold; }
        private int GetHealth() { RefreshGameStateCache(); return _currentHealth > 0 ? _currentHealth : _gsHealth; }

        private bool CmpNum(int actual, string op, int target)
        {
            if (op == "<") return actual < target;
            if (op == ">") return actual > target;
            if (op == "<=") return actual <= target;
            if (op == ">=") return actual >= target;
            if (op == "==" || op == "=") return actual == target;
            return false;
        }

        // ==================== 翻译 ====================

        private List<string> TranslateEach(List<string> items) { var r = new List<string>(); foreach (var i in items) r.Add(Translate(i)); return r; }

        // 反向翻译：中文→英文，用于阵容管理中用户输入中文时自动转回英文原名
        private string ReverseTranslate(string chinese)
        {
            if (string.IsNullOrEmpty(chinese)) return chinese;
            // 如果不含中文，可能已经是英文原名，直接返回
            if (!HasChinese(chinese)) return chinese;
            string english;
            if (_reverseTranslations.TryGetValue(chinese, out english)) return english;
            // 尝试去掉括号后缀如 "燃烧 (黄金)"
            var parenIdx = chinese.IndexOf('(');
            if (parenIdx > 0)
            {
                var s2 = chinese.Substring(0, parenIdx).TrimEnd();
                if (_reverseTranslations.TryGetValue(s2, out english)) return english;
            }
            return chinese; // 找不到翻译则原样返回
        }

        private string Translate(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            if (!_useChinese) return english;
            string chinese;
            if (_translations.TryGetValue(english, out chinese)) return chinese;
            // 循环去掉 [Karnok Unique] [Crash Site Expedition] 等方括号前缀
            var stripped = english;
            var bracketWords = new List<string>();
            while (stripped.StartsWith("[") && stripped.IndexOf(']') > 0)
            {
                var end = stripped.IndexOf(']');
                var bracketContent = stripped.Substring(1, end - 1);
                bracketWords.AddRange(bracketContent.Split(' '));
                stripped = stripped.Substring(end + 1).TrimStart();
                if (_translations.TryGetValue(stripped, out chinese)) return chinese;
            }
            // 去掉方括号后仍找不到？尝试移除括号内的单词（如 Expedition）
            if (bracketWords.Count > 0)
            {
                foreach (var w in bracketWords)
                {
                    if (w.Length <= 3) continue;
                    var trial = stripped.Replace(" " + w, "").Replace(w + " ", "").Trim();
                    if (trial != stripped && _translations.TryGetValue(trial, out chinese)) return chinese;
                }
            }
            // 去掉括号后缀 (Gold) (Silver) 等
            var parenIdx = english.IndexOf('(');
            if (parenIdx > 0)
            {
                var s2 = english.Substring(0, parenIdx).TrimEnd();
                if (_translations.TryGetValue(s2, out chinese)) return chinese;
            }
            // 模糊匹配：Creature ↔ Monster
            if (english.Contains("Creature"))
            {
                var trial = english.Replace("Creature", "Monster");
                if (_translations.TryGetValue(trial, out chinese)) return chinese;
            }
            return english;
        }

        // ==================== JSON 导出 (v7.4.0 增强版) ====================

        /// <summary>通过 template_id 查卡牌数据库获取英文内部名</summary>
        private string ResolveCardNameByTemplateId(string templateId, BazaarGameClient.Domain.Models.Cards.Card cd)
        {
            // 优先从 CardData 直接读取名称
            if (cd != null)
            {
                try
                {
                    if (cd.Template != null && !string.IsNullOrEmpty(cd.Template.InternalName))
                        return cd.Template.InternalName;
                    if (!string.IsNullOrEmpty(cd.Name)) return cd.Name;
                }
                catch { }
            }
            // 回退：通过 template_id 在 _cardDb 中查找
            return LookupCardNameByTemplateId(templateId);
        }

        /// <summary>仅在 _cardDb 中通过 template_id (GUID) 查找英文内部名</summary>
        private string LookupCardNameByTemplateId(string templateId)
        {
            if (string.IsNullOrEmpty(templateId) || _cardDb == null || _cardDb.Count == 0) return null;
            foreach (var kv in _cardDb)
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Value.internal_name)) continue;
                if (kv.Key.Equals(templateId, StringComparison.OrdinalIgnoreCase)
                    || (kv.Value.internal_name ?? "").Equals(templateId, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.internal_name;
            }
            return null;
        }

        /// <summary>从游戏运行时模板缓存构建 template_id (GUID) → internal_name 映射</summary>
        private static void BuildTemplateNameMapFromCache(Dictionary<string, string> map)
        {
            try
            {
                // 方法1: TheBazaar.ClientCache.RunConfig → CardMap
                var asm = typeof(BoardManager).Assembly;
                var allTypes = asm.GetTypes();
                var cacheType = allTypes.FirstOrDefault(t => t.Name == "ClientCache" || t.FullName == "TheBazaar.ClientCache");
                if (cacheType != null)
                {
                    object runConfig = null;
                    TryGetStaticMemberValue(cacheType, "RunConfig", out runConfig);
                    if (runConfig == null) TryGetStaticMemberValue(cacheType, "runConfig", out runConfig);
                    if (runConfig != null)
                    {
                        // 尝试 .Value 解包
                        object nestedValue;
                        if (TryGetMemberValue(runConfig, "Value", out nestedValue) && nestedValue != null)
                            runConfig = nestedValue;

                        // 尝试 CardMap / GetCardMap()
                        object cardMap = null;
                        if (!TryGetMemberValue(runConfig, "CardMap", out cardMap) || cardMap == null)
                            cardMap = TryInvokeParameterlessMethod(runConfig, "GetCardMap");

                        if (cardMap != null)
                        {
                            EnumerateCardTemplates(cardMap, map);
                            if (map.Count > 0) return;
                        }
                    }
                }

                // 方法2: 全局搜索 BazaarPlusPlus.StaticCards.BppStaticDataAccess
                var bppType = allTypes.FirstOrDefault(t => t.FullName != null && t.FullName.Contains("BppStaticDataAccess"));
                if (bppType != null)
                {
                    object manager;
                    if (TryInvokeParameterlessStaticMethod(bppType, "TryGetReadyManagerObject", out manager) && manager != null)
                    {
                        object cardMap;
                        if (TryInvokeStaticMethod(bppType, "LoadCardMap", new[] { manager }, out cardMap) && cardMap != null)
                        {
                            EnumerateCardTemplates(cardMap, map);
                        }
                    }
                }
            }
            catch { }
        }

        private static void EnumerateCardTemplates(object cardMap, Dictionary<string, string> map)
        {
            if (cardMap == null || map == null) return;
            try
            {
                var dict = cardMap as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        if (entry.Value == null) continue;
                        string templateId = null, internalName = null;
                        TryGetMemberValue(entry.Value, "TemplateId", out var tid);
                        TryGetMemberValue(entry.Value, "InternalName", out var iname);
                        templateId = tid?.ToString();
                        internalName = iname?.ToString();
                        if (!string.IsNullOrEmpty(templateId) && !string.IsNullOrEmpty(internalName) && !map.ContainsKey(templateId))
                            map[templateId] = internalName;
                        // 也尝试 entry.Key 作为 template_id
                        if (entry.Key != null)
                        {
                            var keyStr = entry.Key.ToString();
                            if (!string.IsNullOrEmpty(keyStr) && !string.IsNullOrEmpty(internalName) && !map.ContainsKey(keyStr))
                                map[keyStr] = internalName;
                        }
                    }
                    return;
                }
                var enu = cardMap as System.Collections.IEnumerable;
                if (enu != null && !(cardMap is string))
                {
                    foreach (var item in enu)
                    {
                        if (item == null) continue;
                        string templateId = null, internalName = null;
                        TryGetMemberValue(item, "TemplateId", out var tid);
                        TryGetMemberValue(item, "InternalName", out var iname);
                        templateId = tid?.ToString();
                        internalName = iname?.ToString();
                        if (!string.IsNullOrEmpty(templateId) && !string.IsNullOrEmpty(internalName) && !map.ContainsKey(templateId))
                            map[templateId] = internalName;
                        // 也尝试递归 .Value
                        object nestedValue;
                        if (TryGetMemberValue(item, "Value", out nestedValue) && nestedValue != null)
                            EnumerateCardTemplates(nestedValue, map);
                    }
                }
            }
            catch { }
        }

        private static bool TryGetStaticMemberValue(Type type, string name, out object value)
        {
            value = null;
            try
            {
                var f = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { value = f.GetValue(null); return true; }
                var p = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) { value = p.GetValue(null, null); return true; }
            }
            catch { }
            return false;
        }

        private static bool TryGetMemberValue(object target, string name, out object value)
        {
            value = null;
            if (target == null) return false;
            try
            {
                var t = target.GetType();
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { value = f.GetValue(target); return true; }
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 0) { value = p.GetValue(target, null); return true; }
            }
            catch { }
            return false;
        }

        private static object TryInvokeParameterlessMethod(object target, string name)
        {
            if (target == null) return null;
            try
            {
                var m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && m.GetParameters().Length == 0) return m.Invoke(target, null);
            }
            catch { }
            return null;
        }

        private static bool TryInvokeParameterlessStaticMethod(Type type, string name, out object value)
        {
            value = null;
            try
            {
                var m = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && m.GetParameters().Length == 0) { value = m.Invoke(null, null); return true; }
            }
            catch { }
            return false;
        }

        private static bool TryInvokeStaticMethod(Type type, string name, object[] args, out object value)
        {
            value = null;
            try
            {
                foreach (var m in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name == name && m.GetParameters().Length == (args?.Length ?? 0))
                    {
                        value = m.Invoke(null, args);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string NormalizeTierStr(string tier)
        {
            if (string.IsNullOrEmpty(tier)) return "Bronze";
            var lower = tier.ToLowerInvariant();
            if (lower.Contains("bronze") || tier.Contains("\u94dc") || tier.Contains("\u9752\u94dc")) return "Bronze";
            if (lower.Contains("silver") || tier.Contains("\u94f6") || tier.Contains("\u767d\u94f6")) return "Silver";
            if (lower.Contains("gold") || tier.Contains("\u91d1") || tier.Contains("\u9ec4\u91d1")) return "Gold";
            if (lower.Contains("diamond") || tier.Contains("\u94bb") || tier.Contains("\u94bb\u77f3")) return "Diamond";
            if (lower.Contains("legendary")) return "Legendary";
            return tier;
        }

        private BoardData GatherBoardData()
        {
            int day = _currentDay;
            if (day < 1) { var ui = TryReadUiDay(); if (ui.HasValue) { day = ui.Value; _currentDay = day; } }
            int gold = _currentGold;
            if (gold <= 0) { var ui = TryReadUiGold(); if (ui.HasValue) { gold = ui.Value; _currentGold = gold; } }
            int health = _currentHealth;
            if (health <= 0) { var ui = TryReadUiHealth(); if (ui.HasValue) { health = ui.Value; _currentHealth = health; } }

            // 从 DTO 读取 Level / XP
            int level = 0, xp = 0;
            try
            {
                if (LatestGameStateDto != null)
                {
                    var player = GetField(LatestGameStateDto, "Player");
                    if (player != null)
                    {
                        var attrs = GetField(player, "Attributes") as System.Collections.IEnumerable;
                        if (attrs != null)
                        {
                            foreach (var item in attrs)
                            {
                                if (item == null) continue;
                                var key = GetProperty(item, "Key");
                                var val = GetProperty(item, "Value");
                                if (key == null || val == null) continue;
                                var ks = key.ToString();
                                int iv; try { iv = Convert.ToInt32(val); } catch { continue; }
                                if (ks.Equals("Level", StringComparison.OrdinalIgnoreCase)) level = iv;
                                else if (ks.Equals("XP", StringComparison.OrdinalIgnoreCase) || ks.Equals("Experience", StringComparison.OrdinalIgnoreCase)) xp = iv;
                            }
                        }
                    }
                }
            }
            catch { }

            var d = new BoardData
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Day = day, Hero = _detectedHero, Gold = gold, Income = _currentIncome,
                Health = health, Prestige = _currentPrestige, Level = level, XP = xp,
                InventorySlotsUsed = 0, InventorySlotsTotal = 10,
                BoardItems = new List<CardInfo>(), StorageItems = new List<CardInfo>(),
                OpponentItems = new List<CardInfo>(), SkillCards = new List<CardInfo>(),
                Shops = new List<ShopInfo>(), EventOptions = new List<string>(),
                EventOptionsDetailed = new List<EventOptionInfo>()
            };

            var shopCards = new List<CardInfo>();
            string bestShopName = "";
            var seenIds = new HashSet<string>();
            var templateIdToName = new Dictionary<string, string>(); // 运行时 template_id → internal_name 映射

            // 从游戏运行时模板缓存构建 template_id → internal_name 映射
            BuildTemplateNameMapFromCache(templateIdToName);

            // === 扫描所有 CardController（类似 BazaarStateExporter 的 UiCardCapture） ===
            try
            {
#pragma warning disable 0618
                var allCC = UnityEngine.Object.FindObjectsOfType<CardController>();
#pragma warning restore 0618
                if (allCC != null)
                {
                    foreach (var cc in allCC)
                    {
                        try
                        {
                            var cd = cc.CardData;
                            if (cd == null) continue;
                            var typeStr = cd.Type.ToString();
                            var iid = cd.InstanceId != null ? cd.InstanceId.ToString() : "";
                            var templateId = cd.TemplateId != null ? cd.TemplateId.ToString() : "";

                            // 解析名称：CardData.Template.InternalName → Translate → fallback _cardDb
                            string internalName = ResolveCardNameByTemplateId(templateId, cd);
                            string displayName = string.IsNullOrEmpty(internalName) ? "???" : Translate(internalName);
                            // 建立运行时 template_id → internal_name 映射（供 DTO 回退使用）
                            if (!string.IsNullOrEmpty(templateId) && !string.IsNullOrEmpty(internalName) && internalName != "???")
                                templateIdToName[templateId] = internalName;

                            if (typeStr == "Skill")
                            {
                                if (!string.IsNullOrEmpty(iid) && seenIds.Contains(iid)) continue;
                                if (!string.IsNullOrEmpty(iid)) seenIds.Add(iid);
                                var skillCi = BuildCardInfo(cd, internalName ?? displayName, templateId, iid, "Skill");
                                d.SkillCards.Add(skillCi);
                                continue;
                            }
                            if (typeStr != "Item") continue;
                            if (string.IsNullOrEmpty(internalName) || internalName == "???") continue;
                            if (!string.IsNullOrEmpty(iid) && seenIds.Contains(iid)) continue;
                            if (!string.IsNullOrEmpty(iid)) seenIds.Add(iid);

                            bool isPlayer = false;
                            try { isPlayer = IsPlayerBoardFor(cc); }
                            catch { try { isPlayer = IsPlayerItem(cc.transform); } catch { } }

                            var section = GetCardSection(cd);
                            var uiCtx = GetUiContext(cc);
                            int? price = GetCardPrice(cc);

                            var ci = BuildCardInfo(cd, internalName, templateId, iid, section);
                            if (price.HasValue) ci.Attributes["Price"] = price.Value;

                            bool isShop = section.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0
                                || section.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0
                                || uiCtx.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0
                                || uiCtx.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isOpp = !isPlayer && uiCtx.IndexOf("OpponentItemSocket_", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isReward = section.IndexOf("Reward", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (isShop && !isPlayer && !isReward)
                            {
                                shopCards.Add(ci);
                                if (string.IsNullOrEmpty(bestShopName))
                                {
                                    foreach (var part in uiCtx.Split('/'))
                                    {
                                        if ((part.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0
                                            || part.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0
                                            || part.IndexOf("Encounter", StringComparison.OrdinalIgnoreCase) >= 0)
                                            && !part.Contains("Socket") && !part.Contains("Portrait"))
                                        { bestShopName = part; break; }
                                    }
                                }
                            }
                            else if (isOpp)
                            {
                                d.OpponentItems.Add(ci);
                            }
                            else if (isPlayer)
                            {
                                if (section.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0)
                                    d.BoardItems.Add(ci);
                                else if (section.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) >= 0)
                                    d.StorageItems.Add(ci);
                                else
                                {
                                    try
                                    {
                                        if (_isPlayerBoardProp != null && ((bool)_isPlayerBoardProp.GetValue(cc, null)))
                                            d.BoardItems.Add(ci);
                                        else d.StorageItems.Add(ci);
                                    }
                                    catch { d.BoardItems.Add(ci); }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 回退: 旧 BoardManager 方式（补充 Stash / Board）
            if (d.StorageItems.Count == 0 && _playerCardsOnBoardField != null)
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
                                var section = GetCardSection(cd);
                                var loc = "Board";
                                if (section.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) >= 0) loc = "Storage";
                                else if (_isPlayerBoardProp != null) { try { loc = ((bool)_isPlayerBoardProp.GetValue(ctrl, null)) ? "Board" : "Storage"; } catch { } }
                                var templateId = cd.TemplateId != null ? cd.TemplateId.ToString() : "";
                                string internalName = ResolveCardNameByTemplateId(templateId, cd);
                                var ci = BuildCardInfo(cd, internalName ?? "???", templateId, cd.InstanceId != null ? cd.InstanceId.ToString() : "", loc);
                                if (loc == "Board") d.BoardItems.Add(ci); else d.StorageItems.Add(ci);
                            }
                        }
                    }
                }
                catch { }
            }

            // DTO 回退: 从 LatestGameStateDto.Cards 补充 Stash/Skills（未被 CardController 扫描到的）
            if (d.StorageItems.Count == 0 || d.BoardItems.Count == 0 || d.SkillCards.Count == 0)
            {
                try
                {
                    if (LatestGameStateDto != null)
                    {
                        var cardsEnum = GetField(LatestGameStateDto, "Cards") as System.Collections.IEnumerable;
                        if (cardsEnum != null)
                        {
                            foreach (var cardObj in cardsEnum)
                            {
                                if (cardObj == null) continue;
                                var iid = GetField(cardObj, "InstanceId");
                                var tid = GetField(cardObj, "TemplateId");
                                var sec = GetField(cardObj, "Section");
                                var typ = GetField(cardObj, "Type");
                                var tier = GetField(cardObj, "Tier");
                                var instanceIdStr = iid != null ? iid.ToString() : "";
                                var templateIdStr = tid != null ? tid.ToString() : "";
                                var sectionStr = sec != null ? sec.ToString() : "";
                                var typeStr = typ != null ? typ.ToString() : "";
                                var tierStr = tier != null ? tier.ToString() : "";
                                if (string.IsNullOrEmpty(instanceIdStr)) continue;
                                if (seenIds.Contains(instanceIdStr)) continue;
                                seenIds.Add(instanceIdStr);

                                // 通过 template_id 查找名称：优先运行时映射表 → _cardDb 回退
                                string name = null;
                                if (!string.IsNullOrEmpty(templateIdStr))
                                {
                                    templateIdToName.TryGetValue(templateIdStr, out name);
                                    if (string.IsNullOrEmpty(name))
                                        name = LookupCardNameByTemplateId(templateIdStr);
                                }
                                if (string.IsNullOrEmpty(name)) name = "???";

                                var ci = new CardInfo
                                {
                                    Name = string.IsNullOrEmpty(name) ? "???" : Translate(name),
                                    TemplateId = templateIdStr,
                                    InstanceId = instanceIdStr,
                                    CardType = typeStr,
                                    Tier = NormalizeTierStr(tierStr),
                                    Location = sectionStr,
                                    Attributes = new Dictionary<string, int>()
                                };
                                // 读取附魔
                                try
                                {
                                    var ench = GetProperty(cardObj, "Enchantment");
                                    if (ench != null && !string.IsNullOrEmpty(ench.ToString()))
                                        ci.Enchantment = ench.ToString();
                                }
                                catch { }

                                if (typeStr.Equals("Skill", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (d.SkillCards.Count == 0) d.SkillCards.Add(ci);
                                }
                                else if (typeStr.Equals("Item", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (sectionStr.Equals("Hand", StringComparison.OrdinalIgnoreCase) && d.BoardItems.Count == 0)
                                        d.BoardItems.Add(ci);
                                    else if (sectionStr.Equals("Stash", StringComparison.OrdinalIgnoreCase))
                                        d.StorageItems.Add(ci);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 技能 (SkillPresentationManager)
            if (_skillListField != null && d.SkillCards.Count == 0)
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
                                        var bm = r.transform.GetComponentInParent<BoardManager>();
                                        if (bm == null || bm != BoardManager.Instance) continue;
                                        var templateId = r.Card.TemplateId != null ? r.Card.TemplateId.ToString() : "";
                                        string internalName = ResolveCardNameByTemplateId(templateId, r.Card);
                                        var ci = BuildCardInfo(r.Card, internalName ?? "???", templateId, r.Card.InstanceId != null ? r.Card.InstanceId.ToString() : "", "Skill");
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

            // 商店 EncounterController
            try
            {
#pragma warning disable 0618
                var encs = UnityEngine.Object.FindObjectsOfType<EncounterController>();
#pragma warning restore 0618
                if (encs != null)
                {
                    foreach (var ec in encs)
                    {
                        if (ec == null) continue;
                        var t = ec.transform;
                        if (t.name.Contains("_Frame_") || t.name.Contains("_PV")) continue;
                        var shopName = t.name;
                        if (shopName.EndsWith("(Clone)")) shopName = shopName.Substring(0, shopName.Length - 7);
                        d.Shops.Add(new ShopInfo { GameObjectName = shopName, DisplayName = Translate(shopName), Position = string.Format("{0:F1},{1:F1},{2:F1}", t.position.x, t.position.y, t.position.z) });
                    }
                }
            }
            catch { }

            // 当前商店详情
            if (shopCards.Count > 0)
            {
                if (bestShopName.EndsWith("(Clone)")) bestShopName = bestShopName.Substring(0, bestShopName.Length - 7);
                d.CurrentShop = new ShopDetail { ShopName = bestShopName, DisplayName = string.IsNullOrEmpty(bestShopName) ? "" : Translate(bestShopName), Items = shopCards };
            }

            // 事件选项（从 DTO 读取 SelectionSet）
            try
            {
                if (LatestGameStateDto != null)
                {
                    var currentState = GetField(LatestGameStateDto, "CurrentState");
                    if (currentState != null)
                    {
                        var selSet = GetField(currentState, "SelectionSet") as System.Collections.IEnumerable;
                        if (selSet != null)
                        {
                            foreach (var id in selSet)
                            {
                                if (id != null)
                                {
                                    var idStr = id.ToString();
                                    if (!string.IsNullOrEmpty(idStr))
                                    {
                                        d.EventOptions.Add(idStr);
                                        string kind = "unknown";
                                        if (idStr.StartsWith("enc_")) kind = "encounter";
                                        else if (idStr.StartsWith("ste_")) kind = "step";
                                        else if (idStr.StartsWith("com_")) kind = "combat";
                                        else if (idStr.StartsWith("pvp_")) kind = "pvp";
                                        d.EventOptionsDetailed.Add(new EventOptionInfo { Id = idStr, Kind = kind });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 统计物品槽位
            d.InventorySlotsUsed = d.BoardItems.Count + d.StorageItems.Count;
            if (d.BoardItems.Count == 0 && d.StorageItems.Count == 0 && d.SkillCards.Count == 0)
                d.StatusMessage = "No items found. Enter a run first.";
            else
                d.StatusMessage = string.Format("Board={0} Stash={1} Skills={2} Shop={3}",
                    d.BoardItems.Count, d.StorageItems.Count, d.SkillCards.Count,
                    d.CurrentShop != null ? d.CurrentShop.Items.Count : 0);

            return d;
        }

        /// <summary>构建 CardInfo（含名称映射）</summary>
        private CardInfo BuildCardInfo(BazaarGameClient.Domain.Models.Cards.Card cd, string internalName, string templateId, string instanceId, string location)
        {
            var ci = new CardInfo
            {
                Name = string.IsNullOrEmpty(internalName) ? "???" : Translate(internalName),
                TemplateId = templateId ?? "",
                InstanceId = instanceId ?? "",
                CardType = cd.Type.ToString(),
                CardSize = cd.Size.ToString(),
                Tier = cd.Tier.ToString(),
                Location = location,
                Attributes = new Dictionary<string, int>()
            };
            // 附魔（通过反射获取 Enchantment 属性）
            try
            {
                var enchProp = cd.GetType().GetProperty("Enchantment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (enchProp != null)
                {
                    var ench = enchProp.GetValue(cd, null);
                    if (ench != null && !string.IsNullOrEmpty(ench.ToString()))
                        ci.Enchantment = ench.ToString();
                }
            }
            catch { }
            // 属性
            try
            {
                if (cd.Attributes != null)
                {
                    foreach (var kv in cd.Attributes)
                    {
                        if (kv.Value != 0) ci.Attributes[kv.Key.ToString()] = kv.Value;
                    }
                }
            }
            catch { }
            return ci;
        }

        private static string GetCardSection(BazaarGameClient.Domain.Models.Cards.Card cd)
        {
            try { var sp = cd.GetType().GetProperty("Section", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (sp != null) { var v = sp.GetValue(cd, null); return v != null ? v.ToString() : ""; } } catch { }
            return "";
        }

        private static string GetUiContext(CardController cc)
        {
            try
            {
                var names = new List<string>();
                Transform cur = cc.transform;
                for (int d = 0; cur != null && d < 12; d++) { names.Add(cur.name ?? ""); cur = cur.parent; }
                return string.Join("/", names.ToArray());
            }
            catch { return ""; }
        }

        private static int? GetCardPrice(CardController cc)
        {
            try
            {
                var pp = cc.GetType().GetProperty("ActivePriceContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pp == null) return null;
                var pc = pp.GetValue(cc, null);
                if (pc == null) return null;
                var cpf = pc.GetType().GetField("currentPrice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (cpf == null) return null;
                var txc = cpf.GetValue(pc);
                if (txc == null) return null;
                var tp = txc.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tp == null) return null;
                var raw = tp.GetValue(txc, null) as string;
                if (string.IsNullOrEmpty(raw)) return null;
                var digits = new string(raw.Where(char.IsDigit).ToArray());
                int val;
                return int.TryParse(digits, out val) ? (int?)val : null;
            }
            catch { return null; }
        }

        // === UI 数值扫描（借鉴 bazaar-helper） ===

        private static int? TryReadUiInt(string namePattern)
        {
            try
            {
                int bestScore = int.MinValue;
                int? bestValue = null;
#pragma warning disable 0618
                var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
#pragma warning restore 0618
                foreach (var go in allObjects)
                {
                    if (go == null) continue;
                    if ((go.name ?? "").IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        string t = TryGetComponentText(comp);
                        if (string.IsNullOrEmpty(t)) continue;
                        int parsed;
                        if (TryParseFirstInt(t, out parsed))
                        {
                            int score = ScoreGameObject(go);
                            if (score > bestScore) { bestScore = score; bestValue = parsed; }
                            break;
                        }
                    }
                }
                if (bestScore < 1000) return null;
                return bestValue;
            }
            catch { return null; }
        }

        private int? TryReadUiGold()
        {
            var v = TryReadUiInt("Gold_Number"); if (v.HasValue) return v;
            v = TryReadUiInt("GoldNumber"); if (v.HasValue) return v;
            return TryReadUiResourceByHierarchy("gold", new[] { "tooltip", "monster", "reward", "enemy", "opponent" });
        }

        private int? TryReadUiHealth()
        {
            int? v;
            foreach (var p in new[] { "HP_Number", "Health_Value", "HealthNumber", "HPNumber" })
            { v = TryReadUiInt(p); if (v.HasValue) return v; }
            return TryReadUiResourceByHierarchy("health", new[] { "tooltip", "monster", "reward", "enemy", "opponent", "regen", "maxhealth", "max_health" });
        }

        private int? TryReadUiDay()
        {
            try
            {
                int bestScore = int.MinValue;
                int? bestValue = null;
#pragma warning disable 0618
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
#pragma warning restore 0618
                foreach (var mb in all)
                {
                    if (mb == null || mb.gameObject == null || !mb.gameObject.activeInHierarchy) continue;
                    if ((mb.GetType().FullName ?? mb.GetType().Name).IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string objName = (mb.gameObject.name ?? "").ToLowerInvariant();
                    string hierarchy = GetHierarchyPath(mb.transform).ToLowerInvariant();
                    if (!objName.Contains("day") && !hierarchy.Contains("day")) continue;
                    if (hierarchy.Contains("tooltip") || hierarchy.Contains("reward") || hierarchy.Contains("card") || hierarchy.Contains("history")) continue;
                    string t = TryGetComponentText(mb);
                    if (string.IsNullOrEmpty(t)) continue;
                    int parsed;
                    if (!TryParseFirstInt(t, out parsed) || parsed < 1 || parsed > 20) continue;
                    int score = ScoreGameObject(mb.gameObject);
                    if (objName.Contains("daynumber") || objName.Contains("day_number") || objName.Contains("dayvalue") || objName.Contains("day_value") || objName.Contains("currentday")) score += 500;
                    else if (objName.Contains("day")) score += 300;
                    if (score > bestScore) { bestScore = score; bestValue = parsed; }
                }
                if (bestScore < 1400) return null;
                return bestValue;
            }
            catch { return null; }
        }

        private int? TryReadUiResourceByHierarchy(string keyword, string[] excludeKeywords)
        {
            try
            {
                int bestScore = int.MinValue;
                int? bestValue = null;
#pragma warning disable 0618
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
#pragma warning restore 0618
                foreach (var mb in all)
                {
                    if (mb == null || mb.gameObject == null || !mb.gameObject.activeInHierarchy) continue;
                    if ((mb.GetType().FullName ?? mb.GetType().Name).IndexOf("Text", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string hierarchy = GetHierarchyPath(mb.transform).ToLowerInvariant();
                    if (hierarchy.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (excludeKeywords != null)
                    {
                        bool excluded = false;
                        foreach (var ex in excludeKeywords) { if (hierarchy.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) { excluded = true; break; } }
                        if (excluded) continue;
                    }
                    string t = TryGetComponentText(mb);
                    if (string.IsNullOrEmpty(t)) continue;
                    int parsed;
                    if (!TryParseFirstInt(t, out parsed)) continue;
                    int score = ScoreGameObject(mb.gameObject);
                    if (score > bestScore) { bestScore = score; bestValue = parsed; }
                }
                if (bestScore < 1000) return null;
                return bestValue;
            }
            catch { return null; }
        }

        private static string GetHierarchyPath(Transform t)
        {
            var names = new List<string>();
            Transform cur = t;
            while (cur != null && names.Count < 16) { names.Add(cur.name); cur = cur.parent; }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string TryGetComponentText(Component comp)
        {
            if (comp == null) return null;
            var type = comp.GetType();
            foreach (var name in new[] { "text", "Text", "m_text" })
            {
                try { var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (prop != null && prop.GetIndexParameters().Length == 0) { var v = prop.GetValue(comp, null); if (v != null) return v.ToString(); } } catch { }
                try { var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (field != null) { var v = field.GetValue(comp); if (v != null) return v.ToString(); } } catch { }
            }
            return null;
        }

        private static bool TryParseFirstInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"[-+]?\d[\d,]*");
            if (!m.Success) return false;
            return int.TryParse(m.Value.Replace(",", ""), out value);
        }

        private static int ScoreGameObject(GameObject go)
        {
            int s = 0;
            if (go.activeInHierarchy) s += 1000;
            if (go.activeSelf) s += 100;
            if (go.scene.IsValid()) s += 50;
            if (go.scene.isLoaded) s += 50;
            return s;
        }

        private void ExportToJson(BoardData d)
        {
            var dir = Path.Combine(Paths.GameRootPath, "BazaarBoardReader", "data");
            Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(d, Formatting.Indented);
            // 带时间戳的历史文件
            var p = Path.Combine(dir, string.Format("board_{0}.json", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            WriteJsonAtomic(p, json);
            // 最新快照（供外部脚本读取）
            var latest = Path.Combine(dir, "board_latest.json");
            WriteJsonAtomic(latest, json);
            _logger.LogInfo(string.Format("[BoardReader] Export: Board={0} Stash={1} Opp={2} Skills={3} Shops={4} ShopItems={5}",
                d.BoardItems.Count, d.StorageItems.Count, d.OpponentItems.Count, d.SkillCards.Count,
                d.Shops.Count, d.CurrentShop != null ? d.CurrentShop.Items.Count : 0));
        }
    }

    // ==================== 数据类 ====================

    internal class OverlayLabel { public string Text; public string Tier; public Vector3 ScreenPos; public string SubText; public string HoverData; public bool IsItem; public bool Blink; }

    public class TrackedCard { public string Name; public string Type; public bool IsPlayer; public float LastSeen; public string Tier; }

    [Serializable]
    public class MerchantEntry { public string name; public string category; public List<string> heroes; public string tier; public List<string> tags; public List<string> exclude_tags; public string size; public bool cross_hero; }
    public class CardDataEntry { public string internal_name; public string display_name; public string type; public List<string> heroes; public List<string> tags; public List<string> hidden_tags; public string size; public string description; public string starting_tier; public List<string> tiers; }
    public class CardsData { public Dictionary<string, CardDataEntry> cards; }
    public class CardDescEntry { public string internal_name; public string description; public string template_id; }
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
        public List<string> SkillCoreOwned = new List<string>();
        public List<string> SkillCoreMissing = new List<string>();
        public List<string> SkillFlexOwned = new List<string>();
        public List<string> SkillFlexMissing = new List<string>();
        public List<string> ShopCoreMatches = new List<string>();
        public List<string> ShopFlexMatches = new List<string>();
    }

    [Serializable]
    public class BoardData
    {
        public string Timestamp;
        public int Day;
        public string Hero;
        public int Gold;
        public int Income;
        public int Health;
        public int Prestige;
        public int Level;
        public int XP;
        public int InventorySlotsUsed;
        public int InventorySlotsTotal;
        public List<CardInfo> BoardItems;
        public List<CardInfo> StorageItems;
        public List<CardInfo> OpponentItems;
        public List<CardInfo> SkillCards;
        public List<ShopInfo> Shops;
        public ShopDetail CurrentShop;
        public List<string> EventOptions;
        public List<EventOptionInfo> EventOptionsDetailed;
        public string StatusMessage;
    }

    [Serializable]
    public class ShopInfo
    {
        public string GameObjectName;
        public string DisplayName;
        public string Position;
        public List<CardInfo> Items;
    }

    [Serializable]
    public class ShopDetail
    {
        public string ShopName;
        public string DisplayName;
        public List<CardInfo> Items;
        public int? RefreshCost;
        public int? RefreshesRemaining;
        public bool? RefreshAvailable;
    }

    [Serializable]
    public class EventOptionInfo
    {
        public string Id;
        public string TemplateId;
        public string Name;
        public string Kind;
        public string CardType;
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
        public string TemplateId;
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
