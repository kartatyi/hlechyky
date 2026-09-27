"""F5 посеред заїзду: набране, журнал і час відновлюються; фініш після F5 зараховано. Далі «Ще раз» (боти лишаються)."""
import json, sys, time
from tr import Page, run_bots, room_state
p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "classic"})
th, bots = run_bots(8232, rid, ["Петро"], cpm=[140], rounds=2)
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
p.type_text(cpm=280, errors=0.04, stop_at=0.45)
before = p.state()
print("до F5:", {k: before[k] for k in ('c', 'events', 'localGo')})
time.sleep(0.5)
p.tab.call("Page.reload")
time.sleep(4)
p.dismiss()
after = p.state()
print("після F5:", {k: after[k] for k in ('c', 'events', 'localGo', 'phase')} if after else None)
rs = room_state(p)
me = [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]
print("сервер бачить c =", me["c"])
p.shot("f5-after.png")
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
p.type_text(cpm=280, errors=0.03, seed=5)
for _ in range(200):
    rs = room_state(p)
    if rs["phase"] == "done": break
    time.sleep(0.5)
me = [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]
print("фініш після F5:", {k: me[k] for k in ('fin', 'place', 'cpm', 'acc', 'wrong', 'flag')})
time.sleep(1)
# «Ще раз» кнопкою каркаса
seats_before = p.ev("return [...document.querySelectorAll('.grbox .gseat')].map(e => e.textContent)")
print(p.ev("const b = [...document.querySelectorAll('.grbox .gbtns button')].find(x => /Ще раз/.test(x.textContent)); if (!b) return 'нема кнопки'; b.click(); return 'ok';"))
time.sleep(1.5)
st = p.state()
print("після «Ще раз»:", st['phase'], st['len'], st['text'][:40])
print("місця:", seats_before, '→', p.ev("return [...document.querySelectorAll('.grbox .gseat')].map(e => e.textContent)"))
p.shot("rematch-ready.png")
p.wait_phase("go")
p.type_text(cpm=300, errors=0.02, seed=9)
for _ in range(200):
    rs = room_state(p)
    if rs["phase"] == "done": break
    time.sleep(0.5)
print("другий раунд:", json.dumps(rs["result"], ensure_ascii=False))
time.sleep(1.5)
p.shot("rematch-done.png")
for l in p.logs(): print('LOG', l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
