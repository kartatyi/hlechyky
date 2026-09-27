"""Після рецензій (27.09): телефон 375×812 (сенсор), десятеро. Кругла кнопка «💬 Стіл» не лягає на поточний рядок
(заміряємо перетин прямокутників на кожному новому рядку), відлік — над трасою, ніки з номерами на вузьких доріжках,
підсумок; без горизонтальної прокрутки.
    python scen_review_phone.py [гравців=10]
"""
import json, sys, time
from tr import Page, run_bots, room_state

n = int(sys.argv[1]) if len(sys.argv) > 1 else 10
p = Page(9731, "Оля", 375, 812, mobile=True)
rid = p.create("typerace", {"length": "medium", "source": "classic"})
names = ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"][:n - 1]
th, bots = run_bots(8232, rid, names, cpm=[160 + 25 * i for i in range(n - 1)])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
time.sleep(1.3)
p.dismiss()
p.shot("rv-phone-ready.png")
print("відлік:", p.ev("const c = document.querySelector('.grbox .tr-count'); return [c.parentElement.className, c.textContent, getComputedStyle(c).fontSize]"))
p.wait_phase("go")
time.sleep(0.3)
p.ev("document.activeElement && document.activeElement.blur(); return 1;")
r = p.ev("const b = document.querySelector('.grbox .tr-textbox').getBoundingClientRect(); return [b.x + b.width / 2, b.y + 20];")
p.tap(r[0], r[1])
time.sleep(0.4)
print("фокус після тапу:", p.focused())
FAB = """const f = document.querySelector('.tchat .tc-head'), w = document.querySelector('.grbox .tr-word');
  if (!f || !w || !f.getClientRects().length) return 'кнопки нема';
  const a = f.getBoundingClientRect(), b = w.getBoundingClientRect();
  const hit = !(a.right < b.left || a.left > b.right || a.bottom < b.top || a.top > b.bottom);
  return {fab: [Math.round(a.left), Math.round(a.top)], word: [Math.round(b.left), Math.round(b.top), Math.round(b.bottom)], hit};"""
hits = 0
checks = 0
s = p.state()
total = len(s["text"])
for part in (0.2, 0.4, 0.6, 0.8, 0.97):
    p.type_text(cpm=240, errors=0.02, seed=int(part * 100), stop_at=part)
    time.sleep(0.25)
    res = p.ev(FAB)
    checks += 1
    if isinstance(res, dict) and res["hit"]:
        hits += 1
    print(f"  на {int(part * 100)} %:", res)
    if part == 0.6:
        p.shot("rv-phone-go.png")
print("кнопка балачки лягла на поточне слово:", hits, "з", checks)
print("ширина:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth]"))
p.type_text(cpm=240, errors=0.02, seed=99)
for _ in range(400):
    rs = room_state(p)
    if rs["phase"] == "done":
        break
    time.sleep(0.5)
time.sleep(1.8)
p.shot("rv-phone-done.png")
p.shot("rv-phone-done-full.png", full=True)
print("ширина done:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth]"))
print("перф:", p.ev("return __typerace.perf()"))
for l in p.logs():
    print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
