"""Дуель: Оля в браузері (справжні натиски) проти бота Петра. Знімки: лобі, відлік, заїзд, підсумок; консоль і вид."""
import json, sys, time
from tr import Page, run_bots, room_state

W = int(sys.argv[1]) if len(sys.argv) > 1 else 1280
H = int(sys.argv[2]) if len(sys.argv) > 2 else 1000
tag = sys.argv[3] if len(sys.argv) > 3 else "duel"
opts = json.loads(sys.argv[4]) if len(sys.argv) > 4 else {"length": "short", "source": "classic"}

p = Page(9731, "Оля", W, H)
rid = p.create("typerace", opts)
print("стіл", rid)
th, bots = run_bots(8232, rid, ["Петро"], cpm=[200])
time.sleep(2.5)
p.shot(f"{tag}-lobby.png")
print(p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r;"))
time.sleep(1.0)
p.shot(f"{tag}-ready.png")
s = p.wait_phase("go")
time.sleep(0.4)
print("фокус після старту:", p.focused())
p.type_text(cpm=300, errors=0.05, stop_at=0.5)
p.shot(f"{tag}-go.png")
p.type_text(cpm=300, errors=0.03, seed=2)
time.sleep(1.5)
p.shot(f"{tag}-fin.png")
for _ in range(120):
    rs = room_state(p)
    if rs and rs["phase"] == "done":
        break
    time.sleep(0.5)
time.sleep(1.8)
p.shot(f"{tag}-done.png")
rs = room_state(p)
print(json.dumps(rs, ensure_ascii=False)[:1500])
print("бот:", bots[0].log)
print("статус:", p.ev("return document.querySelector('.grbox .gstatus') && document.querySelector('.grbox .gstatus').textContent"))
for line in p.logs():
    print("LOG", line)
print(p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;"))
