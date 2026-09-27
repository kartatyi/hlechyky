"""Клієнтські шляхи фінішу, що їх знайшла рецензія (27.09):
  1) F5, поки висить червона літера: червоне після перезавантаження висить і далі, фініш зараховано (раніше — 🤖 mismatch);
  2) фініш, коли виклик до сервера падає двічі (кліпнув зв'язок): клієнт повторює, доки сервер не побачить;
  3) F5 на останній літері, коли фініш так і не дійшов: після перезавантаження фініш іде сам (раніше — «застряг на 100 %»);
  (зв'язок, що ліг зовсім, — scen_netdrop.py).
Один Chrome (9731) + бот-суперник. Запуск: python scen_finish_paths.py
"""
import json, time
from tr import Page, run_bots, room_state

p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "proverbs"})
th, bots = run_bots(8232, rid, ["Петро"], cpm=[170], rounds=3)
time.sleep(3)
p.dismiss()


def me_row():
    rs = room_state(p)
    return rs, [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]


def wait_done(limit=150):
    for _ in range(limit * 2):
        rs = room_state(p)
        if rs and rs["phase"] == "done":
            return rs
        time.sleep(0.5)
    raise RuntimeError("заїзд не скінчився")


def rematch():
    time.sleep(1.2)
    print("  «Ще раз»:", p.ev("const b = [...document.querySelectorAll('.grbox .gbtns button')].find(x => /Ще раз/.test(x.textContent)); if (!b) return 'нема кнопки'; b.click(); return 'ok';"))
    time.sleep(1.5)


BLOCK = """
window.__blockFinish = %d;
if (!window.__origInvoke) {
  window.__origInvoke = signalR.HubConnection.prototype.invoke;
  signalR.HubConnection.prototype.invoke = function (m, ...a) {
    if (m === 'Act' && a[1] === 'finish' && window.__blockFinish !== 0) {
      if (window.__blockFinish > 0) window.__blockFinish--;
      window.__blocked = (window.__blocked || 0) + 1;
      return Promise.reject(new Error('Invocation canceled due to the underlying connection being closed.'));
    }
    return window.__origInvoke.call(this, m, ...a);
  };
}
return window.__blockFinish;"""

# ---------------------------------------------------------------- 1) F5 у червоному
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
p.type_text(cpm=280, errors=0.0, stop_at=0.4)
s = p.state()
wrong = 'ж' if s["text"][s["c"]] != 'ж' else 'ш'
p.insert(wrong)
time.sleep(0.3)
before = p.state()
print("1) до F5:", {k: before[k] for k in ("c", "red", "events")})
p.tab.call("Page.reload")
time.sleep(4)
p.dismiss()
after = p.state()
print("   після F5:", {k: after[k] for k in ("c", "red", "events", "phase")})
print("   на екрані червоне:", p.ev("return !!document.querySelector('.grbox .tr-word b.bad')"))
p.shot("fix-f5red-after.png")
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
time.sleep(0.2)
p.backspace()
time.sleep(0.2)
p.type_text(cpm=280, errors=0.0, seed=4)
time.sleep(2.5)
rs, me = me_row()
print("   фініш після F5 у червоному:", {k: me[k] for k in ("fin", "place", "wrong", "flag")})
wait_done()
rematch()

# ---------------------------------------------------------------- 2) виклик фінішу падає двічі
p.wait_phase("go")
print("2) блок двох фінішів:", p.ev(BLOCK % 2))
p.type_text(cpm=300, errors=0.02, seed=6)
time.sleep(0.5)
print("   статус одразу:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
time.sleep(4.5)
rs, me = me_row()
print("   заблоковано разів:", p.ev("return window.__blocked"), "фініш:", {k: me[k] for k in ("fin", "place", "flag")})
wait_done()
rematch()

# ---------------------------------------------------------------- 3) фініш не дійшов, F5 на 100 %
p.wait_phase("go")
print("3) блок усіх фінішів:", p.ev(BLOCK % -1))
p.type_text(cpm=300, errors=0.02, seed=8)
time.sleep(1.5)
st = p.state()
rs, me = me_row()
print("   локально фініш:", st["finished"], "c =", st["c"], "/", st["len"], "сервер бачить fin =", me["fin"])
p.tab.call("Page.reload")
time.sleep(4)
p.dismiss()
time.sleep(1.5)
rs, me = me_row()
print("   після F5:", {k: me[k] for k in ("fin", "place", "flag")}, "статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
p.shot("fix-f5-finish-after.png")
wait_done()

for l in p.logs():
    print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
