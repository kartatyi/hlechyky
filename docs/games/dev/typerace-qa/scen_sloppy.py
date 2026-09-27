"""Після рецензій (27.09): неохайний друкар на довгому тексті в тренуванні — помилка кожні ~8 знаків і по 12 натисків,
поки помітив червоне (усе справжніми натисками CDP). Раніше журнал упирався в стелю 1600 подій, і суддя казав 🤖
«журнал не сходиться»; тепер клієнт пише не більше 4 проковтнутих на червону літеру — журнал нижче стелі, заїзд
зараховано. Наприкінці — чи бере журнал час натиску з події, а не з миті обробки: сторінку «підвішуємо» на 400 мс,
поки CDP шле дві літери з проміжком ~120 мс, — у журналі між ними має бути ~120 мс, а не 0–2.
"""
import random, time
from tr import Page, room_state

p = Page(9731, "Оля", 1280, 900)
rid = p.open_solo()
p.dismiss()
# з будь-якої фази тренування — одразу в довгий заїзд класикою
p.ev("const s = __typerace.state(); if (s.phase === 'ready' || s.phase === 'go') await s.ctx.act('stop', {}); "
     "await new Promise(r => setTimeout(r, 500)); return (await s.ctx.act('go', { length: 'long', source: 'classic' })).ok;")
p.wait_phase("go")
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
s = p.state()
text = s["text"]
rng = random.Random(3)
raw = 0
errors = 0
i = 0
while i < len(text):
    ch = text[i]
    if ch == '\n':
        ch = ' '
    if i > 0 and i % 8 == 0 and ch != ' ':
        p.insert('ж' if ch != 'ж' else 'ш')              # помилка
        raw += 1
        errors += 1
        time.sleep(0.05 + rng.random() * 0.06)
        for _ in range(12):                              # не помітив — гатить далі
            p.insert(rng.choice('абвгд'))
            raw += 1
            time.sleep(0.05 + rng.random() * 0.07)
        p.backspace()
        raw += 1
        time.sleep(0.08 + rng.random() * 0.08)
    p.insert(ch)
    raw += 1
    i += 1
    time.sleep(0.045 + rng.random() * 0.07)
time.sleep(1.5)
st = p.state()
rs = room_state(p)
me = rs["racers"][0]
print("текст", len(text), "знаків; натисків", raw, "(помилок", errors, "); у журналі подій", st["events"])
print("фініш:", {k: me[k] for k in ("fin", "cpm", "acc", "wrong", "flag")})
p.shot("rv-sloppy-done.png")

# ---- час натиску з події
p.key("Enter", "Enter", 13)
p.wait_phase("go")
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
time.sleep(0.3)
s = p.state()
p.insert(s["text"][0])
time.sleep(0.3)
k0 = p.state()["events"]
# сторінка «зависла» на 400 мс; тим часом дві клавіші з проміжком ~120 мс (CDP чекає, доки сторінка прийме натиск,
# тож друга піде вже після «відвисання»; важить перша: оброблена на ~380 мс пізніше, ніж натиснута)
p.ev("window.__late = []; document.addEventListener('keydown', (e) => __late.push([e.key, Math.round(Date.now() - (performance.timeOrigin + e.timeStamp))]), true); "
     "setTimeout(() => { const t = performance.now(); while (performance.now() - t < 400) {} }, 0); return 1;")
time.sleep(0.02)
c1, c2 = s["text"][1], s["text"][2]
for ch in (c1, c2):
    code = 'Key' + ch.upper() if ch.isascii() and ch.isalpha() else ''
    p.tab.call("Input.dispatchKeyEvent", type="keyDown", key=ch, text=ch, unmodifiedText=ch)
    p.tab.call("Input.dispatchKeyEvent", type="keyUp", key=ch)
    time.sleep(0.12)
time.sleep(0.6)
d = p.ev("const s = __typerace.state(); const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'; "
         "const out = []; for (let i = 0; i < s.k.length; i++) out.push([s.k[i], 4 * (A.indexOf(s.d[2*i]) * 64 + A.indexOf(s.d[2*i+1]))]); return out;")
print("журнал другого заїзду (подія, мс від попередньої):", d)
print("клавіша: на скільки мс обробник запізнився від натиску:", p.ev("return __late"))
p.ev("document.querySelector('.grbox .tr-stop').click(); return 1;")
for l in p.logs():
    print("LOG", l)
