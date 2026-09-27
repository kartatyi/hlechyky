"""Звірка журналу JS ↔ C#: TyperaceCore.log на трьох сценаріях == TyperaceLogs.FromJs; TyperaceCore.capped (стеля
проковтнутих натисків) == TyperaceLogs.FromJsCapped; ще й same() на класах знаків."""
import json, os, re
from tr import Page
p = Page(9731, "Оля", 1280, 900)
src = open(os.path.join(__import__('tr')._root(), 'docs/games/dev/') + 'typerace-parity.js', encoding='utf-8').read()
body = src.split("*/", 1)[1].strip().rstrip(";")
js = p.ev("return " + body)
cs = open(os.path.join(__import__('tr')._root(), 'tests/') + 'Hlechyky.Tests/Games/TyperaceLogs.cs', encoding='utf-8').read()
block = cs.split('FromJs =')[1].split('];')[0]
pairs = re.findall(r'\("([^"]*)",\s*"([^"]*)"\)', block)
for i, (k, d) in enumerate(pairs):
    print(i, "k", js[i]["k"] == k, "d", js[i]["d"] == d)
capped = re.search(r'FromJsCapped =\s*\("([^"]*)",\s*"([^"]*)"\)', cs)
print("capped", "k", js[3]["k"] == capped.group(1), "d", js[3]["d"] == capped.group(2))
print("same:", p.ev("""const S = TyperaceCore.same; return [S('’', "'"), S('—', '-'), S('«', '"'), S(String.fromCharCode(10), ' '), S('С', 'с'), S('с', 'c'), S('’', '"')]"""))
