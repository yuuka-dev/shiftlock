"""普段の話し言葉から 帰宅／就寝／起床 を拾う判定と、わからんときの聞き返し（pending）と学習（learned.json）。
ここの正規表現・語彙は「入力」の定義なので i18n には置かない（表示の言葉だけ i18n）。"""
from __future__ import annotations

import re
from datetime import datetime, timedelta

from . import i18n as M
from .store import _read, Store, _write


def norm_kw(s: str) -> str:
    """『ねるわー』『おきたぁ！』『おやすみ♡』みたいな語尾のゆれを落とす。"""
    s = s.strip()
    s = re.sub(r"[ー〜~!！?？。．.、,wｗ♡♥❤☆★…\s]+$", "", s)
    s = re.sub(r"[ぁぃぅぇぉっ]+$", "", s)
    return s


LEARNED_KINDS = ("kitaku", "neru", "okita", "none")   # learned.json のキー（この順で一覧とメニューに出す。表示の言葉は i18n）


def to_hira(s: str) -> str:
    return "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in s)


def norm_text(s: str) -> str:
    """半角カナ・全角英数・カタカナを揃える（ｵﾔｽﾐ / オヤスミ / おやすみ を同じに）。"""
    import unicodedata
    return to_hira(unicodedata.normalize("NFKC", s)).lower().strip()


_EDGE = r"[\s、。,.!?w♡♥❤☆★…ー〜~・*\"'「」『』()（）]+"


def phrase_core(s: str) -> str:
    t = norm_text(s)
    t = re.sub(r"^" + _EDGE, "", t)
    t = re.sub(_EDGE + r"$", "", t)
    return t[:30]


# パターンは norm_text 後（ひらがな・半角記号）に当てる
_P = r"(?:^|[\s、。,!?w…ー〜~♡♥❤☆★」』)]|もう|そろそろ|今から|いまから|ほな|じゃあ|じゃ|ので|から|し)"


INTENT_PATTERNS = {
    "kitaku": [r"ただいま", r"帰宅", r"(?:おうち|お家|家|いえ|うち)(?:に|へ)?(?:帰|かえ)(?:った|ってきた|ってきて|りました|ってきました)",
               r"(?:おうち|お家|家|いえ)(?:に)?(?:着|つ)いた", r"(?:帰|かえ)ってきた", r"帰庫して(?:帰|かえ)"],
    "neru": [_P + r"(?:寝|ね)(?:る|ます|よう|よっ|ちゃう|てくる|るわ|るね|るー)",
             r"寝る(?!前|まえ)", r"即寝|寝落ち|寝(?:する|します|てくる)", r"すや(?:り|ぁ|すや|る|ります|ーん)", r"おやすみ", r"就寝", r"zzz|💤",
             r"(?:布団|ふとん|おふとぅん|おふとん|べっど|べっと)(?:に|へ)?(?:入|はい|い)(?:る|ります|った)"],
    "okita": [r"(?:起|お)きた(?![らいく])", r"おはよ", r"目(?:が)?覚め(?:た|ました)", r"起床", r"めざめた"],
}


NEGATE = {
    "neru": r"(?:寝|ね)(?:れ|られ|むれ)(?:ない|へん|ん|ず|なく)|眠れ(?:ない|へん|ん)|寝(?:たら|る前|るまえ)|ねるまえ|ねるねる",
    "okita": r"起き(?:れ|られ)(?:ない|へん|ん)|起きたら|おきたら",
}


# 「っぽい言葉はあるけど決めきれへん」ときに聞くためのヒント
WEAK = {
    "neru": r"ねむ|眠|おやす|すや|ふとん|布団|べっど|ばたん|ぐー|ぐう|zz|💤|寝|ねんね|おねむ|ぬくぬく",
    "okita": r"おは(?![なしかに])|起き|めざ|むくり|むくっ",
    "kitaku": r"ただい|かえっ(?!て[^き])|かえり|帰っ|帰り|(?:家|いえ|うち|おうち)(?:に)?(?:つ|着)い|帰庫",
}


REQUEST = (r"(?:[てで](?:ほしい|欲しい|くれ|ちょうだい|ください|下さい|みて|ね$|な$|$|[ー〜!。])"
           r"|お願い|おねがい|頼む|たのむ|まえに|前に|[?]|```|https?://|\.py|\.ts|\.js|error|やっといて|しといて)")


AFFIRM = r"^(?:うん|うむ|そう|そうそう|そうやで|そうよ|せや|せやで|せやねん|はい|yes|y|ok|おけ|おっけ|おk|ええよ|あってる|正解|それ|ん|そ)$"


DENY = r"^(?:ちゃう|ちゃうで|ちゃうねん|ちゃいます|ちがう|違う|ちゃうちゃう|いいえ|いや|no|n|ううん)"


def load_learned(store: Store) -> dict:
    d = _read(store.p("learned.json"), {})
    return {k: list(d.get(k, [])) for k in LEARNED_KINDS}


def learn(store: Store, phrase: str, kind: str) -> str:
    core = phrase_core(phrase)
    if len(core) < 2:
        return ""
    d = load_learned(store)
    for k in d:
        if core in d[k]:
            d[k].remove(core)
    d[kind].append(core)
    _write(store.p("learned.json"), d)
    return core


def detect_intents(text: str, learned: dict | None = None) -> list[str]:
    """『おうちかえった！ねむねむだからスヤリンコする！ｗ』→ ['kitaku', 'neru']。
    お願いごと・質問・長文・寝れない系は拾わへん（誤爆したらプロンプトを食べるより記録をミスる方がマシ）。"""
    t = norm_text(text)
    if not t or len(t) > 80 or "\n" in t or re.search(REQUEST, t):
        return []
    learned = learned or {}
    if phrase_core(t) in learned.get("none", []):
        return []
    found = {}
    for k, pats in INTENT_PATTERNS.items():
        if k in NEGATE and re.search(NEGATE[k], t):
            continue
        pos = [m.start() for pat in pats for m in re.finditer(pat, t)]
        pos += [t.rfind(ph) for ph in learned.get(k, []) if ph and ph in t]
        if pos:
            found[k] = max(pos)
    out = ["kitaku"] if "kitaku" in found else []
    if "neru" in found and "okita" in found:      # 『起きたけどまた寝る』→ 後ろのほうを採用
        out.append("neru" if found["neru"] > found["okita"] else "okita")
    elif "neru" in found or "okita" in found:
        out.append("neru" if "neru" in found else "okita")
    return out


def weak_intent(text: str, learned: dict | None = None) -> str | None:
    """はっきりは拾えへんけど、それっぽい言葉がある → 聞く候補（一番後ろに出てきたもの）。"""
    t = norm_text(text)
    if not t or len(t) > 40 or "\n" in t or re.search(REQUEST, t):
        return None
    if learned and phrase_core(t) in learned.get("none", []):
        return None
    best = None
    for k, pat in WEAK.items():
        if k in NEGATE and re.search(NEGATE[k], t):
            continue
        ms = [m.start() for m in re.finditer(pat, t)]
        if ms and (best is None or max(ms) > best[1]):
            best = (k, max(ms))
    return best[0] if best else None


ASK_COOLDOWN = timedelta(minutes=30)


def can_ask(store: Store, now: datetime) -> bool:
    last = _read(store.p("state.json"), {}).get("last_ask")
    return not last or now - datetime.fromisoformat(last) >= ASK_COOLDOWN


def set_pending(store: Store, phrase: str, kind: str, now: datetime) -> None:
    st = _read(store.p("state.json"), {})
    st["last_ask"] = now.isoformat(timespec="minutes")
    st["pending"] = {"phrase": phrase, "kind": kind, "at": now.isoformat(timespec="minutes")}
    _write(store.p("state.json"), st)


def take_pending(store: Store, now: datetime) -> dict | None:
    st = _read(store.p("state.json"), {})
    pd = st.get("pending")
    if not pd:
        return None
    if now - datetime.fromisoformat(pd["at"]) > timedelta(minutes=20):
        st.pop("pending", None)
        _write(store.p("state.json"), st)
        return None
    return pd


def clear_pending(store: Store) -> None:
    st = _read(store.p("state.json"), {})
    if st.pop("pending", None) is not None:
        _write(store.p("state.json"), st)


def learned_text(store: Store) -> str:
    d = load_learned(store)
    lines = []
    for k in LEARNED_KINDS:
        lines.append(M.INTENT_LEARNED_LINE.format(name=M.INTENT_LEARNED_LABELS[k],
                                                  phrases=M.INTENT_LEARNED_SEP.join(d[k]) if d[k] else M.COMMON_NONE))
    return M.INTENT_LEARNED_HEAD + "\n" + "\n".join(lines)


def unlearn(store: Store, phrase: str) -> str:
    core = phrase_core(phrase)
    d = load_learned(store)
    hit = [k for k in d if core in d[k]]
    for k in hit:
        d[k].remove(core)
    _write(store.p("learned.json"), d)
    return M.INTENT_FORGOT.format(core=core) if hit else M.INTENT_NOT_LEARNED.format(core=core)
