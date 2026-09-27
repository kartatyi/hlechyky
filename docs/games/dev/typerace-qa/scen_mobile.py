"""Телефон 375×812 (сенсор): тап по тексту ставить фокус, друк по літері; «свайп» (ціле слово за раз) відкинуто."""
import json, sys, time
from tr import Page, run_bots, room_state
n = int(sys.argv[1]) if len(sys.argv) > 1 else 3
p = Page(9731, "Оля", 375, 812, mobile=True)
rid = p.create("typerace", {"length": "short", "source": "classic"})
print("стіл", rid)
th, bots = run_bots(8232, rid, ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"][:n - 1], cpm=[150 + 30 * i for i in range(n - 1)])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
time.sleep(1.2)
p.dismiss()
p.shot(f"m{n}-ready.png")
p.wait_phase("go")
time.sleep(0.3)
p.ev("document.activeElement && document.activeElement.blur(); return 1;")
r = p.ev("const b = document.querySelector('.grbox .tr-textbox').getBoundingClientRect(); return [b.x + b.width / 2, b.y + b.height / 2];")
p.tap(r[0], r[1])
time.sleep(0.4)
print("фокус після тапу:", p.focused())
p.type_text(cpm=200, errors=0.03, stop_at=0.3)
c0 = p.state()["c"]
p.insert("хмарами")          # як свайп-клавіатура: ціле слово за раз
time.sleep(0.3)
st = p.state()
print("свайп: c до", c0, "після", st["c"], "; підказка:", p.ev("const h = document.querySelector('.grbox .tr-hint'); return h.hidden ? null : h.textContent;"))
p.shot(f"m{n}-go.png")
print("ширина:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth]"))
p.type_text(cpm=220, errors=0.02, seed=4)
for _ in range(200):
    rs = room_state(p)
    if rs["phase"] == "done": break
    time.sleep(0.5)
time.sleep(1.8)
p.shot(f"m{n}-done.png")
p.shot(f"m{n}-done-full.png", full=True)
print("ширина done:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth]"))
for l in p.logs(): print('LOG', l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
