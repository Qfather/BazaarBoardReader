#!/usr/bin/env python3
"""从 7.3.2 基线重建 BazaarBoardReaderPlugin.cs 的所有修改。"""
import sys

BAK = r"G:\SteamLibrary\steamapps\common\The Bazaar\BazaarBoardReader\BazaarBoardReaderPlugin.cs"
OUT = BAK  # 直接覆写

with open(BAK, "r", encoding="utf-8-sig") as f:
    text = f.read()

changes = 0
errors = []

def replace(old, new, label):
    global changes, text
    if old in text:
        text = text.replace(old, new)
        changes += 1
        print(f"OK: {label}")
    else:
        errors.append(label)
        print(f"FAIL: {label} - 未找到匹配")

# === 1. 添加 _lastAutoExportTime 字段 ===
replace(
    "        private float _lastExportTime;\n        private const float CooldownSeconds = 2f;",
    "        private float _lastExportTime;\n        private float _lastAutoExportTime;\n        private const float CooldownSeconds = 2f;\n        private const float AutoExportCooldownSeconds = 1.5f;",
    "1. _lastAutoExportTime 字段"
)

# === 2. CardController Patch: 添加自动导出触发 ===
replace(
    """                    lock (BazaarBoardReaderPlugin.TrackedCards)
                    {
                        var tier = cd.Tier.ToString();
                        BazaarBoardReaderPlugin.TrackedCards[id] = new TrackedCard
                        { Name = name, Type = typeStr, IsPlayer = isPlayer, LastSeen = Time.time, Tier = tier };
                    }
                }
                catch { }
            }
        }
    }""",
    """                    lock (BazaarBoardReaderPlugin.TrackedCards)
                    {
                        var tier = cd.Tier.ToString();
                        BazaarBoardReaderPlugin.TrackedCards[id] = new TrackedCard
                        { Name = name, Type = typeStr, IsPlayer = isPlayer, LastSeen = Time.time, Tier = tier };
                    }
                    // 事件驱动自动导出（借鉴 bazaar-helper-2.0.0 的事件驱动模式）
                    BazaarBoardReaderPlugin.TriggerAutoExport();
                }
                catch { }
            }
        }
    }""",
    "2. CardController Patch 自动导出触发"
)

# === 3. 替换 JSON 导出部分 ===
json_start = text.find("        // ==================== JSON 导出 ====================")
data_class_start = text.find("    // ==================== 数据类 ====================")

if json_start >= 0 and data_class_start > json_start:
    new_json = r"""        // ==================== JSON 导出 ====================

        // 事件驱动自动导出（借鉴 bazaar-helper-2.0.0 的事件驱动模式）
        internal static void TriggerAutoExport()
        {
            try
            {
                var instances = FindObjectsOfType<BazaarBoardReaderPlugin>();
                if (instances == null || instances.Length == 0) return;
                var plugin = instances[0];
                if (plugin == null) return;
                if (Time.time - plugin._lastAutoExportTime < AutoExportCooldownSeconds) return;
                plugin._lastAutoExportTime = Time.time;
                if (!plugin._overlayEnabled) return;
                var d = plugin.GatherBoardData();
                plugin.WriteAutoExport(d);
            }
            catch (Exception ex)
            {
                try { _logger.LogDebug("[BoardReader] 自动导出失败: " + ex.Message); } catch { }
            }
        }

        private void WriteAutoExport(BoardData d)
        {
            var dir = Path.Combine(Paths.GameRootPath, "BoardData");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "board_latest.json");
            WriteJsonAtomic(p, JsonConvert.SerializeObject(d, Formatting.Indented));
        }

        // === UI 资源扫描（借鉴 bazaar-helper-2.0.0） ===

        private static int? TryReadUiInt(string namePattern, string hierarchyPattern = null)
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
                    if (!string.IsNullOrEmpty(hierarchyPattern))
                    {
                        if (GetGameObjectHierarchy(go.transform).IndexOf(hierarchyPattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
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
            v = TryReadUiResourceByHierarchy("gold", new[] { "tooltip", "monster", "reward", "enemy", "opponent" }); if (v.HasValue) return v;
            v = TryReadUiResourceByHierarchy("currency", new[] { "tooltip", "monster", "reward", "enemy", "opponent" }); if (v.HasValue) return v;
            v = TryReadUiResourceByHierarchy("wallet", new[] { "tooltip", "monster", "reward", "enemy", "opponent" }); if (v.HasValue) return v;
            return TryReadUiResourceByHierarchy("coins", new[] { "tooltip", "monster", "reward", "enemy", "opponent" });
        }

        private int? TryReadUiHealth()
        {
            int? v;
            foreach (var p in new[] { "HP_Number", "Health_Value", "HealthNumber", "HPNumber", "hpnumber", "hp_number", "healthnumber", "health_number", "currenthealth", "current_health" })
            { v = TryReadUiInt(p); if (v.HasValue) return v; }
            v = TryReadUiResourceByHierarchy("health", new[] { "tooltip", "monster", "reward", "enemy", "opponent", "regen", "maxhealth", "max_health", "healthregen" }); if (v.HasValue) return v;
            return TryReadUiResourceByHierarchy("hp", new[] { "tooltip", "monster", "reward", "enemy", "opponent", "hps", "hpr", "php", "hpa" });
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
                    string hierarchy = GetGameObjectHierarchy(mb.transform).ToLowerInvariant();
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
                    string hierarchy = GetGameObjectHierarchy(mb.transform).ToLowerInvariant();
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

        private static string GetGameObjectHierarchy(Transform t)
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

        // === CardController 辅助读取 ===

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

        // === RunManager 兜底读取 ===

        private void TryReadFromRunManager()
        {
            try
            {
                var asm = typeof(BoardManager).Assembly;
                var allTypes = asm.GetTypes();
                var rmType = allTypes.FirstOrDefault(t => t.Name == "RunManager");
                if (rmType == null) { _logger.LogInfo("[BoardReader] RunManager: 类型未找到"); return; }
                var ip = rmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (ip == null) { _logger.LogInfo("[BoardReader] RunManager: Instance属性未找到"); return; }
                var inst = ip.GetValue(null, null);
                if (inst == null) { _logger.LogInfo("[BoardReader] RunManager: Instance为null"); return; }

                object player = null;
                var pp = rmType.GetProperty("Player", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pp != null) player = pp.GetValue(inst, null);
                if (player == null) { var pf = rmType.GetField("Player", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (pf != null) player = pf.GetValue(inst); }
                if (player == null && LatestGameStateDto != null) player = GetField(LatestGameStateDto, "Player");
                if (player == null)
                {
                    var nmpType = allTypes.FirstOrDefault(t => t.Name == "NetMessageProcessor");
                    if (nmpType != null)
                    {
#pragma warning disable 0618
                        var nmpInst = FindObjectOfType(nmpType);
#pragma warning restore 0618
                        if (nmpInst != null)
                        {
                            var lmf = nmpType.GetField("_lastMessage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (lmf != null) { var lm = lmf.GetValue(nmpInst); if (lm != null) { var dp = lm.GetType().GetProperty("Data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (dp != null) { var d = dp.GetValue(lm, null); if (d != null) player = GetField(d, "Player"); } } }
                        }
                    }
                }
                if (player == null) { _logger.LogInfo("[BoardReader] RunManager: Player未找到"); return; }

                object attrs = null;
                var af = player.GetType().GetField("Attributes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (af != null) attrs = af.GetValue(player);
                if (attrs == null) { var ap = player.GetType().GetProperty("Attributes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (ap != null) attrs = ap.GetValue(player, null); }
                if (attrs == null) { var uf = player.GetType().GetField("_attributes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (uf != null) attrs = uf.GetValue(player); }
                if (attrs == null) { _logger.LogInfo("[BoardReader] RunManager: Attributes未找到"); return; }

                var enu = attrs as System.Collections.IEnumerable;
                if (enu == null) { _logger.LogInfo("[BoardReader] RunManager: Attributes不是IEnumerable"); return; }

                var keyList = new List<string>();
                foreach (var item in enu)
                {
                    if (item == null) continue;
                    var kp = item.GetType().GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var vp = item.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (kp == null) kp = item.GetType().GetProperty("key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (vp == null) vp = item.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (kp == null || vp == null) continue;
                    var key = kp.GetValue(item, null);
                    var val = vp.GetValue(item, null);
                    if (key == null || val == null) continue;
                    var ks = key.ToString();
                    int iv;
                    try { iv = Convert.ToInt32(val); } catch { continue; }
                    keyList.Add(ks + "=" + iv);

                    if (_currentGold <= 0 && ks.IndexOf("Gold", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("MaxGold", StringComparison.OrdinalIgnoreCase) < 0) _currentGold = iv;
                    if (_currentHealth <= 0 && ks.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("MaxHealth", StringComparison.OrdinalIgnoreCase) < 0 && ks.IndexOf("Regen", StringComparison.OrdinalIgnoreCase) < 0) _currentHealth = iv;
                    if (_currentIncome <= 0 && (ks.Equals("Income", StringComparison.OrdinalIgnoreCase) || ks.IndexOf("DailyIncome", StringComparison.OrdinalIgnoreCase) >= 0 || ks.IndexOf("GoldIncome", StringComparison.OrdinalIgnoreCase) >= 0 || (ks.IndexOf("Income", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0))) _currentIncome = iv;
                    if (_currentPrestige <= 0 && (ks.Equals("Prestige", StringComparison.OrdinalIgnoreCase) || ks.IndexOf("CurrentPrestige", StringComparison.OrdinalIgnoreCase) >= 0 || (ks.IndexOf("Prestige", StringComparison.OrdinalIgnoreCase) >= 0 && ks.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0))) _currentPrestige = iv;
                }

                if (_currentDay < 1)
                {
                    foreach (var pn in new[] { "Day", "CurrentDay", "DayNumber", "RunDay" })
                    {
                        try { var dp = rmType.GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (dp != null) { var dv = dp.GetValue(inst, null); if (dv != null) { _currentDay = Convert.ToInt32(dv); break; } } } catch { }
                    }
                }
                if (string.IsNullOrEmpty(_detectedHero))
                {
                    var hp = player.GetType().GetProperty("Hero", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (hp != null) { var hv = hp.GetValue(player, null); if (hv != null && !string.IsNullOrEmpty(hv.ToString())) _detectedHero = hv.ToString(); }
                }

                _logger.LogInfo(string.Format("[BoardReader] RunManager: Hero={0} Day={1} Gold={2} Health={3} Income={4} Prestige={5} Keys=[{6}]",
                    _detectedHero, _currentDay, _currentGold, _currentHealth, _currentIncome, _currentPrestige,
                    string.Join(", ", keyList.ToArray())));
            }
            catch (Exception ex) { _logger.LogInfo("[BoardReader] RunManager异常: " + ex.Message); }
        }

        // === 增强版 GatherBoardData ===

        private BoardData GatherBoardData()
        {
            TryReadFromRunManager();
            int day = _currentDay;
            if (day < 1) { var ui = TryReadUiDay(); if (ui.HasValue) { day = ui.Value; _currentDay = day; } }
            int gold = _currentGold;
            if (gold <= 0) { var ui = TryReadUiGold(); if (ui.HasValue) { gold = ui.Value; _currentGold = gold; } }
            int health = _currentHealth;
            if (health <= 0) { var ui = TryReadUiHealth(); if (ui.HasValue) { health = ui.Value; _currentHealth = health; } }

            var d = new BoardData
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Day = day, Hero = _detectedHero, Gold = gold, Income = _currentIncome,
                Health = health, Prestige = _currentPrestige,
                BoardItems = new List<CardInfo>(), StorageItems = new List<CardInfo>(),
                OpponentItems = new List<CardInfo>(), SkillCards = new List<CardInfo>(),
                Shops = new List<ShopInfo>()
            };

            var shopCards = new List<CardInfo>();
            string bestShopName = "";
            var seenIds = new HashSet<string>();

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
                            if (typeStr != "Item" && typeStr != "Skill") continue;
                            var name = GetCardNameStatic(cd);
                            if (string.IsNullOrEmpty(name) || name == "???") continue;
                            var iid = cd.InstanceId != null ? cd.InstanceId.ToString() : "";
                            if (!string.IsNullOrEmpty(iid) && seenIds.Contains(iid)) continue;
                            if (!string.IsNullOrEmpty(iid)) seenIds.Add(iid);

                            var section = GetCardSection(cd);
                            var uiCtx = GetUiContext(cc);
                            bool isPlayer = IsPlayerItem(cc.transform);
                            int? price = GetCardPrice(cc);

                            var ci = new CardInfo { Name = GetCardName(cd), InstanceId = iid, CardType = typeStr, CardSize = cd.Size.ToString(), Tier = cd.Tier.ToString(), Location = section, Attributes = new Dictionary<string, int>() };
                            if (cd.Attributes != null) foreach (var kv in cd.Attributes) if (kv.Value != 0) ci.Attributes[kv.Key.ToString()] = kv.Value;
                            if (price.HasValue) ci.Attributes["Price"] = price.Value;

                            if (typeStr == "Skill") continue;

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
                            else if (isOpp) { d.OpponentItems.Add(ci); }
                            else if (isPlayer && typeStr == "Item")
                            {
                                if (section.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0) d.BoardItems.Add(ci);
                                else if (section.IndexOf("Stash", StringComparison.OrdinalIgnoreCase) >= 0) d.StorageItems.Add(ci);
                                else { try { if (_isPlayerBoardProp != null && ((bool)_isPlayerBoardProp.GetValue(cc, null))) d.BoardItems.Add(ci); else d.StorageItems.Add(ci); } catch { d.BoardItems.Add(ci); } }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 回退: 旧方法
            if (d.BoardItems.Count == 0 && d.StorageItems.Count == 0 && _playerCardsOnBoardField != null)
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
                                var ci = new CardInfo { Name = GetCardName(cd), InstanceId = cd.InstanceId != null ? cd.InstanceId.ToString() : "", CardType = cd.Type.ToString(), CardSize = cd.Size.ToString(), Tier = cd.Tier.ToString(), Location = loc, Attributes = new Dictionary<string, int>() };
                                if (cd.Attributes != null) foreach (var kv in cd.Attributes) if (kv.Value != 0) ci.Attributes[kv.Key.ToString()] = kv.Value;
                                if (loc == "Board") d.BoardItems.Add(ci); else d.StorageItems.Add(ci);
                            }
                        }
                    }
                }
                catch { }
            }

            // 技能
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
                                        var bm = r.transform.GetComponentInParent<BoardManager>();
                                        if (bm == null || bm != BoardManager.Instance) continue;
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
                        d.Shops.Add(new ShopInfo { GameObjectName = t.name, DisplayName = Translate(t.name), Position = string.Format("{0:F1},{1:F1},{2:F1}", t.position.x, t.position.y, t.position.z) });
                    }
                }
            }
            catch { }

            if (shopCards.Count > 0)
            {
                if (bestShopName.EndsWith("(Clone)")) bestShopName = bestShopName.Substring(0, bestShopName.Length - 7);
                d.CurrentShop = new ShopDetail { ShopName = bestShopName, DisplayName = string.IsNullOrEmpty(bestShopName) ? "" : Translate(bestShopName), Items = shopCards };
            }

            return d;
        }

        private void ExportToJson(BoardData d)
        {
            var dir = Path.Combine(Paths.GameRootPath, "BoardData");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, string.Format("board_{0}.json", DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            WriteJsonAtomic(p, JsonConvert.SerializeObject(d, Formatting.Indented));
            _logger.LogInfo(string.Format("[BoardReader] 导出: 物品={0} 仓库={1} 对手={2} 技能={3} 商店={4} 商店物品={5}",
                d.BoardItems.Count, d.StorageItems.Count, d.OpponentItems.Count, d.SkillCards.Count,
                d.Shops.Count, d.CurrentShop != null ? d.CurrentShop.Items.Count : 0));
        }
"""
    text = text[:json_start] + new_json + text[data_class_start:]
    changes += 1
    print("3. JSON导出 + 所有新方法 — OK")
else:
    errors.append("3. JSON导出边界")
    print("FAIL: 找不到JSON导出边界")

# === 4a. BoardData 类（添加 OpponentItems, CurrentShop） ===
replace(
    """    [Serializable]
    public class BoardData
    {
        public string Timestamp;
        public int Day;
        public string Hero;
        public int Gold;
        public int Income;
        public int Health;
        public int Prestige;
        public List<CardInfo> BoardItems;
        public List<CardInfo> StorageItems;
        public List<CardInfo> SkillCards;
        public List<ShopInfo> Shops;
    }""",
    """    [Serializable]
    public class BoardData
    {
        public string Timestamp;
        public int Day;
        public string Hero;
        public int Gold;
        public int Income;
        public int Health;
        public int Prestige;
        public List<CardInfo> BoardItems;
        public List<CardInfo> StorageItems;
        public List<CardInfo> OpponentItems;
        public List<CardInfo> SkillCards;
        public List<ShopInfo> Shops;
        public ShopDetail CurrentShop;
    }""",
    "4a. BoardData 类"
)

# === 4b. ShopInfo/ShopDetail 类 ===
replace(
    """    [Serializable]
    public class ShopInfo
    {
        public string GameObjectName;
        public string DisplayName;
        public string Position;
    }""",
    """    [Serializable]
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
    }""",
    "4b. ShopInfo + ShopDetail 类"
)

# === 保存 ===
if len(errors) == 0:
    print(f"\n全部 {changes} 处修改成功，写入文件...")
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("完成!")
else:
    print(f"\n失败 {len(errors)} 处: {errors}")
    sys.exit(1)
