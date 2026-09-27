"""Зв'язок ліг перед останньою літерою (рецензія 27.09: «повтор фінішу не спрацьовує ніколи»).

Сокет SignalR закриваємо самі, а /hub блокуємо через CDP (Network.setBlockedURLs) на кілька секунд — так
перепідключення справді не вдається. Остання літера лягає, поки зв'язку нема: каркас відповідає { ok: false }, клієнт
повторює; коли зв'язок вертається, фініш доходить (сервер час рахує від миті, коли дійшло). Один Chrome (9731) + бот.
"""
import time
from tr import Page, run_bots, room_state

OFF_S = float(__import__('sys').argv[1]) if len(__import__('sys').argv) > 1 else 4.0
p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "proverbs"})
th, bots = run_bots(8232, rid, ["Петро"], cpm=[150])
time.sleep(3)
p.dismiss()
# запам'ятати сокет, яким іде SignalR
p.ev("""if (!window.__wsHooked) { window.__wsHooked = true; const s = WebSocket.prototype.send;
  WebSocket.prototype.send = function (...a) { window.__ws = this; return s.apply(this, a); }; } return 1;""")
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
s = p.state()
p.type_text(cpm=300, errors=0.02, seed=10, stop_at=(len(s["text"]) - 1) / len(s["text"]))
p.tab.call("Network.enable")
p.tab.call("Network.setBlockedURLs", urls=["*/hub*"])
print("сокет закрито:", p.ev("if (!window.__ws) return 'нема сокета'; window.__ws.close(); return 'ok';"))
time.sleep(0.6)
s = p.state()
t0 = time.time()
p.insert(s["text"][s["c"]] if s["text"][s["c"]] != "\n" else " ")
time.sleep(1.2)
rs = room_state(p)
me = [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]
print("без зв'язку: локально фініш:", p.state()["finished"], "| сервер fin =", me["fin"],
      "| статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
p.shot("fix-netdrop-finish.png")
time.sleep(max(0, OFF_S - 1.2))
p.tab.call("Network.setBlockedURLs", urls=[])
print("зв'язок повернуто через", round(time.time() - t0, 1), "с")
got = None
for i in range(60):
    time.sleep(0.5)
    try:
        rs = room_state(p)
    except Exception as e:  # noqa: BLE001
        continue
    me = [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]
    if me["fin"] is not None:
        got = me
        break
print("фініш дійшов через", round(time.time() - t0, 1), "с після останньої літери:",
      {k: got[k] for k in ("fin", "place", "flag")} if got else None,
      "| спроб:", p.ev("return __typerace.state().finTries"))
print("статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
for _ in range(200):
    rs = room_state(p)
    if rs["phase"] == "done":
        break
    time.sleep(0.5)
print("підсумок:", [(r["nick"], r["place"], r["flag"]) for r in rs["racers"]])
for l in p.logs():
    print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
