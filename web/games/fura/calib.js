// Майстер калібрування керма: осі й кнопки в кожного керма (і навіть браузера) свої, тому не вгадуємо, а питаємо
// рухом: «відпусти все» → «вліво до упору» → «вправо» → педалі → кнопки → H-перемикач. Вісь шукаємо як ту, що
// змінилась найбільше від спокою; кнопку — як нову натиснуту. Злиті педалі (газ і гальмо на одній осі в різні боки)
// виходять самі: гальмо знайдеться на осі газу з протилежним знаком, а pedalOf рахує кожну лише у свій бік.

import { ACTIONS, H_GEARS, saveCalib, shortName, pads, isPad, forgetCalibCache } from './input.js';

const STABLE_MS = 600;

export function openCalib(host, gpId, onDone) {
  const el = document.createElement('div');
  el.className = 'fura-modal';
  el.innerHTML = '<div class="fura-box"><h3>🛞 Калібрування керма</h3><div class="fura-dev muted"></div>'
    + '<div class="fura-step"></div><div class="fura-meter"><i></i></div><div class="fura-extra"></div>'
    + '<div class="fura-row"><button type="button" class="skip">Пропустити</button>'
    + '<button type="button" class="again">Спочатку</button><button type="button" class="cancel">Скасувати</button></div></div>';
  host.appendChild(el);
  const $ = (s) => el.querySelector(s);
  const stepEl = $('.fura-step'), meter = $('.fura-meter i'), extra = $('.fura-extra');

  let id = gpId;
  const c = { v: 1, buttons: {}, gears: {}, sens: 1 };
  let rest = null, restBtn = new Set();
  let steps = [], si = 0, since = 0, cand = null, raf = 0, closed = false, waitRelease = false;

  const gp = () => pads().find((p) => p.id === id) || null;
  const used = () => new Set([c.steer, c.gas, c.brake, c.clutch].filter(Boolean).map((a) => a.i));
  const usedBtn = () => new Set([...Object.values(c.buttons), ...Object.values(c.gears)]);

  function axisMove(p, exclude) {
    let best = -1, d = 0;
    p.axes.forEach((v, i) => {
      if (exclude && exclude.has(i)) return;
      const x = Math.abs(v - rest[i]);
      if (x > d) { d = x; best = i; }
    });
    return { i: best, d };
  }
  const allNearRest = (p, eps) => p.axes.every((v, i) => Math.abs(v - rest[i]) < eps);
  const newButton = (p) => p.buttons.findIndex((b, i) => b && b.pressed && !restBtn.has(i) && !usedBtn().has(i));
  const anyButton = (p) => p.buttons.some((b, i) => b && b.pressed && !restBtn.has(i));

  function build() {
    steps = [
      { text: 'Відпусти кермо й педалі — нічого не тисни', kind: 'rest' },
      { text: 'Поверни кермо <b>до упору вліво</b> і тримай', kind: 'left' },
      { text: 'Тепер <b>до упору вправо</b> і тримай', kind: 'right' },
      { text: 'Відпусти кермо. Вижми <b>газ</b> до підлоги', kind: 'pedal', key: 'gas', skip: true },
      { text: 'Вижми <b>гальмо</b> до підлоги', kind: 'pedal', key: 'brake', skip: true },
      { text: 'Вижми <b>зчеплення</b> (нема — «Пропустити»)', kind: 'pedal', key: 'clutch', skip: true },
      ...ACTIONS.map(([key, label]) => ({ text: 'Натисни кнопку: <b>' + label + '</b>', kind: 'button', key, skip: true })),
      { text: 'Є H-перемикач? Увімкни <b>1-шу</b> передачу (нема — «Пропустити»)', kind: 'gear', key: '1', skip: true, skipAll: true },
      ...H_GEARS.slice(1).map((g) => ({ text: 'Увімкни <b>' + (g === 'R' ? 'задню (R)' : g + '-у') + '</b> передачу', kind: 'gear', key: g, skip: true })),
      { kind: 'final' },
    ];
  }

  function show() {
    const s = steps[si];
    const p = gp();
    $('.fura-dev').textContent = p ? shortName(p.id) : 'Натисни будь-яку кнопку на кермі — браузер покаже його лише після натиску';
    $('.skip').style.display = s && s.skip ? '' : 'none';
    meter.style.width = '0%';
    extra.innerHTML = '';
    if (!s) return;
    if (s.kind === 'final') return final();
    stepEl.innerHTML = s.text;
    since = 0; cand = null;
    waitRelease = s.kind !== 'rest' && s.kind !== 'left';
  }

  function next(skipped) {
    const s = steps[si];
    if (skipped && s.skipAll) { si = steps.findIndex((x) => x.kind === 'final'); return show(); }
    si++;
    show();
  }

  function final() {
    stepEl.innerHTML = '✅ Готово. Ось що вийшло:';
    $('.fura-meter').style.display = 'none';
    const row = (a, b) => '<tr><td>' + a + '</td><td>' + b + '</td></tr>';
    const btnName = (i) => (i == null ? '—' : 'кнопка ' + i);
    const ax = (a) => (a ? 'вісь ' + a.i : '—');
    extra.innerHTML = '<table>' + row('Кермо', ax(c.steer)) + row('Газ', ax(c.gas)) + row('Гальмо', ax(c.brake))
      + row('Зчеплення', ax(c.clutch))
      + ACTIONS.map(([k, l]) => row(l, btnName(c.buttons[k]))).join('')
      + row('H-перемикач', Object.keys(c.gears).length ? Object.keys(c.gears).join(' ') : '—') + '</table>'
      + '<label>Гострота керма <select class="sens">'
      + '<option value="1">Кермо до упору — колеса до упору</option>'
      + '<option value="1.5">Гостріше (×1,5)</option><option value="2">Ще гостріше (×2)</option></select></label>'
      + '<p class="muted small">Якщо в G HUB стоїть 900°, а крутити так далеко незручно — бери «гостріше».</p>'
      + '<div class="fura-row"><button type="button" class="main save">Зберегти й їхати</button></div>';
    extra.querySelector('.save').addEventListener('click', () => {
      c.sens = +extra.querySelector('.sens').value || 1;
      c.name = shortName(id);
      c.at = new Date().toISOString();
      saveCalib(id, c);
      forgetCalibCache(id);
      close(true);
    });
  }

  function tick(now) {
    if (closed) return;
    raf = requestAnimationFrame(tick);
    let p = gp();
    if (!id || !p) {
      const cands = pads().filter((x) => !isPad(x));
      if (cands.length) { id = cands[0].id; p = cands[0]; show(); }
      else return;
    }
    const s = steps[si];
    if (!s || s.kind === 'final') return;
    if (waitRelease) {
      if (rest && allNearRest(p, 0.15) && !anyButton(p)) waitRelease = false;
      else { stepEl.innerHTML = s.text + '<div class="muted small">спершу відпусти все</div>'; return; }
      stepEl.innerHTML = s.text;
    }
    if (s.kind === 'rest') {
      const snap = p.axes.slice();
      if (cand && snap.every((v, i) => Math.abs(v - cand[i]) < 0.02)) {
        if (!since) since = now;
        meter.style.width = Math.min(100, (now - since) / 12) + '%';
        if (now - since > 1200) {
          rest = snap;
          restBtn = new Set(p.buttons.map((b, i) => (b && b.pressed ? i : -1)).filter((i) => i >= 0));
          next();
        }
      } else { cand = snap; since = 0; }
      return;
    }
    if (s.kind === 'left' || s.kind === 'right') {
      let i, d;
      if (s.kind === 'left') ({ i, d } = axisMove(p));
      else { i = c.steer.i; d = Math.abs(p.axes[i] - rest[i]); }
      const v = p.axes[i];
      const ok = d >= 0.5 && (s.kind === 'left' || Math.sign(v - c.steer.c) !== Math.sign(c.steer.l - c.steer.c));
      meter.style.width = Math.min(100, d * 100) + '%';
      if (ok && cand && cand.i === i && Math.abs(v - cand.v) < 0.03) {
        if (!since) since = now;
        if (now - since > STABLE_MS) {
          if (s.kind === 'left') c.steer = { i, l: v, c: rest[i], r: rest[i] - (v - rest[i]) };
          else c.steer.r = v;
          next();
        }
      } else { cand = ok ? { i, v } : null; since = 0; }
      return;
    }
    if (s.kind === 'pedal') {
      const ex = new Set([c.steer && c.steer.i]);
      const { i, d } = axisMove(p, ex);
      const v = p.axes[i];
      meter.style.width = Math.min(100, d * 100) + '%';
      // Та сама вісь, що вже має інша педаль, годиться лише в протилежний бік (злиті педалі)
      const clash = Object.entries({ gas: c.gas, brake: c.brake, clutch: c.clutch })
        .some(([k, a]) => a && k !== s.key && a.i === i && Math.sign(v - a.rest) === Math.sign(a.full - a.rest));
      const ok = d >= 0.4 && !clash;
      if (ok && cand && cand.i === i && Math.abs(v - cand.v) < 0.03) {
        if (!since) since = now;
        if (now - since > STABLE_MS) { c[s.key] = { i, rest: rest[i], full: v }; next(); }
      } else { cand = ok ? { i, v } : null; since = 0; }
      return;
    }
    if (s.kind === 'button' || s.kind === 'gear') {
      const b = newButton(p);
      if (b >= 0) {
        if (s.kind === 'button') c.buttons[s.key] = b; else c.gears[s.key] = b;
        meter.style.width = '100%';
        next();
      }
    }
  }

  function close(saved) {
    if (closed) return;
    closed = true;
    cancelAnimationFrame(raf);
    el.remove();
    onDone && onDone(saved ? id : null);
  }

  $('.skip').addEventListener('click', () => next(true));
  $('.again').addEventListener('click', () => {
    Object.assign(c, { buttons: {}, gears: {}, steer: undefined, gas: undefined, brake: undefined, clutch: undefined });
    rest = null; si = 0; $('.fura-meter').style.display = ''; show();
  });
  $('.cancel').addEventListener('click', () => close(false));

  build();
  show();
  raf = requestAnimationFrame(tick);
  return { close: () => close(false), get open() { return !closed; } };
}
