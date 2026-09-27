"""Поле не у фокусі — перша ж клавіша ставить фокус і сама лягає в поле (onKey каркаса)."""
import time
from tr import Page, run_bots
p = Page(9731, "Оля", 1280, 900)
rid = p.create("typerace", {"length": "short", "source": "classic"})
th, bots = run_bots(8232, rid, ["Петро"], cpm=[120])
time.sleep(3)
p.dismiss()
p.ev("const r = await HGames.call('StartRoom', '" + rid + "'); return r.ok;")
p.wait_phase("go")
time.sleep(0.3)
p.ev("document.activeElement.blur(); return 1;")
print("фокус до:", p.focused())
s = p.state()
ch = s["text"][0]
p.tab.call("Input.dispatchKeyEvent", type="keyDown", key=ch, text=ch, unmodifiedText=ch)
p.tab.call("Input.dispatchKeyEvent", type="keyUp", key=ch)
time.sleep(0.3)
s2 = p.state()
print("фокус після:", p.focused(), "c:", s["c"], "→", s2["c"], "журнал:", p.ev("return __typerace.state().k"))
# Backspace без фокуса теж
p.ev("document.activeElement.blur(); return 1;")
p.key("Backspace", "Backspace", 8)
time.sleep(0.3)
print("після Backspace без фокуса: c =", p.state()["c"], "журнал:", p.ev("return __typerace.state().k"))
for l in p.logs(): print("LOG", l)
p.ev("const r = await HGames.call('LeaveRoom', '" + rid + "'); return r.ok;")
