"""Після рецензій (27.09): Steam Deck 1280×800, ?deck=1 + фейковий пад, десятеро за столом (9 ботів).

Перевіряє: кільце пада в лобі — на «Почати» (не на «Ефір» у шапці); відлік — над трасою, текст чистий; на вузьких
доріжках видно номер і нік; чіпи в шапці кольорами тракторів; клавіатура пада не закриває поточний рядок; після
фінішу — гудки (клавіша 1) над трактором; дотяжка хвоста для повільного біля фінішу; підсумок на десятьох влазить у
800 px разом із «Ще раз»; кільце пада в підсумку — на «Ще раз»; слово-пастка.
    python scen_review_deck.py [довжина=medium] [мій_cpm=340]
"""
import json, os, sys, time
from tr import Page, run_bots, room_state, BASE

length = sys.argv[1] if len(sys.argv) > 1 else "medium"
my_cpm = int(sys.argv[2]) if len(sys.argv) > 2 else 340
url = BASE.replace('/?cb=', '/?deck=1&cb=')
p = Page(9731, "Оля", 1280, 800, url=url)
fake = open(os.path.join(__import__('tr')._root(), 'docs/games/dev/') + 'fakepad.js', encoding='utf-8').read()
p.tab.call("Page.addScriptToEvaluateOnNewDocument", source=fake)
p.tab.call("Page.reload")
time.sleep(4)
p.tab.events = []
rid = p.create("typerace", {"length": length, "source": "classic"})
print("стіл", rid)
names = ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"]
cpm = [420, 380, 330, 300, 260, 220, 190, 170, 150]
robot = [False, False, False, False, True, False, False, False, False]
leave = [None, None, None, None, None, None, 0.4, None, None]
th, bots = run_bots(8232, rid, names, cpm=cpm, robot=robot, leave_at=leave)
time.sleep(3.5)
p.dismiss()

# ---- лобі: куди стає кільце пада
p.ev("padHit(0); return 1;")          # перший дотик будить пад і ставить кільце на першу ціль
time.sleep(0.5)
print("лобі, кільце на:", p.ev("const a = document.activeElement; return a ? (a.dataset.do || a.className || a.tagName) + ' «' + (a.textContent || '').trim().slice(0, 20) + '»' : null"))
p.shot("rv-deck-lobby.png")

# ---- відлік
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
time.sleep(1.3)
print("відлік:", p.ev("const c = document.querySelector('.grbox .tr-count'); return c ? [c.parentElement.className, c.textContent, c.hidden, getComputedStyle(c).fontSize] : null"))
p.shot("rv-deck-ready.png")
print("чіпи:", p.ev("return [...document.querySelectorAll('.grbox .gseat')].slice(0, 10).map(e => e.textContent + ':' + getComputedStyle(e).color)"))

# ---- заїзд
p.wait_phase("go")
p.type_text(cpm=my_cpm, errors=0.03, stop_at=0.3)
p.shot("rv-deck-go.png")
print("доріжка, px:", p.ev("return __typerace.state().laneH"), "прокрутка:", p.ev("return [document.scrollingElement.scrollHeight, innerHeight]"))

# клавіатура пада: поточний рядок має лишитись над нею
p.ev("document.activeElement && document.activeElement.blur(); HPad.focus(document.querySelector('.grbox .tr-in')); padHit(0); return 1;")
time.sleep(0.8)
print("клавіатура пада:", p.ev("""const k = document.querySelector('.padkbd'), i = document.querySelector('.grbox .tr-in'), w = document.querySelector('.grbox .tr-word');
  if (!k) return 'нема'; const kr = k.getBoundingClientRect(), wr = w.getBoundingClientRect();
  return {kbdTop: Math.round(kr.top), wordBottom: Math.round(wr.bottom), visible: wr.bottom <= kr.top};"""))
p.shot("rv-deck-padkbd.png")
p.ev("padHit(1); return 1;")          # Ⓑ — закрити клавіатуру
time.sleep(0.5)
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
time.sleep(0.2)
p.type_text(cpm=my_cpm, errors=0.03, seed=5)
time.sleep(1.5)
# ---- гудки з фінішу
print("гудки видно:", p.ev("const c = document.querySelector('.grbox .tr-cheers'); return c && !c.hidden"))
p.ev("document.activeElement && document.activeElement.blur(); return 1;")
p.key("1", "Digit1", 49)
time.sleep(0.45)
print("гудків у польоті:", p.ev("return __typerace.state().emoN"))
p.shot("rv-deck-cheer.png")
time.sleep(0.8)
p.ev("document.querySelector('.grbox .tr-cheer[data-cheer=\"3\"]').click(); return 1;")
time.sleep(0.35)
p.shot("rv-deck-cheer2.png")

# ---- дотяжка й кінець
extra = 0
for _ in range(600):
    rs = room_state(p)
    if rs and rs["phase"] == "done":
        break
    v = p.ev("return __typerace.state().ctx.view.extra")
    if v and v > extra:
        extra = v
        print("дотяжка:", v, "раз(и); підказка:", p.ev("const h = document.querySelector('.grbox .tr-hint'); return h && !h.hidden ? h.textContent : null"))
        if v == 1:
            p.shot("rv-deck-stretch.png")
    time.sleep(0.5)
time.sleep(1.8)
p.shot("rv-deck-done.png")
rs = room_state(p)
for r in rs["racers"]:
    print(" ", r["seat"], r["nick"], "c", r["c"], "fin", r["fin"], "place", r["place"], "cpm", r["cpm"], "flag", r["flag"], "gone", r["gone"])
print("result", json.dumps(rs["result"], ensure_ascii=False))
print("підсумок влазить:", p.ev("""const b = [...document.querySelectorAll('.grbox .gbtns button')].find(x => /Ще раз/.test(x.textContent));
  return {page: document.scrollingElement.scrollHeight, screen: innerHeight, again: b ? Math.round(b.getBoundingClientRect().bottom) : null};"""))
print("медалі:", p.ev("return [...document.querySelectorAll('.grbox .tr-table tbody tr')].map(r => r.querySelector('.tr-medal').textContent + ' ' + r.querySelector('.tr-bnick').textContent)"))
print("пастка:", p.ev("const t = document.querySelector('.grbox .tr-trap'); return t ? t.textContent : null"))
print("кільце пада в підсумку:", p.ev("const e = document.querySelector('[data-pad-first]'); return e ? (e.dataset.do || e.className) : null"))
for l in p.logs():
    print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
