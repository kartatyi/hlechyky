"""Після рецензій (27.09), стіл на трьох, 1280×900:
  * переможець-утікач: Петро доїхав першим і встав — у підсумку в нього 🚪, а 🥇 — у того, кого назвав Глек;
  * F5 без збереженого заїзду (як у приватному вікні: sessionStorage стерто) — заново з нуля, старт від годинника
    сервера, фініш зараховано (раніше — 🤖 «годинник не сходиться»);
  * гудки з фінішу (клавіші 1 і 4) — кружечок з емодзі біля трактора; знімки з наближенням.
"""
import json, time
from tr import Page, run_bots, room_state

p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "proverbs"})
th, bots = run_bots(8232, rid, ["Петро", "Ганна"], cpm=[520, 110], leave_after=[True, False])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
p.type_text(cpm=300, errors=0.02, stop_at=0.4)
print("до F5:", {k: p.state()[k] for k in ("c", "events")})
# як приватне вікно: після перезавантаження сховище вкладки не віддає нічого з заїзду
hide = p.tab.call("Page.addScriptToEvaluateOnNewDocument", source="""(() => { const g = Storage.prototype.getItem;
  Storage.prototype.getItem = function (k) { return String(k).startsWith('typerace:') ? null : g.call(this, k); }; })();""")
p.tab.call("Page.reload")
time.sleep(4)
p.tab.call("Page.removeScriptToEvaluateOnNewDocument", identifier=hide["identifier"])
p.dismiss()
st = p.state()
print("після F5 без збереженого:", {k: st[k] for k in ("c", "events", "phase")},
      "локальний старт раніше за «зараз» на", p.ev("const s = __typerace.state(); return Math.round((Date.now() - s.localGo) / 1000) + ' с'"))
p.ev("document.querySelector('.grbox .tr-textbox').click(); return 1;")
time.sleep(0.2)
p.type_text(cpm=320, errors=0.02, seed=3)
time.sleep(1.2)
rs = room_state(p)
me = [r for r in rs["racers"] if r["nick"].endswith("Оля")][0]
print("фініш після F5 з нуля:", {k: me[k] for k in ("fin", "place", "flag")})
p.ev("document.activeElement && document.activeElement.blur(); return 1;")
p.key("1", "Digit1", 49)
time.sleep(0.32)
p.shot("rv-cheer-1.png")
time.sleep(0.9)
p.ev("document.querySelector('.grbox .tr-cheer[data-cheer=\"3\"]').click(); return 1;")
time.sleep(0.5)
p.shot("rv-cheer-2.png")
for _ in range(400):
    rs = room_state(p)
    if rs["phase"] == "done":
        break
    time.sleep(0.5)
time.sleep(1.8)
p.shot("rv-gone-winner-done.png")
print("result:", json.dumps(rs["result"], ensure_ascii=False))
print("медалі:", p.ev("return [...document.querySelectorAll('.grbox .tr-table tbody tr')].map(r => r.querySelector('.tr-medal').textContent + ' ' + r.querySelector('.tr-bnick').textContent)"))
print("статус:", p.ev("return document.querySelector('.grbox .gstatus').textContent"))
for l in p.logs():
    print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
