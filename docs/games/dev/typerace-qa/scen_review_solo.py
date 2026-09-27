"""Після рецензій (27.09), тренування: вибір без самотньої 🔈; привид рекорду на сусідній доріжці; підсумок без
п'ятикратного рекорду (одне число, репліка Глека, «найкраще»; статус лише «Enter — ще раз», тосту нема).
    python scen_review_solo.py [ширина=1280] [висота=900] [мітка=rv-solo] [mobile]
"""
import json, sys, time
from tr import Page, room_state

W = int(sys.argv[1]) if len(sys.argv) > 1 else 1280
H = int(sys.argv[2]) if len(sys.argv) > 2 else 900
tag = sys.argv[3] if len(sys.argv) > 3 else "rv-solo"
mobile = len(sys.argv) > 4 and sys.argv[4] == "mobile"
p = Page(9731, "Оля", W, H, mobile=mobile)
rid = p.open_solo()
p.dismiss()
time.sleep(0.6)
p.shot(f"{tag}-pick.png")
print("рядок чисел у виборі видно:", p.ev("const s = document.querySelector('.grbox .tr-stats'); return s && getComputedStyle(s).display !== 'none'"))
p.ev("document.querySelector('.grbox [data-len=short]').click(); document.querySelector('.grbox [data-src=proverbs]').click(); return 1;")
time.sleep(0.2)
p.ev("document.querySelector('.grbox .tr-go').click(); return 1;")
p.wait_phase("go")
p.type_text(cpm=330, errors=0.01, seed=2)
time.sleep(1.5)
print("перший заїзд, me:", p.ev("return __typerace.state().ctx.view.me"))
p.shot(f"{tag}-done1.png")
toasts = p.ev("return [...document.querySelectorAll('.toast, .toasts > *')].map(t => t.textContent)")
print("тости:", toasts)
print("статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
print("підсумок:", p.ev("return document.querySelector('.grbox .tr-board').innerText"))

# другий заїзд — із привидом рекорду; їдемо повільніше, щоб привид був попереду
p.key("Enter", "Enter", 13)
time.sleep(1.2)
print("доріжок у відліку:", p.ev("return __typerace.state().lanes"), "привид:", p.ev("return __typerace.state().ghostBest"))
p.shot(f"{tag}-ready2.png")
p.wait_phase("go")
p.type_text(cpm=200, errors=0.02, seed=4, stop_at=0.5)
p.shot(f"{tag}-go2.png")
p.type_text(cpm=200, errors=0.02, seed=5)
time.sleep(1.5)
p.shot(f"{tag}-done2.png")
print("другий заїзд, me:", p.ev("return __typerace.state().ctx.view.me"))
print("статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
print("підсумок:", p.ev("return document.querySelector('.grbox .tr-board').innerText"))
print("перф:", p.ev("return __typerace.perf()"))
print("ширина:", p.ev("return [document.scrollingElement.scrollWidth, innerWidth]"))
for l in p.logs():
    print("LOG", l)
