"""Оля встає посеред заїзду кнопкою «Встати»: трактор сіріє, решта їде; картка лишається глядацькою, помилок нема."""
import json, time
from tr import Page, run_bots, room_state
p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "classic"})
th, bots = run_bots(8232, rid, ["Петро", "Ганна"], cpm=[200, 170])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
p.type_text(cpm=250, errors=0.03, stop_at=0.3)
print(p.ev("const b = [...document.querySelectorAll('.grbox .gbtns button')].find(x => /Встати/.test(x.textContent)); b.click(); return !!b;"))
time.sleep(2)
p.goto(rid)
time.sleep(2)
rs = room_state(p)
print("після виходу:", [(r['nick'], r['gone'], r['s'], r['c']) for r in rs['racers']], rs['phase'])
p.shot("leave-go.png")
p.insert("а")    # друк після виходу нікуди не йде
time.sleep(0.3)
print("стат:", p.ev("return document.querySelector('.grbox .tr-nums').textContent"), "| статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
for _ in range(200):
    rs = room_state(p)
    if rs["phase"] == "done": break
    time.sleep(0.5)
time.sleep(1.5)
p.shot("leave-done.png")
print(json.dumps(rs["result"], ensure_ascii=False))
for l in p.logs(): print("LOG", l)
