"""Звірка банку Клавоперегонів із Вікіджерелами (spec §7.2–7.3).

Для кожного запису kind == "classic" тягне сторінку з поля "page" (action=parse, prop=text — разом із транскльованими
сканами, яких у wikitext нема), зводить і сторінку, і уривок тим самим normalize(), що й гра (TyperaceText.Normalize у
C#), і вимагає, щоб уривок був дослівним підрядком сторінки. Друкує відхилення й підсумок.

У тестах не запускається (мережа). Запуск — повним шляхом до Python (див. AGENT-COMMON):

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe data/typerace/check.py [--only id1,id2]
"""
import html
import json
import os
import re
import sys
import time
import unicodedata
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
UA = {"User-Agent": "HlechykyGames/1.0 (https://hlechyky.pp.ua)"}

APOS = set("'’ʼ‘`´′")
HYPH = set("-‐‑")
DASH = set("‒–—―−")
QUOT = set('"„“”«»‟')


def normalize(raw: str) -> str:
    """Дзеркало TyperaceText.Normalize (C#). Міняти разом."""
    if not raw:
        return ""
    s = "".join(ch for ch in raw if ch not in "\u0301\u0300\u00ad\u200b\u200c\u200d\u2060\ufeff")
    s = unicodedata.normalize("NFC", s)
    a = []
    i = 0
    while i < len(s):
        ch = s[i]
        if ch == "\r":
            a.append("\n")
            if i + 1 < len(s) and s[i + 1] == "\n":
                i += 1
        elif ch == "\n":
            a.append("\n")
        elif ch == "…":
            a.append("...")
        elif ch in APOS:
            a.append("’")
        elif ch.isspace():
            a.append(" ")
        else:
            a.append(ch)
        i += 1
    b = []
    sp = br = False
    for ch in "".join(a):
        if ch == " ":
            sp = True
            continue
        if ch == "\n":
            br = True
            continue
        if b:
            if br:
                b.append("\n")
            elif sp:
                b.append(" ")
        sp = br = False
        b.append(ch)
    t = "".join(b)
    c = []
    for i, ch in enumerate(t):
        prev = c[-1] if c else "\n"
        nxt = t[i + 1] if i + 1 < len(t) else "\n"
        if ch in HYPH or ch in DASH:
            if ch in HYPH and prev.isalnum() and nxt.isalnum():
                c.append("-")
                continue
            if prev not in (" ", "\n", "(", "«"):
                c.append(" ")
            c.append("—")
            if nxt not in (" ", "\n", ",", ".", ")", "»", "!", "?", ";", ":"):
                c.append(" ")
            continue
        if ch in QUOT:
            opening = prev in (" ", "\n", "(", "—", "«") or not c
            if not opening and nxt.isalpha() and prev in (":", ","):
                opening = True
            c.append("«" if opening else "»")
            continue
        c.append(ch)
    d = []
    for ch in "".join(c):
        if ch == " " and d and d[-1] in (" ", "\n"):
            continue
        if ch == "\n" and d and d[-1] == " ":
            d.pop()
        d.append(ch)
    return "".join(d).strip(" \n")


ALLOWED = set("абвгґдеєжзиіїйклмнопрстуфхцчшщьюяАБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ0123456789 \n.,;:!?-—’()«»\"")


def typeable(text: str) -> bool:
    if not text or text[0] in " \n" or text[-1] in " \n":
        return False
    for i, ch in enumerate(text):
        if ch not in ALLOWED:
            return False
        if i and ch in " \n" and text[i - 1] in " \n":
            return False
    return True


def page_text(title: str) -> str:
    """HTML сторінки (з транслюзією сканів) → текст: теги геть, <br>/абзаци — переноси."""
    q = urllib.parse.urlencode({"action": "parse", "prop": "text", "page": title, "disabletoc": "1",
                                "redirects": "1", "format": "json", "formatversion": "2"})
    with urllib.request.urlopen(urllib.request.Request("https://uk.wikisource.org/w/api.php?" + q, headers=UA), timeout=60) as r:
        h = json.loads(r.read().decode())["parse"]["text"]
    h = re.sub(r"(?is)<(style|script|table|sup|sub)[^>]*>.*?</\1>", " ", h)
    h = re.sub(r'(?is)<span[^>]*class="[^"]*(pagenum|ws-pagenum|reference|mw-editsection)[^"]*"[^>]*>.*?</span>', " ", h)
    h = re.sub(r"(?i)<br\s*/?>", "\n", h)
    h = re.sub(r"(?i)</(p|div|h\d|li|dd|dt|tr)>", "\n\n", h)
    return html.unescape(re.sub(r"(?s)<[^>]+>", "", h))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    only = None
    if "--only" in sys.argv:
        only = set(sys.argv[sys.argv.index("--only") + 1].split(","))
    bank = json.load(open(os.path.join(HERE, "texts.json"), encoding="utf-8"))
    ok = bad = 0
    cache = {}
    for t in bank["texts"]:
        if only and t["id"] not in only:
            continue
        text = t["text"]
        if not typeable(normalize(text)):
            print("НЕДРУКОВАНЕ:", t["id"])
            bad += 1
            continue
        if t.get("kind") != "classic":
            continue
        page = t.get("page")
        if not page:
            print("БЕЗ СТОРІНКИ:", t["id"])
            bad += 1
            continue
        if page not in cache:
            try:
                cache[page] = normalize(page_text(page))
            except Exception as e:  # noqa: BLE001 — мережа, сторінка зникла
                print("НЕ ВІДКРИЛАСЬ:", t["id"], page, e)
                bad += 1
                continue
            time.sleep(0.2)
        if normalize(text) in cache[page]:
            ok += 1
        else:
            bad += 1
            print("НЕ ЗБІГАЄТЬСЯ:", t["id"], page)
    print(f"звірено: {ok} дослівно, відхилень: {bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
