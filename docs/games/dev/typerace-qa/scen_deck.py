"""Steam Deck: 1280×800, ?deck=1, фейковий пад (fakepad.js до pad.js). Кільце на полі для друку, Ⓐ — клавіатура пада, Ⓐ ще раз — літера."""
import json, os, sys, time
from tr import Page, run_bots, room_state, BASE
n = int(sys.argv[1]) if len(sys.argv) > 1 else 10
url = BASE.replace('/?cb=', '/?deck=1&cb=')
p = Page(9731, "Оля", 1280, 800, url=url)
fake = open(os.path.join(__import__('tr')._root(), 'docs/games/dev/') + 'fakepad.js', encoding='utf-8').read()
p.tab.call("Page.addScriptToEvaluateOnNewDocument", source=fake)
p.tab.call("Page.reload")
time.sleep(4)
p.tab.events = []
rid = p.create("typerace", {"length": "long", "source": "classic"})
print("стіл", rid)
th, bots = run_bots(8232, rid, ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"][:n - 1], cpm=[150 + 25 * i for i in range(n - 1)])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
time.sleep(0.6)
print("пад:", p.ev("return {on: HPad.on, pads: HPad.pads, deck: HPad.deck}"))
print("прокрутка:", p.ev("return [document.scrollingElement.scrollHeight, innerHeight, __typerace.state().text.split(String.fromCharCode(10)).length]"))
p.ev("document.activeElement && document.activeElement.blur(); HPad.focus(document.querySelector('.grbox .tr-in')); return 1;")
time.sleep(0.3)
p.ev("padHit(0); return 1;")         # перший дотик будить пад
time.sleep(0.4)
p.ev("HPad.focus(document.querySelector('.grbox .tr-in')); padHit(0); return 1;")   # Ⓐ на полі — клавіатура пада
time.sleep(0.6)
print("клавіатура пада:", p.ev("return !!document.querySelector('.padkbd')"))
ev0 = p.state()["events"]
p.ev("padHit(0); return 1;")         # Ⓐ — перша літера клавіатури
time.sleep(0.5)
st = p.state()
print("подій у журналі:", ev0, "→", st["events"], "; журнал:", p.ev("return __typerace.state().k"))
p.shot(f"deck{n}-go.png")
p.ev("padHit(1); return 1;")         # Ⓑ — закрити клавіатуру
time.sleep(0.4)
p.shot(f"deck{n}-go2.png")
p.ev("document.querySelector('.grhead .gfullbtn, [data-full], .gfull-toggle') ? 1 : 0; return 1;")
print("кнопки шапки:", p.ev("return [...document.querySelectorAll('button')].filter(b => /⛶/.test(b.textContent) || /весь екран/i.test(b.title || '')).map(b => b.className + '|' + b.title)"))
p.ev("const b = [...document.querySelectorAll('button')].find(b => /⛶/.test(b.textContent) || /весь екран/i.test(b.title || '')); if (b) b.click(); return !!b;")
time.sleep(0.8)
print("⛶ прокрутка:", p.ev("return [document.scrollingElement.scrollHeight, innerHeight]"))
p.shot(f"deck{n}-full.png")
for l in p.logs(): print('LOG', l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
