// 🖼 Емодзі картинками (web/emo.js). Значки сайту — емодзі, а кожна система малює їх по-своєму: на Windows, Android
// та в Apple шапка й навігація виглядали як три різні сайти (і амфора черепків усюди інша). Тут ті самі емодзі в шапці,
// навігації й кнопках підмінюються картинками одного набору — Microsoft Fluent Emoji (static/emoji, ліцензія MIT) — тож
// усі бачать те саме, що й на Windows. У балачках, реакціях і текстах людей емодзі лишаються рідними: це їхня мова.
//
// Розмітку ніхто не переписував: модуль сам знаходить відомі емодзі в тексті своїх ділянок (ROOTS) і стежить за ними —
// панель «В ефірі», черга й поради перемальовуються з кожним треком. Картинка має alt з тим самим емодзі, тож копіювання
// тексту й читачі екрана бачать його, як і раніше.
(function () {
  'use strict';
  const MAP = {
    '📻': '1f4fb', '🎵': '1f3b5', '🎮': '1f3ae', '📊': '1f4ca', '💬': '1f4ac', '🏺': '1f3fa', '🚨': '1f6a8', '🎙': '1f399',
    '💡': '1f4a1', '❤': '2764', '⏭': '23ed', '🎧': '1f3a7', '👤': '1f464', '🛍': '1f6cd', '🎁': '1f381', '⌨': '2328',
    '📜': '1f4dc', '🕘': '1f558', '📂': '1f4c2', '📈': '1f4c8', '🚫': '1f6ab', '📣': '1f4e3', '📷': '1f4f7', '🛡': '1f6e1',
    '🖥': '1f5a5', '🔔': '1f514', '🙂': '1f642', '📎': '1f4ce', '🌙': '1f319', '✨': '2728', '👍': '1f44d', '👎': '1f44e',
  };
  // Де підмінюємо: каркас сайту. Де ні — реакції над обкладинкою, репліки людей, поля вводу.
  const ROOTS = 'header, .mtabs, #libTabs, .libline, .nightbar, #now, #queue, #sugCards, #chatTabs, #chatForm, #home';
  const SKIP = '.reacts, .hm-msg, .memenu small, input, textarea, [data-noemo]';
  const RE = new RegExp('(' + Object.keys(MAP).join('|') + ')\\uFE0F?', 'u');
  const REG = new RegExp(RE.source, 'gu');

  function swap(node) {
    const s = node.nodeValue;
    const host = node.parentElement;
    if (!s || !host || !RE.test(s) || host.closest(SKIP)) return;
    const frag = document.createDocumentFragment();
    let i = 0, m;
    REG.lastIndex = 0;
    while ((m = REG.exec(s))) {
      if (m.index > i) frag.append(s.slice(i, m.index));
      const img = new Image();
      img.className = 'emo';
      img.alt = m[1];
      img.draggable = false;
      img.src = '/static/emoji/' + MAP[m[1]] + '.svg';
      frag.append(img);
      i = m.index + m[0].length;
    }
    if (i < s.length) frag.append(s.slice(i));
    node.replaceWith(frag);
  }

  function apply(root) {
    if (root.nodeType === 3) { swap(root); return; }
    if (root.nodeType !== 1) return;
    const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const found = [];
    for (let n = w.nextNode(); n; n = w.nextNode()) if (RE.test(n.nodeValue)) found.push(n);
    found.forEach(swap);
  }

  const seen = new WeakSet();
  const watch = new MutationObserver((list) => {
    for (const m of list) for (const n of m.addedNodes) apply(n);
  });
  /// Ділянки, що з'являються пізніше (головна малюється, коли на неї вперше зайшли), — підхоплюємо тим самим викликом.
  function scan() {
    document.querySelectorAll(ROOTS).forEach((root) => {
      if (seen.has(root)) return;
      seen.add(root);
      apply(root);
      watch.observe(root, { childList: true, subtree: true });
    });
  }

  window.HEmo = { scan, apply };
  scan();
})();
