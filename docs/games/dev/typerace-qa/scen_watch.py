"""Глядач: стіл ботів (господар — бот), Оля лише дивиться: траса, лідер у статусі з кадрів, підсумок. Потім вихід бота посеред заїзду."""
import json, random, sys, time
from tr import Page, room_state
from trbots import run_bots, create_room, start_room
tag = str(random.randint(100, 999))
rid = create_room(8232, "Петро" + tag, {"length": "short", "source": "proverbs"})
print("стіл", rid)
th, bots = run_bots(8232, rid, ["Петро", "Ганна", "Іван", "Тарас"], cpm=[260, 220, 180, 150], leave_at=[None, None, 0.5, None], tag=tag)
time.sleep(2.5)
print(start_room(8232, "Петро" + tag, rid))
p = Page(9731, "Оля", 1280, 900)
p.goto(rid)
p.dismiss()
time.sleep(6)
s1 = p.ev("return document.querySelector('.grbox .gstatus').textContent")
p.shot("watch-go.png")
time.sleep(4)
s2 = p.ev("return document.querySelector('.grbox .gstatus').textContent")
print("статус:", s1, "→", s2)
print("стат-рядок:", p.ev("return document.querySelector('.grbox .tr-nums').textContent"))
for _ in range(200):
    rs = room_state(p)
    if rs and rs["phase"] == "done": break
    time.sleep(0.5)
time.sleep(1.8)
p.shot("watch-done.png")
print(json.dumps(rs["result"], ensure_ascii=False))
print("кнопки:", p.ev("return [...document.querySelectorAll('.grbox .gbtns button, .grbox .gbtns span')].map(b => b.textContent)"))
for l in p.logs(): print("LOG", l)
