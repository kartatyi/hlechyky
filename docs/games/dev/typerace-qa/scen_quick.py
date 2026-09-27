"""Швидкий знімок: стіл на N (боти), Оля друкує трохи, знімок посеред заїзду."""
import json, sys, time
from tr import Page, run_bots, room_state
n = int(sys.argv[1]) if len(sys.argv) > 1 else 4
W = int(sys.argv[2]) if len(sys.argv) > 2 else 1280
H = int(sys.argv[3]) if len(sys.argv) > 3 else 1000
tag = sys.argv[4] if len(sys.argv) > 4 else 'quick'
mobile = len(sys.argv) > 5 and sys.argv[5] == 'mobile'
p = Page(9731, "Оля", W, H, mobile=mobile)
rid = p.create("typerace", {"length": "medium", "source": "classic"})
names = ["Петро", "Ганна", "Іван", "Тарас", "Ірина", "Сашко", "Марко", "Леся", "Остап"][:n - 1]
th, bots = run_bots(8232, rid, names, cpm=[150 + 40 * i for i in range(n - 1)])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
time.sleep(1.2)
p.shot(f"{tag}-ready.png")
p.wait_phase("go")
p.type_text(cpm=320, errors=0.05, stop_at=0.35)
p.insert('ж')
time.sleep(0.4)
p.shot(f"{tag}-go.png")
print(json.dumps(p.ev("return __typerace.perf()"), ensure_ascii=False))
for l in p.logs(): print('LOG', l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
