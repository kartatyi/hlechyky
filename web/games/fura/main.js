// Полігон «Чумаків-далекобійників»: збирає докупи сцену (three.js), фізику (Rapier), фуру, керування, звук і HUD.
// Фізика — рівним кроком 60 Гц, малювання — з кожним кадром із проміжною позою між двома кроками (на 120/144 Гц
// моніторах фура не тремтить). Вхід опитуємо раз на кадр; «щойно натиснуте» (зчепити, камера…) — теж раз.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import * as RAPIER from '/static/vendor/rapier-0.21.0/rapier.js';
import { buildWorld, SPAWN as POLY_SPAWN } from './world.js';
import { buildKray } from './kray.js';
import { Truck, TRACTOR, TRAILER } from './truck.js';
import { buildModels } from './model.js';
import { Input, pads, isPad, looksLikeWheel, loadCalib, shortName } from './input.js';
import { openCalib } from './calib.js';
import { Sound } from './audio.js';
import { Nav } from './nav.js';
import { Trips } from './trip.js';
import { Voice } from './voice.js';

const STEP = 1 / 60;
const CAMS = ['chase', 'cabin', 'far'];
const CAM_NAME = { chase: 'за фурою', cabin: 'з кабіни', far: 'згори' };

function isDeck() {
  try {
    if (/[?&]deck=1/.test(location.search) || /SteamOS|Steam Deck/i.test(navigator.userAgent)) return true;
    if (pads().some((p) => /28de/i.test(p.id))) return true;
    return /Linux/.test(navigator.platform) && screen.width === 1280 && screen.height === 800;
  } catch { return false; }
}
const store = {
  get(k, d) { try { const v = localStorage.getItem('fura.' + k); return v == null ? d : v; } catch { return d; } },
  set(k, v) { try { localStorage.setItem('fura.' + k, v); } catch { /* пусто */ } },
};

/** mode: 'kray' — «Чумацький край» (мапа з сервера), 'polygon' — полігон етапу 0 для тренування. */
/** server — міст до столу гри (fura.js): { tell(action, payload), input(action, payload), view(), onView } */
export async function start({ onExit, mode = 'kray', server = null } = {}) {
  const app = document.createElement('div');
  app.className = 'fura-app';
  app.innerHTML = '<div class="fura-loading">' + (mode === 'kray' ? '🗺 Будуємо Чумацький край…' : '🚚 Заводимо фуру…') + '</div>';
  document.body.appendChild(app);
  try { if (app.requestFullscreen && !document.fullscreenElement) app.requestFullscreen().catch(() => {}); } catch { /* пусто */ }

  await RAPIER.init();
  const low = isDeck() || (navigator.hardwareConcurrency || 8) <= 4;
  const quality = low ? 'low' : 'high';

  // ---- рендер
  const renderer = new THREE.WebGLRenderer({ antialias: !low, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, low ? 1 : 1.5));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.domElement.className = 'fura-3d';
  renderer.domElement.tabIndex = 0;
  app.innerHTML = '';
  app.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  const sky = new THREE.Color('#a9cfe8');
  scene.background = sky;
  scene.fog = new THREE.Fog(sky, 140, 560);
  scene.add(new THREE.HemisphereLight('#e4f1ff', '#6f7d4d', 1.1));
  const sun = new THREE.DirectionalLight('#fff1d6', 2.3);
  sun.castShadow = true;
  sun.shadow.mapSize.set(low ? 1024 : 2048, low ? 1024 : 2048);
  const sc = sun.shadow.camera;
  sc.left = -40; sc.right = 40; sc.top = 40; sc.bottom = -40; sc.near = 1; sc.far = 220;
  sun.shadow.bias = -0.0006;
  scene.add(sun, sun.target);

  const camera = new THREE.PerspectiveCamera(62, 1, 0.08, 900);

  // ---- фізика
  const world = new RAPIER.World({ x: 0, y: -9.81, z: 0 });
  world.timestep = STEP;
  try { world.integrationParameters.numSolverIterations = 8; } catch { /* стара назва — лишаємо типове */ }
  let level, SPAWN;
  if (mode === 'kray') {
    const res = await fetch('/api/games/fura/map/kray');
    if (!res.ok) throw new Error('мапа не приїхала (' + res.status + ')');
    const map = await res.json();
    await new Promise((r) => setTimeout(r, 30));            // дати браузеру домалювати «Будуємо…»
    level = buildKray(THREE, RAPIER, scene, world, quality, map);
    // поки рейсів нема (крок 1d) — тягач у депо, порожній причіп у боксі 1
    const s = level.spawn, dock = level.places.find((p) => p.depot).docks[0];
    SPAWN = { tractor: s, trailer: { x: dock.x, z: dock.z, yaw: dock.yaw, y: level.height(dock.x, dock.z) } };
    scene.fog.near = 240; scene.fog.far = 760; camera.far = 1100;
  } else {
    level = buildWorld(THREE, RAPIER, scene, world, quality);
    SPAWN = POLY_SPAWN;
  }
  const truck = new Truck(THREE, RAPIER, world, scene, { spawn: SPAWN });
  truck.ground = level.height || (() => 0);
  let trips = null, nav = null, voice = null;   // рейси й Глек у рації — лише в краю й лише зі столом (з картки гри)
  const model = buildModels(THREE, scene);
  // Промені коліс бачать лише нерухоме: ні свій тягач/причіп, ні конуси під колесами (конус штовхаємо корпусом)
  const onlyFixed = (col) => { const p = col.parent(); return !p || p.isFixed(); };

  // ---- HUD
  const hud = document.createElement('div');
  hud.className = 'fura-hud';
  hud.innerHTML = '<div class="fura-panel fura-dev-line"></div>'
    + '<div class="fura-panel fura-hint"></div>'
    + '<div class="fura-panel fura-speedo"><div class="fura-speed">0<small>км/год</small></div>'
    + '<div class="fura-gearline"><span class="fura-gear">D1</span><span class="fura-rpm"><i></i></span></div>'
    + '<div class="fura-pedals"><span class="p-c"><i></i></span><span class="p-b"><i></i></span><span class="p-g"><i></i></span>'
    + '<span class="fura-steerbar"><i></i></span></div></div>'
    + '<div class="fura-panel fura-cargo"><div class="muted">🏺 вантаж ціле</div><div class="fura-pct">100 %</div><div class="fura-link"></div></div>'
    + '<div class="fura-tools"><button type="button" data-t="cam" title="Камера (C)">📷</button>'
    + (server ? '<button type="button" data-t="radio" title="Радіо Глечиків у кабіні">📻</button>' : '')
    + '<button type="button" data-t="mute" title="Звук (M)">🔊</button><button type="button" data-t="fs" title="Повний екран (F)">⛶</button>'
    + '<button type="button" data-t="menu" title="Меню (Esc)">☰</button><button type="button" data-t="exit" title="Вийти">✕</button></div>'
    + '<div class="fura-flash"></div>';
  app.appendChild(hud);
  const $ = (s) => hud.querySelector(s);
  const ui = {
    dev: $('.fura-dev-line'), hint: $('.fura-hint'), speed: $('.fura-speed'), gear: $('.fura-gear'), rpm: $('.fura-rpm i'),
    pg: $('.p-g i'), pb: $('.p-b i'), pc: $('.p-c i'), steer: $('.fura-steerbar i'), pct: $('.fura-pct'), link: $('.fura-link'),
    flash: $('.fura-flash'), mute: hud.querySelector('[data-t=mute]'),
  };

  const input = new Input();
  const sound = new Sound();
  sound.start();
  ui.mute.textContent = sound.muted ? '🔇' : '🔊';

  let cam = store.get('cam', 'chase');
  if (!CAMS.includes(cam)) cam = 'chase';
  let modal = null;          // відкрите вікно: { close, open }
  let toast = '', toastUntil = 0;
  let stopped = false, raf = 0, last = performance.now(), acc = 0, fps = 60;
  let lookYaw = 0, lookPitch = 0, dragging = null;
  let calibAsked = new Set();
  const say = (t, ms = 2200) => { toast = t; toastUntil = performance.now() + ms; };

  // ---- інтерполяція поз
  const snap = (b) => { const t = b.translation(), r = b.rotation(); return { p: new THREE.Vector3(t.x, t.y, t.z), q: new THREE.Quaternion(r.x, r.y, r.z, r.w) }; };
  let prevT = snap(truck.tb), curT = prevT, prevR = snap(truck.rb), curR = prevR;
  const pose = { tp: new THREE.Vector3(), tq: new THREE.Quaternion(), rp: new THREE.Vector3(), rq: new THREE.Quaternion() };

  // ---- камера за фурою
  const chase = { pos: new THREE.Vector3(0, 6, 30), yaw: 0, look: new THREE.Vector3() };
  function placeCamera(dt) {
    if (api.freeCam) {                     // огляд з консолі: __fura.freeCam = {pos:[x,y,z], look:[x,y,z]}
      camera.position.set(...api.freeCam.pos); camera.lookAt(...api.freeCam.look); camera.fov = api.freeCam.fov || 60;
      camera.updateProjectionMatrix(); return;
    }
    const fwd = new THREE.Vector3(0, 0, 1).applyQuaternion(pose.tq);
    const yaw = Math.atan2(fwd.x, fwd.z);
    if (cam === 'cabin') {
      const eye = new THREE.Vector3(0.6, 1.66, 1.95).applyQuaternion(pose.tq).add(pose.tp);
      camera.position.copy(eye);
      // погляд трохи вниз: щоб у кадрі були панель, кермо й глечик
      const q = pose.tq.clone().multiply(new THREE.Quaternion().setFromEuler(new THREE.Euler(lookPitch - 0.16, Math.PI + lookYaw, 0, 'YXZ')));
      camera.quaternion.copy(q);
      camera.fov = 70;
    } else {
      // ззаду фури, на курсі, що повільно доганяє тягач (на розворотах камера не крутиться разом із кабіною)
      const d = Math.atan2(Math.sin(yaw - chase.yaw), Math.cos(yaw - chase.yaw));
      chase.yaw += d * Math.min(1, dt * (cam === 'far' ? 1.5 : 2.6));
      // з причепом відходимо далі й вище, інакше камера дивиться в його дах
      const long = truck.coupled || pose.rp.distanceTo(pose.tp) < 24;
      const dist = cam === 'far' ? 34 : long ? 25 : 14, h = cam === 'far' ? 26 : long ? 8 : 5.5;
      const yawC = chase.yaw + (cam === 'chase' ? -lookYaw : 0);
      const want = new THREE.Vector3(pose.tp.x - Math.sin(yawC) * dist, pose.tp.y + h, pose.tp.z - Math.cos(yawC) * dist);
      chase.pos.lerp(want, Math.min(1, dt * 5));
      if (chase.pos.y < 1.2) chase.pos.y = 1.2;
      camera.position.copy(chase.pos);
      chase.look.set(pose.tp.x + Math.sin(chase.yaw) * 5, pose.tp.y + 1.8, pose.tp.z + Math.cos(chase.yaw) * 5);
      camera.lookAt(chase.look);
      camera.fov = 62;
    }
    camera.updateProjectionMatrix();
    model.setCabin(cam === 'cabin');
    // тінь їде за фурою
    sun.position.set(pose.tp.x + 40, pose.tp.y + 80, pose.tp.z + 25);
    sun.target.position.copy(pose.tp);
  }

  function resize() {
    const w = app.clientWidth || window.innerWidth, h = app.clientHeight || window.innerHeight;
    renderer.setSize(w, h, false);
    camera.aspect = w / Math.max(1, h);
    camera.updateProjectionMatrix();
  }
  window.addEventListener('resize', resize);
  document.addEventListener('fullscreenchange', resize);
  resize();

  // ---- огляд мишею (кабіна) — тягни по екрану; подвійний клік — прямо
  const cv = renderer.domElement;
  cv.addEventListener('pointerdown', (e) => { dragging = { x: e.clientX, y: e.clientY }; cv.setPointerCapture(e.pointerId); });
  cv.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    lookYaw -= (e.clientX - dragging.x) * 0.005;
    lookPitch = Math.max(-0.6, Math.min(0.5, lookPitch - (e.clientY - dragging.y) * 0.004));
    dragging = { x: e.clientX, y: e.clientY };
  });
  cv.addEventListener('pointerup', () => { dragging = null; });
  cv.addEventListener('dblclick', () => { lookYaw = 0; lookPitch = 0; });

  // ---- кнопки HUD
  hud.addEventListener('click', (e) => {
    const t = e.target.closest('[data-t]');
    if (!t) return;
    sound.start();
    const k = t.dataset.t;
    if (k === 'cam') nextCam();
    else if (k === 'mute') toggleMute();
    else if (k === 'radio') toggleRadio();
    else if (k === 'fs') toggleFs();
    else if (k === 'menu') openMenu();
    else if (k === 'exit') exit();
  });

  function nextCam() {
    cam = CAMS[(CAMS.indexOf(cam) + 1) % CAMS.length];
    store.set('cam', cam);
    lookYaw = 0; lookPitch = 0;
    say('📷 Камера: ' + CAM_NAME[cam], 1200);
  }
  function toggleMute() { sound.setMuted(!sound.muted); ui.mute.textContent = sound.muted ? '🔇' : '🔊'; }
  // 📻 — той самий ефір, що й у плеєрі сайту (кнопка ▶ у шапці): гра його не дублює, а вмикає й вимикає.
  // Поки Глек говорить, voice.js приглушує саме цей елемент <audio id="audio">.
  const radioOn = () => { const b = document.getElementById('playBtn'); return !!b && /\b(live|busy)\b/.test(b.className); };
  function toggleRadio() {
    const b = document.getElementById('playBtn');
    if (!b) { say('Плеєра радіо на цій сторінці нема'); return; }
    const was = radioOn();
    b.click();
    say(was ? '📻 Радіо вимкнено' : '📻 Радіо Глечиків у кабіні', 1600);
  }
  function toggleFs() {
    try { if (document.fullscreenElement) document.exitFullscreen(); else app.requestFullscreen(); } catch { /* пусто */ }
  }

  // ---- вікна
  function boxModal(html, wire) {
    if (modal) modal.close();
    const el = document.createElement('div');
    el.className = 'fura-modal';
    el.innerHTML = '<div class="fura-box">' + html + '</div>';
    app.appendChild(el);
    const m = { open: true, el, close() { if (!m.open) return; m.open = false; el.remove(); if (modal === m) modal = null; } };
    el.addEventListener('click', (e) => { if (e.target === el) m.close(); });
    modal = m;
    wire && wire(el, m);
    const first = el.querySelector('button');
    if (first) first.focus();
    return m;
  }

  // Вікна з пада й керма: поки гра відкрита, pad.js кнопок не тисне (вони наші), тож водимо фокусом самі.
  // Пад: хрестовина/стік ↑↓, Ⓐ — натиснути, Ⓑ — закрити. Кермо: пелюстки ↑↓, «зчепити» — натиснути, «меню» — закрити.
  // Клавіатура: ↑↓ (Enter і Пробіл тиснуть кнопку самі, Esc закриває). Перший кадр вікна лише запам'ятовує, що вже
  // натиснуто: інакше Ⓐ, що відкрив вікно, одразу натиснув би в ньому першу кнопку.
  const navPrev = new Map();
  let navFor = null;
  function navModal() {
    const el = modal && modal.el;
    if (!el) return;
    const fresh = navFor !== modal;
    navFor = modal;
    const btns = [...el.querySelectorAll('button')].filter((b) => b.offsetParent && !b.disabled);
    let i = btns.indexOf(document.activeElement);
    let move = 0, press = false, back = false;
    if (input.took('ArrowDown')) move = 1;
    if (input.took('ArrowUp')) move = -1;
    if (input.took('Escape')) back = true;
    const on = (gp, k) => k != null && k >= 0 && gp.buttons[k] && gp.buttons[k].pressed;
    for (const gp of pads()) {
      const held = new Set();
      if (isPad(gp)) {
        if (on(gp, 12) || (gp.axes[1] || 0) < -0.6) held.add('up');
        if (on(gp, 13) || (gp.axes[1] || 0) > 0.6) held.add('down');
        if (on(gp, 0)) held.add('ok');
        if (on(gp, 1)) held.add('back');
      } else {
        const c = loadCalib(gp.id);
        if (c && c.buttons) {
          if (on(gp, c.buttons.gearDown)) held.add('up');
          if (on(gp, c.buttons.gearUp)) held.add('down');
          if (on(gp, c.buttons.couple)) held.add('ok');
          if (on(gp, c.buttons.menu)) held.add('back');
        }
      }
      const before = navPrev.get(gp.id) || new Set();
      if (!fresh) for (const a of held) if (!before.has(a)) { if (a === 'up') move = -1; else if (a === 'down') move = 1; else if (a === 'ok') press = true; else back = true; }
      navPrev.set(gp.id, held);
    }
    if (!btns.length) { if (back) modal.close(); return; }
    if (move) { i = i < 0 ? 0 : (i + move + btns.length) % btns.length; btns[i].focus(); }
    if (press) (i >= 0 ? btns[i] : btns[0]).click();
    else if (back && modal) modal.close();
  }

  function openMenu() {
    const wheels = pads().filter((p) => !isPad(p));
    const ph = trips ? trips.phase : '';
    const tripItems = !trips ? '<button type="button" data-m="start">↺ На старт: причіп окремо</button>'
      + '<button type="button" data-m="startc">🔗 На старт зчепленим</button>'
      : (ph === 'free' || ph === 'done' ? '<button type="button" data-m="board">📋 Дошка замовлень</button>'
        + '<button type="button" data-m="depot">↺ У депо</button>'
        : '<button type="button" data-m="board">🚚 Поточне замовлення</button>'
        + (ph === 'pickup' ? '<button type="button" data-m="auto">🔗 Автозчеп (−10 %)</button>' : '')
        + '<button type="button" data-m="cancel">✖ Відмовитись від замовлення</button>');
    boxModal('<h3>☰ ' + (trips ? 'Чумацький край' : 'Полігон') + '</h3>'
      + '<div class="fura-row" style="flex-direction:column;align-items:stretch">'
      + '<button type="button" class="main" data-m="go">▶ Їхати далі</button>'
      + tripItems
      + '<button type="button" data-m="calib">🛞 Калібрування керма' + (wheels.length ? ' (' + shortName(wheels[0].id) + ')' : '') + '</button>'
      + '<button type="button" data-m="gear">⚙ Коробка: ' + (truck.gearMode === 'auto' ? 'автомат → зробити ручну' : 'ручна → зробити автомат') + '</button>'
      + '<button type="button" data-m="help">❓ Керування</button>'
      + '<button type="button" data-m="exit">✕ Вийти' + (trips ? ' (рейс чекатиме)' : ' з полігона') + '</button></div>', (el, m) => {
      el.addEventListener('click', (e) => {
        const b = e.target.closest('[data-m]');
        if (!b) return;
        const k = b.dataset.m;
        m.close();
        if (k === 'start') { truck.place(SPAWN, false); level.resetCones(); resetPoses(); }
        else if (k === 'startc') { truck.place(SPAWN, true); level.resetCones(); resetPoses(); }
        else if (k === 'board') trips.board();
        else if (k === 'auto') trips.auto();
        else if (k === 'cancel') trips.cancel();
        else if (k === 'depot') { truck.place({ tractor: SPAWN.tractor, trailer: { ...truck.rbPose() } }, false); resetPoses(); }
        else if (k === 'calib') calib();
        else if (k === 'gear') { if (truck.gearMode === 'auto') { truck.gearMode = 'manual'; truck.gear = 1; } else truck.setAuto(); }
        else if (k === 'help') help();
        else if (k === 'exit') exit();
      });
    });
  }

  function help() {
    const row = (a, b, c, d) => '<tr><td>' + a + '</td><td>' + b + '</td><td>' + c + '</td><td>' + d + '</td></tr>';
    boxModal('<h3>❓ Керування</h3><table>'
      + row('', '<b>⌨</b>', '<b>🎮 пад / Deck</b>', '<b>🛞 кермо</b>')
      + row('Кермо', 'A / D', 'лівий стік', 'кермо')
      + row('Газ', 'W', 'R2', 'газ')
      + row('Гальмо', 'S', 'L2', 'гальмо')
      + row('Задній хід', 'стань і тримай S', 'стань і тримай L2', 'так само, або R на H-перемикачі')
      + row('Ручник', 'Пробіл', 'Ⓑ', 'кнопка з калібрування')
      + row('Зчепити причіп', 'T', 'Ⓐ', 'кнопка з калібрування')
      + row('Камера', 'C', 'Ⓧ', 'кнопка')
      + row('Сигнал', 'H', 'Ⓨ', 'кнопка')
      + row('Передачі (ручна)', 'E / Q', 'RB / LB', 'пелюстки або H')
      + row('Поставити на колеса', 'R', 'Back', 'кнопка')
      + row('Меню', 'Esc', 'Start', 'кнопка')
      + '</table><p class="muted small">У кабіні оглядайся мишею (тягни) або правим стіком. Двічі клацни — дивитись прямо.</p>'
      + '<div class="fura-row"><button type="button" class="main">Зрозуміло</button></div>',
    (el, m) => el.querySelector('button.main').addEventListener('click', () => m.close()));
  }

  function calib(id) {
    if (modal) modal.close();
    const w = pads().find((p) => !isPad(p));
    const c = openCalib(app, id || (w && w.id), (saved) => { if (modal === c) modal = null; if (saved) say('🛞 Кермо відкалібровано — поїхали', 2500); });
    c.noNav = true;          // майстер сам читає кнопки керма — навігація вікнами з пада тут заважала б
    modal = c;
  }

  // ---- вихід
  function exit() {
    if (stopped) return;
    stopped = true;
    cancelAnimationFrame(raf);
    window.removeEventListener('resize', resize);
    document.removeEventListener('fullscreenchange', resize);
    input.dispose();
    if (voice) voice.stop();
    sound.stop();
    try { if (document.fullscreenElement) document.exitFullscreen(); } catch { /* пусто */ }
    renderer.dispose();
    try { world.free(); } catch { /* пусто */ }
    app.remove();
    if (window.__fura === api) delete window.__fura;
    onExit && onExit();
  }

  function resetPoses() { prevT = curT = snap(truck.tb); prevR = curR = snap(truck.rb); chase.yaw = truck.yawOf(truck.tb); chase.pos.set(curT.p.x, curT.p.y + 6, curT.p.z - 15); }
  resetPoses();

  // ---- кадр
  let hudAt = 0, jugX = 0, jugZ = 0, jugVX = 0, jugVZ = 0, flashAt = 0;
  if (mode === 'kray' && level.roads) {
    nav = new Nav(THREE, scene, level);
    if (server) {
      voice = new Voice({ sound, hud });
      trips = new Trips({ THREE, app, hud, level, truck, model, nav, server, say, boxModal, sound, voice, onPlaced: () => resetPoses() });
      server.onView = (v) => trips.onView(v);
      trips.onView(server.view());
    }
  }

  const api = { truck, world, input, sound, level, camera, scene, sun, renderer, quality, nav, model, get trips() { return trips; }, fake: null, get cam() { return cam; }, set cam(v) { cam = v; }, nextCam, step: (n) => { for (let i = 0; i < n; i++) physics(Object.assign(idleInput(), api.fake || {}, { hit: new Set() })); } };
  window.__fura = api;   // для перевірки з консолі: __fura.fake = {throttle: 1, …}

  function idleInput() { return { steer: 0, throttle: 0, brake: 0, clutch: 0, handbrake: false, horn: false, hit: new Set(), hGear: null, lookX: 0, lookY: 0, source: 'kbd' }; }

  function physics(inp) {
    truck.drive(inp, STEP);
    truck.update(STEP, onlyFixed, onlyFixed);
    world.step();
    truck.afterStep(STEP);
    prevT = curT; curT = snap(truck.tb);
    prevR = curR; curR = snap(truck.rb);
  }

  function frame(now) {
    if (stopped) return;
    raf = requestAnimationFrame(frame);
    let dt = (now - last) / 1000;
    last = now;
    if (dt > 0.25) dt = 0.25;               // вкладку ховали — не наздоганяємо хвилину фізики
    fps += (1 / Math.max(dt, 1e-3) - fps) * 0.05;

    const kmh = truck.speed() * 3.6;
    let inp;
    if (modal && modal.open) {
      inp = idleInput();
      if (!modal.noNav) navModal(now);
      input.pressed.clear();                 // натиски у вікні не мають спрацювати в грі, щойно воно закриється
    } else inp = input.poll(dt, kmh);
    if (api.fake) inp = Object.assign(idleInput(), api.fake, { hit: new Set(api.fake.hit || []) });
    if (api.fake && api.fake.hit) api.fake.hit = [];

    // ---- щойно натиснуте
    if (inp.hit.has('couple') && trips && trips.couplePress()) inp.hit.delete('couple');   // у зоні отримувача — «здати»
    if (inp.hit.has('couple')) {
      const r = truck.toggleCouple();
      if (r === 'on') { say('🔗 Зчеплено. Підняв ноги — можна їхати'); sound.thump(); }
      else if (r === 'off') { say('Відчеплено — причіп на ногах'); sound.thump(); }
      else if (r === 'fast') say('Спершу зупинись');
      else if (r === 'near') say('Ще не під шворнем — підправ');
      else say('Причіп далеко: здай назад під нього');
    }
    if (inp.hit.has('camera')) nextCam();
    if (inp.hit.has('reset')) {
      // спершу сказати судді: наступний стрибок пози — не телепорт
      if (trips) { trips.act('rescue'); voice.say('rescue', { gap: 5000 }); }
      truck.rescue(); resetPoses(); say('Фура на колесах');
    }
    if (inp.hit.has('menu')) { if (modal) modal.close(); else openMenu(); }
    if (!modal && input.keys.has('KeyF') && !api._f) { api._f = true; toggleFs(); } else if (!input.keys.has('KeyF')) api._f = false;
    if (!modal && input.keys.has('KeyM') && !api._m) { api._m = true; toggleMute(); } else if (!input.keys.has('KeyM')) api._m = false;
    if (inp.horn) sound.start();

    // ---- фізика рівним кроком
    if (!document.hidden) {
      acc += dt;
      let n = 0;
      while (acc >= STEP && n < 8) { physics(inp); acc -= STEP; n++; inp = Object.assign({}, inp, { hit: new Set() }); }
      if (n === 8) acc = 0;
    }
    level.sync();

    // ---- поза для малювання між кроками
    const a = Math.min(1, acc / STEP);
    pose.tp.lerpVectors(prevT.p, curT.p, a); pose.tq.slerpQuaternions(prevT.q, curT.q, a);
    pose.rp.lerpVectors(prevR.p, curR.p, a); pose.rq.slerpQuaternions(prevR.q, curR.q, a);
    model.syncBody(model.tractor, pose.tp, pose.tq);
    model.syncBody(model.trailer, pose.rp, pose.rq);
    model.syncWheels(truck.tvc, model.tWheels, 1);
    model.syncWheels(truck.rvc, model.rWheels, 1);
    model.setLegs(!truck.coupled);
    model.wheelPivot.children[0].rotation.z = -(inp.wheelSteer != null ? inp.wheelSteer : truck.steerNow / TRACTOR.maxSteer) * 2.4;

    // глечик на панелі хитається від перевантажень (пружина)
    const g = truck.gforce || { lat: 0, lon: 0 };
    jugVX += (-g.lat * 0.05 - jugX * 60 - jugVX * 6) * dt; jugX += jugVX * dt;
    jugVZ += (g.lon * 0.04 - jugZ * 60 - jugVZ * 6) * dt; jugZ += jugVZ * dt;
    model.jug.rotation.set(jugZ, 0, jugX);

    // огляд правим стіком
    if (Math.abs(inp.lookX) > 0 || Math.abs(inp.lookY) > 0) {
      lookYaw = -inp.lookX * 1.4; lookPitch = -inp.lookY * 0.4;
    } else if (!dragging && cam !== 'cabin') { lookYaw *= 0.9; }

    placeCamera(dt);
    renderer.render(scene, camera);

    // ---- удари по вантажу
    if (truck.hit > 0.02) {
      const p = Math.min(1, truck.hit / 1.5);
      if (voice && trips && trips.phase === 'haul' && p > 0.12) voice.say('clink', { gap: 12000 });
      sound.clink(p);
      if (window.HPad && HPad.rumble) try { HPad.rumble(120 + p * 200, 0.3 + p * 0.6, 0.4); } catch { /* пусто */ }
      ui.flash.classList.add('on'); flashAt = now;
      truck.hit = 0;
    }
    if (flashAt && now - flashAt > 120) { ui.flash.classList.remove('on'); flashAt = 0; }

    sound.update(truck.rpm, truck.throttleNow || 0, truck.brakeNow || 0, inp.horn && !modal);

    // ---- кермо без калібрування: запропонувати раз за вхід
    if (!modal) {
      const un = input.uncal.find((p) => !calibAsked.has(p.id) && (looksLikeWheel(p) || !loadCalib(p.id)));
      if (un) { calibAsked.add(un.id); say('🛞 Бачу «' + shortName(un.id) + '» — відкалібруймо', 2500); setTimeout(() => { if (!stopped && !modal) calib(un.id); }, 900); }
    }

    if (now - hudAt > 66) { hudAt = now; drawHud(inp, kmh); }
  }

  function drawHud(inp, kmh) {
    ui.speed.innerHTML = Math.round(Math.abs(kmh)) + '<small>км/год</small>';
    const gm = truck.gearMode === 'auto' ? 'D' : '';
    ui.gear.textContent = truck.gear < 0 ? 'R' : truck.gear === 0 ? 'N' : gm + truck.gear;
    ui.rpm.style.width = Math.round(((truck.rpm - 600) / 1500) * 100) + '%';
    ui.pg.style.height = Math.round(inp.throttle * 100) + '%';
    ui.pb.style.height = Math.round(inp.brake * 100) + '%';
    ui.pc.style.height = Math.round(inp.clutch * 100) + '%';
    ui.steer.style.left = (50 + inp.steer * 50) + '%';
    const pct = Math.floor(truck.cargo * 10) / 10;
    ui.pct.textContent = pct.toFixed(1).replace('.', ',') + ' %';
    ui.pct.classList.toggle('bad', pct < 90);
    ui.link.textContent = truck.coupled ? '🔗 причіп зчеплено' : '⛓ причіп окремо';

    let hint = '';
    const th = trips ? trips.tick(performance.now(), inp) : null;     // рейс: телеметрія, мапа в куті, своя підказка
    if (th && th.strong) hint = th.text;
    else if (!truck.coupled && !(trips && trips.phase === 'done')) {      // зданий причіп — уже не наш
      const h = truck.hitch();
      if (h.ok) hint = '🔗 Під шворнем! Тисни <b>T</b> · <b>Ⓐ</b> · кнопку «зчепити»';
      else if (h.flat < 14) {
        hint = 'Здай назад під причіп: ще ' + h.flat.toFixed(1).replace('.', ',') + ' м';
        if (h.yawDiff > 0.9) hint += ' · стань рівніше';
        if (!h.slow) hint += ' · повільніше';
      }
    }
    if (!hint && th) hint = th.text;
    if (performance.now() < toastUntil) hint = toast;
    ui.hint.innerHTML = hint;
    ui.hint.classList.toggle('on', !!hint);
    const src = { kbd: '⌨ клавіатура', pad: '🎮 пад', wheel: '🛞 кермо' }[inp.source] || '';
    ui.dev.textContent = (mode === 'kray' ? 'Чумацький край' : 'полігон') + ' · ' + src + ' · камера ' + CAM_NAME[cam] + ' · ' + Math.round(fps) + ' fps';
    const rb = hud.querySelector('[data-t=radio]');
    if (rb) rb.classList.toggle('on', radioOn());
  }

  raf = requestAnimationFrame(frame);
  cv.focus();
  if (!store.get('helped', '')) { store.set('helped', '1'); setTimeout(() => { if (!stopped && !modal) help(); }, 400); }
  return { stop: exit };
}
