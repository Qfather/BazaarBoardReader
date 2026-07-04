using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        private const string PluginVersion = "5.5.0";

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

        // GUI
        private bool _overlayEnabled;
        private bool _showSliders;
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

        // Config
        private const string CfgSec = "Offsets";
        private const string CfgItem = "ItemOffsetY";
        private const string CfgSkill = "SkillOffsetY";
        private const string CfgShop = "ShopOffsetY";
        private const string CfgBg = "BgOpacity";

        private void Awake()
        {
            _logger = Logger;
            _itemOffsetY = Config.Bind(CfgSec, CfgItem, 30f).Value;
            _skillOffsetY = Config.Bind(CfgSec, CfgSkill, 20f).Value;
            _shopOffsetY = Config.Bind(CfgSec, CfgShop, 80f).Value;
            _bgOpacity = Config.Bind(CfgSec, CfgBg, 0.55f).Value;
            _logger.LogInfo(string.Format("[BoardReader] v5.5 物品={0} 技能={1} 商店={2} 背景={3:F0}%",
                (int)_itemOffsetY, (int)_skillOffsetY, (int)_shopOffsetY, _bgOpacity * 100f));

            _labelStyle = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
            _shopStyle = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };

            _playerCardsOnBoardField = typeof(BoardManager).GetField("_playerCardsOnBoard", BindingFlags.Instance | BindingFlags.NonPublic);
            _isPlayerBoardProp = typeof(CardController).GetProperty("IsPlayerBoard", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _skillListField = typeof(TheBazaar.SkillPresentationManager).GetField("_skillList", BindingFlags.Instance | BindingFlags.NonPublic);
            _logger.LogInfo(string.Format("[BoardReader] items={0} skills={1}",
                _playerCardsOnBoardField != null ? "OK" : "NO", _skillListField != null ? "OK" : "NO"));
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
            }
            catch { }
            return false;
        }

        // ==================== Update ====================

        private void Update()
        {
            if (IsKeyPressed("f7")) { _showSliders = !_showSliders; }
            if (IsKeyPressed("f6")) { _overlayEnabled = !_overlayEnabled; }

            if (IsKeyPressed("f5"))
            {
                if (Time.time - _lastExportTime < CooldownSeconds) return;
                _lastExportTime = Time.time;
                try { ExportToJson(GatherBoardData()); }
                catch (Exception ex) { _logger.LogError(string.Format("[BoardReader] 导出失败: {0}", ex)); }
            }

            if (_overlayEnabled && Time.frameCount % 60 == 0)
                RefreshOverlayLabels();
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

            if (_showSliders)
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
        }

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

        // ==================== 标签收集 ====================

        private void RefreshOverlayLabels()
        {
            _itemLabels.Clear();
            _skillLabels.Clear();
            _shopLabels.Clear();
            var cam = Camera.main ?? Camera.current;
            if (cam == null) return;

            // 物品 + 技能选择中的技能卡
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

            // 战斗中的技能
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

            // 商店（通过 EncounterController）
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
                    // 清理 Clone 后缀
                    if (shopName.EndsWith("(Clone)"))
                        shopName = shopName.Substring(0, shopName.Length - 7);
                    _shopLabels.Add(new OverlayLabel { Text = shopName, Tier = "Invalid", ScreenPos = sp });
                }
            }
            catch { }
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

            // 商店
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

    internal class OverlayLabel { public string Text; public string Tier; public Vector3 ScreenPos; }

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
