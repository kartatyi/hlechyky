// Гучності фури: загальна, двигун, ефекти, Глек у рації — свої шини WebAudio в audio.js; радіо — повзунок плеєра
// сайту (#volume): крутимо його ж і кажемо йому «input», тож app.js сам рахує дБ, притишення й пам'ятає гучність.
// Наші рівні — localStorage['fura.vol'] = {master, engine, fx, voice}, 0…100; 80 — як було до мікшера.

const KEY = 'fura.vol';
export const CHANNELS = [
  { k: 'master', name: '🔊 Загальна' },
  { k: 'engine', name: '🚚 Двигун і дорога' },
  { k: 'fx', name: '💨 Ефекти: пневматика, сигнал, вантаж' },
  { k: 'voice', name: '📻 Дядько Глек у рації' },
];
const DEF = 80;
const STEP = 5;

export function loadVol() {
  const v = { master: DEF, engine: DEF, fx: DEF, voice: DEF };
  try {
    const s = JSON.parse(localStorage.getItem(KEY) || '{}');
    for (const c of CHANNELS) if (Number.isFinite(s[c.k])) v[c.k] = clamp(s[c.k]);
  } catch { /* битий запис — типові */ }
  return v;
}

function saveVol(v) { try { localStorage.setItem(KEY, JSON.stringify(v)); } catch { /* приватне вікно */ } }

const clamp = (p) => Math.max(0, Math.min(100, Math.round(p)));

/** Повзунок → множник гучності: 80 — 1 (як було), 100 — ×1,56, 40 — ×0,25 (квадрат — ближче до слуху, ніж лінія). */
export const gainOf = (p) => (p / DEF) ** 2;

/** Вікно «🎚 Гучність». boxModal — з main.js; sound — наш Sound. */
export function openMixer({ boxModal, sound }) {
  const radio = document.getElementById('volume');
  const row = (k, name, val) => '<div class="fura-mix" data-k="' + k + '"><span class="fura-mix-n">' + name + '</span>'
    + '<button type="button" data-d="-' + STEP + '" title="Тихіше">−</button>'
    + '<input type="range" min="0" max="100" step="1" value="' + val + '" aria-label="' + name + '">'
    + '<button type="button" data-d="' + STEP + '" title="Гучніше">+</button><b>' + val + '</b></div>';
  const v = sound.vol;
  let html = '<h3>🎚 Гучність</h3>';
  for (const c of CHANNELS) html += row(c.k, c.name, v[c.k]);
  if (radio) html += row('radio', '📻 Радіо Глечиків (плеєр сайту)', +radio.value);
  html += '<p class="muted small">' + (radio ? 'Радіо — той самий повзунок, що в шапці сайту: міняється й там.'
    : 'Плеєра радіо на цій сторінці нема — його гучність тут не крутиться.') + ' Клавіша M — 🔇 весь звук гри.</p>'
    + '<div class="fura-row"><button type="button" class="main" data-x="ok">Готово</button>'
    + '<button type="button" data-x="mute">' + (sound.muted ? '🔊 Увімкнути звук' : '🔇 Вимкнути звук') + '</button>'
    + '<button type="button" data-x="reset">↺ Як було</button></div>';
  return boxModal(html, (el, m) => {
    const set = (k, p) => {
      p = clamp(p);
      const r = el.querySelector('.fura-mix[data-k="' + k + '"]');
      r.querySelector('input').value = p; r.querySelector('b').textContent = p;
      if (k === 'radio') { radio.value = p; radio.dispatchEvent(new Event('input')); return; }
      sound.setVol(k, p);
      saveVol(sound.vol);
    };
    el.addEventListener('input', (e) => {
      const r = e.target.closest('.fura-mix');
      if (r) set(r.dataset.k, +e.target.value);
    });
    el.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      const r = b.closest('.fura-mix');
      if (r) { set(r.dataset.k, +r.querySelector('input').value + +b.dataset.d); return; }
      if (b.dataset.x === 'ok') m.close();
      else if (b.dataset.x === 'reset') for (const c of CHANNELS) set(c.k, DEF);
      else if (b.dataset.x === 'mute') {
        sound.setMuted(!sound.muted);
        b.textContent = sound.muted ? '🔊 Увімкнути звук' : '🔇 Вимкнути звук';
        const t = document.querySelector('.fura-tools [data-t=mute]');
        if (t) t.textContent = sound.muted ? '🔇' : '🔊';
      }
    });
  });
}
