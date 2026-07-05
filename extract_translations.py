"""从游戏缓存数据库提取英文→中文翻译"""
import sqlite3, json, os, re

LOCALLOW = os.path.join(os.environ['LOCALAPPDATA'], '..', 'LocalLow',
    'Tempo Storm', 'The Bazaar', 'prod', 'cache')
GDATA = os.path.join(LOCALLOW, 'GameData.db')
ZHTRANS = os.path.join(LOCALLOW, 'translations', 'zh-CN.bytes')
OUTPUT = r'F:\SteamLibrary\steamapps\common\The Bazaar\BazaarBoardReader\translations_zh_cn.json'

# 1. 加载所有翻译 (hash → Chinese)
conn = sqlite3.connect(ZHTRANS)
c = conn.cursor()
c.execute('SELECT hash, text FROM translation')
trans = dict(c.fetchall())
conn.close()
print(f'翻译 hash 表: {len(trans)} 条')

# 2. 从 GameData.db 提取所有英文文本
conn = sqlite3.connect(GDATA)
c = conn.cursor()

result = {}

def extract_texts(table):
    """从表中提取所有 Localization 下的 Key→Text 映射"""
    count = 0
    c.execute(f'SELECT Data FROM {table}')
    for (blob,) in c:
        try:
            j = json.loads(blob)
        except:
            continue
        # 遍历所有 Localization 下的 Key→Text
        _extract_loc(j, result)
        count += 1
    print(f'  处理 {table}: {count} 条')

def _extract_loc(obj, out):
    """递归提取所有含有 Key/Text 的 Localization 节点"""
    if isinstance(obj, dict):
        if 'Key' in obj and 'Text' in obj:
            key = obj['Key']
            text = obj['Text']
            if key in trans and text and not _has_zh(text):
                out[text] = trans[key]
        else:
            for v in obj.values():
                _extract_loc(v, out)
    elif isinstance(obj, list):
        for v in obj:
            _extract_loc(v, out)

def _has_zh(s):
    return any('一' <= ch <= '鿿' for ch in s)

for table in ['cards', 'monsters', 'tooltips']:
    try:
        extract_texts(table)
    except Exception as e:
        print(f'  跳过 {table}: {e}')

conn.close()

# 3. 保存
with open(OUTPUT, 'w', encoding='utf-8') as f:
    json.dump({'by_name': result}, f, ensure_ascii=False, indent=2)
print(f'\n总翻译: {len(result)} 条')
print(f'已保存: {OUTPUT}')

# 验证几个关键条目
for check in ['Blight Rage', 'Capital Punisher', 'Tiny Furry Creature',
              'Tiny Furry Monster', 'Crash Site Expedition Ticket', 'Crash Site Ticket']:
    if check in result:
        print(f'  ✅ {check} -> {result[check]}')
    else:
        print(f'  ❌ {check} 未找到')
