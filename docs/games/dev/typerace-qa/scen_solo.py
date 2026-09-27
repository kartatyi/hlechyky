"""Тренування: вибір довжини й текстів, «Поїхали», рекорд, «Ще раз», «Змінити», «Стоп», підсумок; телефон і Full HD."""
import json, sys, time
from tr import Page, room_state

W = int(sys.argv[1]) if len(sys.argv) > 1 else 1280
H = int(sys.argv[2]) if len(sys.argv) > 2 else 900
tag = sys.argv[3] if len(sys.argv) > 3 else "solo"
mobile = len(sys.argv) > 4 and sys.argv[4] == "mobile"
p = Page(9731, "Оля", W, H, mobile=mobile)
rid = p.open_solo()
p.dismiss()
time.sleep(0.5)
p.shot(f"{tag}-pick.png")
print("вибір:", p.ev("return [...document.querySelectorAll('.grbox .tr-chip')].map(b => b.textContent + (b.classList.contains('on') ? '*' : ''))"))
p.ev("document.querySelector('.grbox [data-len=short]').click(); document.querySelector('.grbox [data-src=twisters]').click(); return 1;")
time.sleep(0.3)
p.ev("document.querySelector('.grbox .tr-go').click(); return 1;")
time.sleep(1.0)
print("фаза:", p.state()["phase"], "текст:", p.state()["text"][:80])
p.shot(f"{tag}-ready.png")
p.wait_phase("go")
time.sleep(0.3)
print("фокус:", p.focused())
p.type_text(cpm=300, errors=0.02)
time.sleep(1.2)
rs = room_state(p)
print("підсумок:", json.dumps({k: rs[k] for k in ("phase", "result")}, ensure_ascii=False))
print("me:", p.ev("return __typerace.state().ctx.view.me"))
p.shot(f"{tag}-done.png")
print("статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
# «Ще раз» — Enter
p.key("Enter", "Enter", 13)
time.sleep(1.0)
print("після Enter:", p.state()["phase"])
p.wait_phase("go")
p.type_text(cpm=200, errors=0.05, seed=3, stop_at=0.5)
p.ev("document.querySelector('.grbox .tr-stop').click(); return 1;")
time.sleep(0.8)
print("після Стоп:", p.state()["phase"], p.ev("return __typerace.state().ctx.view.me"))
p.ev("document.querySelector('.grbox [data-len=long]').click(); document.querySelector('.grbox [data-src=classic]').click(); document.querySelector('.grbox .tr-go').click(); return 1;")
p.wait_phase("go")
p.type_text(cpm=320, errors=0.03, seed=5, stop_at=0.3)
p.shot(f"{tag}-go.png")
print("ширина:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth, document.scrollingElement.scrollHeight, innerHeight]"))
p.ev("document.querySelector('.grbox .tr-stop').click(); return 1;")
time.sleep(0.6)
for l in p.logs(): print("LOG", l)
