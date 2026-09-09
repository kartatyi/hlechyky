# -*- coding: utf-8 -*-
"""
Генератор словників «Глечиків». Тримається тут не для краси, а щоб через рік можна було
перегенерувати списки з новішої версії dict_uk і побачити, що саме змінилось.

Що робить:
  1) читає dict_corp_vis.txt (brown-uk/dict_uk) — усі словоформи української з POS-тегами;
  2) читає два частотні списки (OpenSubtitles + Leipzig news), зводить їх у частоту «на мільйон»;
  3) частота леми = сума частот усіх її словоформ (так «загальновідомість» видно куди краще,
     ніж за самим називним: «книга» частіша не в називному);
  4) відбирає іменники, чистить від власних назв, абревіатур, застарілого, лайки й
     субстантивованих прикметників, накладає ручні списки curation-drop.txt / curation-keep.txt;
  5) пише uk-5.txt (відповіді Глек-слова), uk-guess.txt (усі допустимі 5-літерні),
     uk-hangman.txt (5000 найчастіших іменників 5–12 літер) і uk-all.txt (усі форми, для Ерудита).

Запуск (потрібен лише python 3):
    python make-lists.py dict_corp_vis.txt freq-opensubtitles.txt freq-leipzig.txt .

Звідки беруться вхідні файли — див. LICENSE.txt.
"""
import io, sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

UA = set("абвгґдеєжзиіїйклмнопрстуфхцчшщьюя")

# теги dict_uk, з якими лема не годиться у відповіді: власні назви, абревіатури, застаріле,
# рідковживане, діалектне, правописні варіанти, лайка
BAD_LEMMA = {"bad", "obsc", "vulg", "slang", "arch", "rare", "alt", "prop", "abbr",
             "subst", "up19", "up92", "foreign", "latin", "noninfl"}
# для великого словника (Ерудит) фільтр м'якший: там важлива повнота, а не «краса» слова
BAD_FORM = {"bad", "obsc", "vulg", "prop", "abbr", "foreign", "latin"}

# Те, що словник не позначив, а друзям у чаті бачити ні до чого.
BLOCK = set("""лайно піхва попка гівно бидло падло сучка курва блуд повія бордель
жопа дупа хрін член пеніс вагіна оргія стерво шльондра паскуда виблядок
труп смерть кров рабин жид москаль кацап хохол нігер
пияк пияка алкаш нарик наркота героїн кокаїн опіум
пекло сатана демон""".split())

MIN_FREQ_5 = 0.4        # на мільйон, сума двох корпусів — нижче цього слово вже не «загальновідоме»
HANGMAN_TOP = 5000


def clean(w):
    return bool(w) and all(ch in UA for ch in w)


def read_words(fn):
    return {l.strip() for l in open(fn, encoding="utf-8") if l.strip()}


def read_freq(paths):
    """Зводить кілька частотних списків «слово частота» в один: частота на мільйон, сума."""
    out = {}
    for path in paths:
        d, total = {}, 0
        for line in open(path, encoding="utf-8"):
            p = line.split()
            if len(p) < 2:
                continue
            # OpenSubtitles: «слово частота»; Leipzig: «номер\tслово\tчастота»
            w, c = (p[1] if len(p) == 3 and p[0].isdigit() else p[0]).lower(), p[-1]
            try:
                c = int(c)
            except ValueError:
                continue
            d[w] = d.get(w, 0) + c
            total += c
        for w, c in d.items():
            out[w] = out.get(w, 0.0) + c * 1e6 / total
    return out


def parse_dict(path, freq):
    """dict_corp_vis: лема — рядок без відступу, її форми — з відступом."""
    adjforms, nouns, forms5, allforms = set(), {}, set(), set()
    lemma, seen, total = None, set(), 0.0

    def flush():
        if lemma is not None:
            nouns[lemma] = max(nouns.get(lemma, 0.0), total)

    for line in open(path, encoding="utf-8"):
        indented = line[:1] == " "
        line = line.split("#")[0].rstrip()
        if not line.strip():
            continue
        p = line.split()
        if len(p) < 2:
            continue
        word, tags = p[0], set(p[1].split(":"))
        ok = clean(word)

        if ok and not (tags & BAD_FORM):
            allforms.add(word)
            if len(word) == 5:
                forms5.add(word)

        # будь-яка форма прикметника/числівника/займенника — щоб відсіяти «сивий», «вірні», «зайве»
        if ok and (tags & {"adj", "adjp", "numr", "pron"}):
            adjforms.add(word)

        if not indented:
            flush()
            lemma, seen, total = None, set(), 0.0
            # іменник у називному однини (або pluralia tantum: «двері», «гроші»)
            if (ok and "noun" in tags and "v_naz" in tags
                    and ("p" not in tags or "ns" in tags)
                    and not (tags & BAD_LEMMA) and 5 <= len(word) <= 12
                    and word not in BLOCK):
                lemma = word

        if lemma is not None and ok and word not in seen:
            seen.add(word)
            total += freq.get(word, 0.0)
    flush()

    nouns = {w: c for w, c in nouns.items() if w not in adjforms}
    return nouns, forms5, allforms


def main(dictpath, freq1, freq2, outdir):
    freq = read_freq([freq1, freq2])
    nouns, forms5, allforms = parse_dict(dictpath, freq)

    drop = read_words(outdir + "/curation-drop.txt")
    keep = read_words(outdir + "/curation-keep.txt")

    five = {w for w, c in nouns.items() if len(w) == 5 and c >= MIN_FREQ_5}
    five |= {w for w in keep if len(w) == 5}
    five -= drop

    hang = [w for w, c in sorted(nouns.items(), key=lambda x: -x[1]) if w not in drop][:HANGMAN_TOP]
    guess = forms5 | five

    def write(name, words):
        with open(outdir + "/" + name, "w", encoding="utf-8", newline="\n") as f:
            for w in sorted(words):
                f.write(w + "\n")
        print(name, len(words))

    write("uk-5.txt", five)
    write("uk-guess.txt", guess)
    write("uk-hangman.txt", hang)
    write("uk-all.txt", allforms)
    assert five <= guess, "uk-5 має бути підмножиною uk-guess"


if __name__ == "__main__":
    main(*sys.argv[1:5])
