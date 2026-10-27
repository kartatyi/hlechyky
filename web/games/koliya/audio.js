// «Колія» — звук синтезом WebAudio, без файлів. Власник: K4.
// Контракт: createAudio() → { enabled, setEnabled(bool), play(events) (state.ev цього тику), dispose() }
// ЗАВЖДИ заглушено за замовчуванням; вмикає лише кнопка в грі (панель). У перевірках не вмикати.
export function createAudio() {
  return { enabled: false, setEnabled(b) { this.enabled = !!b; }, play() { }, dispose() { } };
}
