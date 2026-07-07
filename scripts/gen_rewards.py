"""从CSV生成事件奖励JSON"""
import json, csv, os
BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

events = {}
current = None
with open(os.path.join(BASE, 'temp', '事件专属奖励.csv'), 'r', encoding='utf-8-sig') as f:
    for row in csv.reader(f):
        r = [c.strip().replace('﻿','') for c in row]
        if not r or all(c=='' for c in r): continue
        name = r[0]
        if name and name != '事件名称':
            current = name
            if current not in events: events[current] = []
        if current and len(r) >= 3:
            hero, reward = r[1], r[2]
            if hero or reward:
                events[current].append({'hero': hero, 'reward': reward})

path = os.path.join(BASE, 'data', 'event_rewards.json')
with open(path, 'w', encoding='utf-8') as f:
    json.dump(events, f, ensure_ascii=False, indent=2)
print("OK:", len(events), "events")
for k in events: print(" ", k)
