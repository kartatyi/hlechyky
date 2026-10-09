# SlotKit — як зробити автомат

Спільний програвач і HUD слотів «🎰 Азарт». Файли: `kit.js` (глобал `SlotKit`, звичайний скрипт), `kit.css`.
Твій автомат — `<id>.js` + `<id>.css` поруч. Живий приклад — `slot-glek.js` (барабани, своя панель, ризик-гра).
Стенд: `proto.html?m=<id>&s=fhd|laptop|phone` — сам вантажить `kit`, `<id>-art.css/js`, `<id>.js/.css`, малює кнопки `demo`.

## 1. Що робить кіт, а що ти
Кіт: вписує автомат у контейнер (масштаб усього), HUD (баланс 🏺, ставка 10/20/50/100/200/500, «крутити», авто,
турбо, звук, ⓘ таблиця виплат, тікер скарбнички), барабани/сітку з анімацією, програвання сценарію, лінії, підрахунок
виграшу, заноси 10×/25×/50× з конфеті, звук-синт, частинки, блиск у спокої, клавіатуру (пробіл) і пад (кнопка 0).
Ти: оголошуєш автомат, будуєш свою сцену в `ctx.area` (рамку, табло, лічильники), мок `spin()`, `demo`, свої кроки.

## 2. Оголошення
```js
SlotKit.define({
  id: 'slot-cascade', title: 'Розбиті глеки',
  grid: { cols: 6, rows: 5 },
  spinStyle: 'drop',              // 'reels' — барабани (стрічки), 'drop' — сітка з падінням (каскади/кластери)
  reels: [[...], ...],            // лише для 'reels': стрічки по колонках (ключі символів)
  lines: [[1,1,1], ...],          // необов'язково: лінії (рядок для кожної колонки) — для малювання й ⓘ
  payUnit: 1,                     // множник виплат у ⓘ: pays × payUnit × ставка (у Глека 1/5 — ставка на лінію)
  paytable: [{ key: 'glek', pays: { 12: 50, 10: 20, 8: 10 }, unit: '+', note: 'найдорожчий' }],
  rules: '<b>…</b>',              // рядок або (ctx) => html — під таблицею
  sounds: { win: 'bell' },        // яким звуком дзенькати малий виграш
  initialState: () => ({}),       // стан між обертами (летить у spin і назад у script.state)
  spin(bet, state, ctx) { return script; },      // МОК: повертає сценарій (може бути Promise)
  demo: { 'Малий виграш': (bet, state) => script, 'Бонус': … },
  build(ctx) { … },               // будуєш DOM у ctx.area; викликається знову при зміні орієнтації
  steps: { mult: async (step, ctx) => { … } },  // свої типи кроків (мають пріоритет над спільними)
  // хуки (усі необов'язкові): onSpinStart, onSpinEnd(ctx, script), onReelStop(ctx, c), onTease(ctx, c),
  // onWin(ctx, step), onMeter(ctx, v), onBigwin(ctx, 'big'|'mega'|'epic'), onBet(ctx, bet), onCascade(ctx, n),
  // onRemove(els, ctx, step) → Promise (свій «тріск» у каскаді), bonusIn/bonusOut(step, ctx) (своя сцена бонусу),
  // unbuild(ctx) (перед перебудовою), destroy(ctx), mounted(ctx, inst)
});
```
Монтування (стенд/сайт): `const inst = SlotKit.mount(el, 'slot-glek', { balance, bet, api, onBalance })`.
`api.spin(bet, state, ctx)` → Promise<script> — сюди потім стане сервер; без `api` кличеться `machine.spin`.
`inst.spin()`, `inst.play(script)`, `inst.demo(name)`, `inst.addBalance(n)`, `inst.destroy()` (прибирає все).

## 3. Розміри й build
Коробка дизайну: широко 1280×800 (тікер 34, HUD 120 → `ctx.area` 1280×646), телефон 420×864 (тікер 30, HUD 206 →
420×628). Орієнтація — `ctx.orient` ('land'|'port'), розмір зони — `ctx.areaW`/`ctx.areaH`. Малюй у px дизайну —
кіт масштабує все разом. `build` кличеться при кожній зміні орієнтації: старий DOM зноситься, поле зберігається
(`ctx.keepGrid` — makeReels підхопить сам).
```js
build(ctx) {
  const st = document.createElement('div'); st.className = 'my-stage'; ctx.area.appendChild(st);
  st.innerHTML = '<div class="my-logo">' + ctx.logoHtml() + '</div><div class="my-field"></div>';
  ctx.makeReels(st.querySelector('.my-field'), { cols: 6, rows: 5, size: ctx.orient === 'port' ? 62 : 96, gap: 6 });
}
```
`makeReels(host, o)` — перший створений стає `ctx.reels`. Опції: `cols, rows, size` (сторона символу) або `height`
(висота вікна — size порахується), `cellW` (ширина барабана), `gap`, `kind: 'drop'|'reels'`, `style: 'cylinder'`
(лише reels: ефект барабана — краї стиснуті), `curve` (кут циліндра, °), `strips`, `speed`, `initial: {stops}|{grid}`.
Поле `ctx.reels`: `cell(c,r)`, `cells()`, `grid()` (по колонках `grid[c][r]`), `center(c,r)` → [x,y] у px поля,
`showLines([{pts:[[c,r]…], n, color}])`, `clearLines()`, `el`, `width/height`; drop — ще `colEl(c)`.

Арт: символ береться з `SlotArt[id].symbols[key].svg` (`ctx.symHtml(key)`/`ctx.symNode(key)`), нема — кружечок
із ключем. `extras.defs` кіт вставляє раз на сторінку. `ctx.extra('coin', '×5')` — extras арту або `null`
(роби заглушку). `ctx.logoHtml()`, `ctx.symName(key)`. Сцена `scene.base`/`scene.bonus` — кіт кладе сам.

## 4. Сценарій
Чисті дані, усе пораховано наперед: `{ bet, steps: [...], win, state, ...твої поля }`. Ставку кіт знімає сам перед
обертом; `win` — повний виграш (разом із бонусом), кіт зарахує його наприкінці й покаже занос, якщо ≥10× ставки.
**Поле завжди по колонках:** `grid[c][r]`, r=0 — верх. Клітинка — `[c, r]`.

Спільні кроки (`t`):
| крок | поля | що робить |
|---|---|---|
| `spin` | `stops` (reels) або `grid`; `tease: [c…]` | оберт/падіння; колонки з `tease` крутяться довше з підсвіткою й наростанням |
| `set` | `stops`/`grid` | поставити поле миттєво |
| `win` | `items: [{cells, amount, line?, path?, color?}]`, `amount?`, `hold?` | оживити `.win`, решту `.dim`, лінії, «+N», підрахунок у лічильнику |
| `cascade` | `remove: [[c,r]…]`, `grid` (поле ПІСЛЯ падіння), `n?` | зникнення (або твій `onRemove`), падіння решти, нові згори |
| `morph` | `cells: [[c,r,key]…]` | перетворити клітинки (дикі, що з'являються) |
| `banner` | `text`, `sub?`, `ms?`, `kind?` | банер посеред автомата |
| `bonusIn` | `count`, `title?`, `sub?` | сцена `bonus`, заставка «N вільних обертів», лічильник у HUD |
| `fs` | `left`, `add?` | вільні: лишилось / +N (банер) |
| `bonusOut` | `total`, `title?` | «Бонус приніс N», сцена назад |
| `pause` / `sound` / `clear` | `ms` / `name` / — | |

Вільні оберти — у тому самому сценарії: `bonusIn` → (`fs`, `spin`, `win`, `cascade`…)×N → `bonusOut`.
Бонус-сцену свою — хук `bonusIn(step, ctx)` / `bonusOut(step, ctx)` замість стандартних.

**Очікування:** вирішує мок/сервер — `tease: [3, 4]` лише коли справді бракує одного скатера (§2.1 контракту).
Для reels символи над/під вікном беруться зі стрічки (`stops`) — чесно. Хук `onTease(ctx, c)` — свої ефекти.

### Свій крок
```js
steps: {
  async mult(step, ctx) {                      // { t: 'mult', cells: [[c,r]], add: 5 }
    step.cells.forEach(([c, r]) => ctx.fx.at(ctx.cell(c, r), { kind: 'spark', n: 20 }));
    ctx.sound('coin'); myCounter.textContent = '×' + step.total;
    await ctx.wait(600);                         // пропускається тапом/пробілом, у турбо вдвічі коротше
  },
}
```

## 5. Що є в ctx
`bet, balance, state, turbo, auto, busy, meter, fs, lastWin, script, orient, scale, area, hud, hudExtra, root, art`
- `wait(ms)` — чекання (пропуск тапом; турбо ×0,5); `wait(ms, true)` — без турбо. `skip()` — пропустити все, що чекає.
- `roll(from, to, ms, fn)` — лічильник з тіком; `rollMeter(to, ms)` — лічильник «виграш» у HUD; `rollMs(amount)`.
- `banner(text, {sub, ms})`, `overlay(cls, html)` → el, `closeOverlay(el)`, `setScene('base'|'bonus')`.
- `showWin(items)`, `clearWin()`, `cell(c,r)`, `say(text, ms)` — репліка Глека під виграшем.
- `addBalance(n)`, `updateHud()`, `spin()`.
- `timeout/interval/listen` — **лише через них**: кіт прибере все при destroy. Без власних `setInterval` і rAF.
- `on(ev, fn)` / `emit(ev, …)` — події ті самі, що хуки.
- `busy = true` — ти тримаєш автомат (своя ризик-гра); поверни `false` і `updateHud()` після.

## 6. Своя панель
`ctx.hudExtra` — місце в HUD (широко — між виграшем і кнопками, на телефоні — окремим рядком). Кнопку додавай
у `onSpinEnd`, прибирай в `onSpinStart`. Великі панелі (ризик-гра, шкала) — у своїй сцені в `ctx.area`. Елементам,
тап по яких не має «пришвидшувати» оберт, дай клас `sk-noskip`.

## 7. Звук і частинки
`ctx.sound(name, opts)`: `stop tick click win bell big level coin crack bonus lose lever flip`. Свій:
`SlotKit.sound.add('swoosh', (h, o) => { h.tone(440, h.t, .2, 'sine', .2, 880); h.noise(h.t, .1, 2000, 1, .3); })`.
`SlotKit.sound.rise(ms)` → `stop()` (наростання). За замовчуванням звук вимкнено — у перевірках не вмикай.
Частинки — один canvas, живе лише поки є частинки: `ctx.fx.burst(x, y, {kind, n, speed, size, spread, angle,
gravity, color, life})` (x,y — px відносно `ctx.root`), `ctx.fx.at(el, {...})`, `ctx.fx.rain({kind, ms, rate})`.
Види: `coin shard confetti spark`; свій — `SlotKit.particles.egg = (g, p) => { /* малюй у (0,0), розмір p.s */ }`.

## 8. Класи для арту
Клітинка `.sk-cell` (+ `data-k`): `.win` — виграш, `.glint` — блиск у спокої (~1 с раз на 3–6 с), `.dim` — не в
виграші, `.sk-morph` — щойно перетворена, `.sk-pop` — зникає. Корінь: `.slot-<id>`, `.sk-land`/`.sk-port`,
`.sk-busy`, `.sk-turbo-on`, `.sk-infs` (вільні), `.sk-bonus` (бонус-сцена). Барабан: `.sk-reel.sk-fast` (розмиття),
`.sk-tease`; колонка сітки `.sk-col.sk-tease`.

## 9. Пороги й чесність
`SlotKit.TIERS` — 10×/25×/50× («Великий / Мега / Епічний занос»), ескалація під час підрахунку. Виграш ≤ 6 с,
тап/пробіл — до кінця. Жодних підставлених «майже»: очікування — лише за правдою сценарію.
У спокої — нуль rAF (`SlotKit.stats.loops` на стенді має бути 0); анімації спокою — лише CSS transform/opacity.

## 10. Сайт і нові помічники (09.10, інтеграція)
На сайті автомат монтує `web/games/slot.js` (один модуль на всі `slot-*`): `SlotKit.mount(el, id, { balance, bet, bets, api })`,
`api.spin` → сервер (`act('spin')` і новий `last.seq`), `api.gamble(pick)` / `api.collect()` — Ворожка (див.
`slot-glek.js`: є `ctx.api` — гроші рухає сервер, без `ctx.api` — мок стенду). Баланс — `ctx.setBalance(n)` від сервера.
- Крок **`jackpot`** `{ amount }` — свято «Скарбничка Глека!», кіт сам додає суму до балансу (у `script.win` її нема).
- Тікер: `SlotKit.setLive({ jackpot, mustHit?, lines })` — живі сума («впаде до N», якщо є `mustHit`) і рядки заносів;
  без `SlotKit.live` — мок `SK.jackpot()`/`SK.feed` (стенд).
- `opts.bets` — набір ставок з сервера (інакше `machine.bets`/`SK.BETS`).
- `ctx.morphCell(c, r, key)` — перетворити без очікування; `ctx.rel(el, base?)` — центр у px дизайну (base — `ctx.area`
  чи `ctx.box`); `ctx.flyTo(from, to, html, {ms, cls, arc, from, to, burst, delay})` → Promise — політ між елементами;
  `ctx.animate(el, keyframes, opts)` — WAAPI, яку тап/пробіл доводить до кінця, у турбо коротша.
- `ctx.gridLayer(cls, {hide})` → `{ el, slot(c, r), hide(on), remove() }` — своя сітка гнізд поверх поля;
  `ctx.hideReels(on)`, `ctx.cellBox(c, r)`. Блиск у спокої не бігає по схованому полю, у бонус-сцені й під час вільних.
- Частинки: `SlotKit.svgParticle('spark', svg)` — вид з будь-якого SVG; у `fx.burst` свої поля `img`, `data` летять у `p`.
- `DropGrid`: `weights` ({ключ: вага}) в опціях `makeReels` чи в `define` — початкове поле з вагами; `cascade` повертає
  й кладе в `ctx.lastDrop` `{ fresh, moved }` (+ подія `drop`); після каскаду кіт сам знімає `dim`, а перебір виграшів у
  спокої вимкнено, якщо в сценарії був `cascade` (чи `cycleWins: false`).
- Автогра стоїть на бонусі: `script.bonus`, крок `bonusIn` або свої кроки з `bonusSteps: ['holdIn']` у `define`.
- `wait(ms, true)` у турбо тепер ×0,6 (звичайний — ×0,5).
- `paytable[].labels` — свій підпис кількості: `{ 12: '12+', 10: '10–11' }`.
- Пробіл і пад діють лише коли автомат видно (на сайті картка буває на складі за лобі).
