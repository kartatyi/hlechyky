/*
  «📜 Історія Глечиків» — window.HHistory (13.10.2026): що й коли з'являлось на сайті, від першого коміту.

  Версія = день, коли зміни справді пішли на прод (з журналу деплоїв): 2026.10.13, другий деплой того ж дня —
  2026.10.13.2. Великим віхам — імена («Толока», «Вечорниці»). Дані — web/history-data.js (HHistoryData): епохи від
  найновішої, у кожній версії від найновішої; на кожен реліз — новий запис зверху (разом із «Що нового», sitenews.js).
  Тексти пунктів — наш власний HTML (лише <b>), тож вставляємо як є.

  Сторінка — маршрут #history (app.js); сюди ведуть підпис версії в «Я» (people.js → cardHtml) і «Що нового».
*/
(() => {
  'use strict';

  const MONTHS = ['січня', 'лютого', 'березня', 'квітня', 'травня', 'червня', 'липня', 'серпня', 'вересня', 'жовтня',
    'листопада', 'грудня'];
  const DAYS = ['неділя', 'понеділок', 'вівторок', 'середа', 'четвер', "п'ятниця", 'субота'];

  let o = null;          // { esc, go } з app.js
  let drawn = false;

  const data = () => window.HHistoryData || { eras: [] };
  const released = () => data().eras.flatMap((e) => e.versions).filter((v) => v.v && v.v !== 'next');
  /// Поточна версія сайту — найновіша, що вже пішла на прод.
  const current = () => (released()[0] || {}).v || '';

  function parts(v) {
    const m = /^(\d{4})\.(\d{2})\.(\d{2})/.exec(v || '');
    return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
  }
  function dayText(v) {
    const d = parts(v);
    return d ? `${d.getDate()} ${MONTHS[d.getMonth()]}, ${DAYS[d.getDay()]}` : '';
  }
  function span(e) {
    const vs = e.versions.filter((v) => v.v !== 'next');
    if (!vs.length) return '';
    const a = parts(vs[vs.length - 1].v), b = parts(vs[0].v);
    const d = (x) => `${x.getDate()} ${MONTHS[x.getMonth()]}`;
    return a.getTime() === b.getTime() ? d(a) : a.getMonth() === b.getMonth()
      ? `${a.getDate()}–${b.getDate()} ${MONTHS[b.getMonth()]}` : `${d(a)} – ${d(b)}`;
  }

  function versionHtml(v, esc) {
    return '<article class="hist-ver' + (v.name ? ' named' : '') + '" id="v' + esc(v.v) + '">'
      + '<div class="hist-when"><b>' + esc(v.v) + '</b><span>' + esc(dayText(v.v)) + '</span></div>'
      + '<div class="hist-body">'
      + (v.name ? '<div class="hist-name">«' + esc(v.name) + '»</div>' : '')
      + (v.lead ? '<p class="hist-lead">' + esc(v.lead) + '</p>' : '')
      + '<ul class="hist-items">' + (v.items || []).map((it) => '<li><span class="hist-ico" aria-hidden="true">'
        + esc(it.icon || '•') + '</span><span>' + (it.text || '') + '</span></li>').join('') + '</ul>'
      + '</div></article>';
  }

  function render() {
    const root = document.getElementById('history');
    if (!root || !o) return;
    const esc = o.esc;
    const eras = data().eras;
    const all = released();
    const changes = all.reduce((n, v) => n + (v.items || []).length, 0);
    const first = all.length ? parts(all[all.length - 1].v) : null;
    root.innerHTML = '<section class="panel hist-head">'
      + '<div class="hist-kick">📜 Історія Глечиків</div>'
      + '<p>Як усе росло: від першого радіо й однієї гри до сотні столів, Гончарного кола й Посиденьок. Кожна версія — день, '
      + 'коли зміни справді прийшли до вас; великі віхи мають імена. Писав Дядько Глек носиком, тож де криво — '
      + 'там для краси.</p>'
      + '<div class="hist-stats">'
      + '<span><b>' + esc(current()) + '</b> зараз</span>'
      + '<span><b>' + all.length + '</b> версій</span>'
      + '<span><b>' + changes + '</b> помітних змін</span>'
      + (first ? '<span>з <b>' + first.getDate() + ' ' + MONTHS[first.getMonth()] + '</b></span>' : '')
      + '</div></section>'
      + eras.map((e) => {
        const vs = e.versions.filter((v) => v.v !== 'next');
        if (!vs.length) return '';
        return '<section class="panel hist-era">'
          + '<div class="hist-era-head"><h2>' + esc(e.title || '') + '</h2><span class="muted">' + esc(span(e)) + '</span></div>'
          + (e.intro ? '<blockquote class="hist-intro">' + esc(e.intro) + '<span>— Дядько Глек</span></blockquote>' : '')
          + vs.map((v) => versionHtml(v, esc)).join('')
          + '</section>';
      }).join('');
    drawn = true;
  }

  window.HHistory = {
    init(opts) { o = opts; },
    show() {
      if (!drawn) render();
    },
    hide() { },
    get version() { return current(); },
    /// Рядок у своєму профілі («Я»): версія сайту й шлях до історії.
    cardHtml() {
      const v = current();
      return v ? '<a class="hist-mine" href="#history">Глечики <b>' + v + '</b> · 📜 історія сайту →</a>' : '';
    },
  };
})();
