"""Десятеро: Оля в браузері (Full HD) + 9 ботів (один — «метроном», один встає посеред заїзду) + глядач у другому Chrome."""
import json, sys, time
from tr import Page, run_bots, room_state

W = int(sys.argv[1]) if len(sys.argv) > 1 else 1920
H = int(sys.argv[2]) if len(sys.argv) > 2 else 1080
tag = sys.argv[3] if len(sys.argv) > 3 else "ten"
length = sys.argv[4] if len(sys.argv) > 4 else "long"

a = Page(9731, "Оля", W, H)
rid = a.create("typerace", {"length": length, "source": "classic"})
print("стіл", rid)
names = ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"]
cpm = [420, 380, 330, 300, 260, 220, 190, 170, 150]
robot = [False, False, False, False, True, False, False, False, False]
leave = [None, None, None, None, None, None, 0.4, None, None]
th, bots = run_bots(8232, rid, names, cpm=cpm, robot=robot, leave_at=leave)
time.sleep(3)
b = Page(9732, "Глядач", 1280, 900)
b.goto(rid)
b.dismiss()
a.dismiss()
print(a.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;"))
a.wait_phase("go")
a.type_text(cpm=340, errors=0.03, stop_at=0.45)
a.shot(f"{tag}-a-go.png")
b.shot(f"{tag}-b-go.png")
print("прокрутка A:", a.ev("return [document.scrollingElement.scrollHeight, innerHeight, document.scrollingElement.scrollWidth, innerWidth]"))
a.ev("__typerace.reset(); return 1;"); b.ev("__typerace.reset(); return 1;")
a.type_text(cpm=340, errors=0.03, seed=7, stop_at=0.6)    # ~5 с друку — щонайменше 300 кадрів
print("перф A:", a.ev("return __typerace.perf()"), "B:", b.ev("return __typerace.perf()"))
a.type_text(cpm=340, errors=0.02, seed=3)
for _ in range(300):
    rs = room_state(a)
    if rs and rs["phase"] == "done":
        break
    time.sleep(0.5)
time.sleep(1.8)
a.shot(f"{tag}-a-done.png")
b.shot(f"{tag}-b-done.png")
rs = room_state(a)
for r in rs["racers"]:
    print(r["seat"], r["nick"], "c", r["c"], "fin", r["fin"], "place", r["place"], "cpm", r["cpm"], "acc", r["acc"], "flag", r["flag"], "gone", r["gone"])
print("result", json.dumps(rs["result"], ensure_ascii=False))
print("прокрутка A done:", a.ev("return [document.scrollingElement.scrollHeight, innerHeight]"))
print("статус B:", b.ev("return document.querySelector('.grbox .gstatus').textContent"))
for bt in bots:
    print(bt.nick, bt.log[-1:] if bt.log else None)
for l in a.logs(): print("LOG A", l)
for l in b.logs(): print("LOG B", l)
a.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
