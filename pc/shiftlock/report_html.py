"""レポートの HTML（render_report）。骨組み（CSS・script・タグ）はここ、目に見える言葉は i18n。"""
from __future__ import annotations

from . import i18n as M
from .phone import mood_str


def _hm(m: int | None) -> str:
    return "—" if m is None else f"{m // 60}:{m % 60:02d}"


def _h1(m: int) -> str:
    return f"{m / 60:.1f}"


def _times(xs: list) -> str:
    """その日の時刻（寝た／起きた）。4 件以上は最新 3 件と残りの件数"""
    more = f'<span class="mmore">+{len(xs) - 3}</span>' if len(xs) > 3 else ""
    return " ".join(xs[-3:]) + more


def render_report(data: dict) -> str:
    from html import escape as E

    def level(m: int) -> int:
        return 0 if m == 0 else 1 if m < 15 else 2 if m < 30 else 3 if m < 45 else 4

    sm = data["summary"]
    past_days = sum(1 for r in data["rows"] if not r.get("future"))   # 途中経過では今日までの日数が分母
    tiles = [
        (M.REPORT_TILE_SHIFTS, M.REPORT_TILE_SHIFTS_VAL.format(worked=sm["worked"], planned=sm["planned"]),
         M.REPORT_TILE_SHIFTS_NOTE + (M.REPORT_TILE_SHIFTS_UNCONFIRMED.format(n=sm["unconfirmed"]) if sm.get("unconfirmed") else "")),
        (M.REPORT_TILE_YASUMI, M.REPORT_TILE_YASUMI_VAL.format(n=sm["yasumi"]),
         M.REPORT_TILE_YASUMI_NOTE + (M.REPORT_TILE_YASUMI_LATE.format(n=sm["yasumi_late"]) if sm.get("yasumi_late") else "")),
        (M.REPORT_TILE_SLEEP, _hm(sm["sleep_median"]), M.REPORT_TILE_SLEEP_NOTE.format(n=sm["sleep_days"], days=past_days)),
        (M.REPORT_TILE_LONG, M.REPORT_TILE_DAYS_VAL.format(n=sm["long_days"]), M.REPORT_TILE_LONG_NOTE),
        (M.REPORT_TILE_LOW, M.REPORT_TILE_DAYS_VAL.format(n=sm["low_days"]), M.REPORT_TILE_LOW_NOTE),
    ]
    if sm.get("enforce_declare") or sm.get("enforce_run"):
        tiles.append((M.REPORT_TILE_ENFORCE, M.REPORT_TILE_ENFORCE_VAL.format(n=sm["enforce_run"]),
                      M.REPORT_TILE_ENFORCE_NOTE.format(declare=sm["enforce_declare"], release=sm["enforce_release"])))
    ab = sm.get("absence") or {}
    if ab.get("limit"):   # 当日欠勤の目安（有給除く）。目安に達していたらタイルを赤枠に
        tiles.append((M.REPORT_TILE_ABSENCE, M.REPORT_TILE_ABSENCE_VAL.format(n=ab["absent"], shifts=ab["shifts"]),
                      M.REPORT_TILE_ABSENCE_NOTE.format(limit=ab["limit"], paid=ab["paid"])   # 値も有給も同じ窓で数える
                      + (M.REPORT_TILE_ABSENCE_OVER if ab["over"] else ""), "warn" if ab["over"] else ""))
    if data.get("mood_enabled"):
        tiles.append((M.REPORT_TILE_MOOD, M.REPORT_TILE_MOOD_VAL.format(n=sm["mood_days"], days=past_days),
                      M.REPORT_TILE_MOOD_NOTE.format(down=sm["mood_down"], up=sm["mood_up"])))
    tile_html = "".join(
        f'<div class="tile{" " + t[3] if len(t) > 3 and t[3] else ""}"><div class="tl">{E(t[0])}</div>'
        f'<div class="tv">{t[1]}</div><div class="tn">{E(t[2])}</div></div>'
        for t in tiles)

    def mood_cell(ms: list) -> str:
        out = []
        for m in ms[-3:]:
            v = m["v"]
            tip = M.REPORT_MOOD_TIP.format(hm=m["hm"], mood=mood_str(v), label=M.PHONE_MOOD_LABEL[v]) + \
                (M.REPORT_MOOD_TIP_MEMO.format(memo=m["memo"]) if m["memo"] else "")
            cls = "mv strong" if abs(v) == 2 else "mv"
            out.append(f'<span class="{cls}" title="{E(tip)}">{mood_str(v)}</span>')
        more = f'<span class="mmore">+{len(ms) - 3}</span>' if len(ms) > 3 else ""
        return " ".join(out) + more

    head = "".join(f'<th class="hh">{h if h % 3 == 0 else ""}</th>' for h in range(24))
    body = []
    for r in data["rows"]:
        cells = []
        for h in range(24):
            cls = ["c"]
            if not r["cov"][h]:
                cls.append("nd")
            elif r["pc"][h] == 0 and r["away"][h]:
                cls.append("aw")
            else:
                cls.append(f"l{level(r['pc'][h])}")
            if r["visit"][h]:
                cls.append("vs")
            inner = ""
            if r["game"][h] >= 15:
                inner += '<i class="g"></i>'
            if r["claude"][h]:
                inner += '<i class="d"></i>'
            if not r["cov"][h]:
                tip = M.REPORT_TIP_NODATA
            elif r["away"][h] and r["pc"][h] == 0:
                tip = M.REPORT_TIP_AWAY
            else:
                tip = M.REPORT_TIP_PC.format(pc=r["pc"][h]) + (M.REPORT_TIP_GAME.format(game=r["game"][h]) if r["game"][h] else "") + \
                      (M.REPORT_TIP_CLAUDE.format(n=r["claude"][h]) if r["claude"][h] else "")
            if r["visit"][h]:
                tip += M.REPORT_TIP_VISIT
            cells.append(f'<td class="{" ".join(cls)}" data-tip="{E(M.REPORT_TIP_CELL.format(label=r["label"], h=h, tip=tip))}">{inner}</td>')
        shift = M.REPORT_SHIFT[r["shift"]]
        flag = " ".join(M.REPORT_FLAG[f] for f in r["flags"])
        we = " we" if r["weekday"] >= 5 else ""
        tr = '<tr class="fu">' if r.get("future") else "<tr>"   # 今日より後の日（途中経過の締め日までの行）は薄く
        body.append(
            f'{tr}<th class="dl{we}">{E(r["label"])}</th>{"".join(cells)}'
            f'<td class="n sh-{r["shift"] or "none"}">{shift}</td>'
            f'<td class="n">{_times(r["neru"])}</td>'
            f'<td class="n">{_times(r["okita"])}</td>'
            f'<td class="n" title="{E(r["sleep_span"])}">{_hm(r["sleep_min"])}</td>'
            f'<td class="n">{_h1(r["pc_total"])}</td>'
            f'<td class="n">{_h1(r["game_total"])}</td>'
            f'<td class="n">{r["claude_total"] or ""}</td>'
            + (f'<td class="n md">{mood_cell(r["mood"])}</td>' if data.get("mood_enabled") else "")
            + f'<td class="n fl">{flag}</td></tr>')

    since_note = (M.REPORT_SINCE_NOTE.format(md=f"{data['since'][5:7]}/{data['since'][8:10]}")
                  if data.get("since") and data["since"] > data["start"] else
                  (M.REPORT_NO_RECORD_NOTE if not data.get("since") else ""))
    pe = data.get("period_end")
    closed = data.get("closed", pe == data["end"]) if pe else False
    period_note = ("" if not pe else
                   (M.REPORT_PERIOD_CLOSED.format(md=f"{pe[5:7]}/{pe[8:10]}") if closed
                    else M.REPORT_PERIOD_ONGOING.format(md=f"{pe[5:7]}/{pe[8:10]}")))
    memo_html = ""
    if data.get("memos"):
        items = "".join(f'<li><span class="mdate">{E(m["date"])}</span> <span class="mv">{mood_str(m["v"])}</span> {E(m["memo"])}</li>'
                        for m in data["memos"])
        memo_html = f'<h2>{M.REPORT_MEMO_HEAD}</h2><ul class="memos">{items}</ul>'
    mood_note = M.REPORT_MOOD_NOTE if data.get("mood_enabled") else ""
    note = M.REPORT_NOTE.format(shift_end=E(data["shift_end"]), mood_note=mood_note, since_note=since_note) + \
        (M.REPORT_ABSENCE_NOTE.format(shifts=ab["window"], limit=ab["limit"]) if ab.get("limit") else "")
    mood_col = f'<th class="colh" style="text-align:left">{M.REPORT_COL_MOOD}</th>' if data.get("mood_enabled") else ""

    return f"""<!DOCTYPE html>
<html lang="ja"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{M.REPORT_TITLE} {E(data["start"])}〜{E(data["end"])}</title>
<style>
.viz-root{{color-scheme:light;
 --surface-1:#fcfcfb;--surface-2:#f3f2ef;--border:#dcdbd6;
 --text-primary:#0b0b0b;--text-secondary:#52514e;--text-muted:#77766f;
 --l0:#f1f0ec;--l1:#b7d3f6;--l2:#86b6ef;--l3:#3987e5;--l4:#1c5cab;
 --away:#c4c2bb;--nodata:#c9c7c0;--game:#eb6834;--dot:#0b0b0b;--yasumi:#d03b3b}}
@media (prefers-color-scheme:dark){{:root:where(:not([data-theme="light"])) .viz-root{{color-scheme:dark;
 --surface-1:#1a1a19;--surface-2:#232321;--border:#3a3a37;
 --text-primary:#f0efec;--text-secondary:#c3c2b7;--text-muted:#9a998f;
 --l0:#232321;--l1:#104281;--l2:#256abf;--l3:#3987e5;--l4:#86b6ef;
 --away:#4a4a46;--nodata:#5a5a55;--game:#d95926;--dot:#f0efec;--yasumi:#e66767}}}}
*{{box-sizing:border-box}}
body{{margin:0;background:#fcfcfb}}
.viz-root{{background:var(--surface-1);color:var(--text-primary);min-height:100vh;
 font:13px/1.5 system-ui,"Hiragino Sans","Yu Gothic UI","Meiryo",sans-serif;padding:24px 28px;font-variant-numeric:tabular-nums}}
h1{{font-size:20px;margin:0 0 2px;font-weight:650}}
.sub{{color:var(--text-secondary);margin:0 0 16px}}
.tiles{{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:18px}}
.tile{{background:var(--surface-2);border-radius:8px;padding:10px 14px;min-width:150px}}
.tl{{color:var(--text-secondary);font-size:12px}}
.tv{{font-size:24px;font-weight:650;line-height:1.3}}
.tv small{{font-size:13px;font-weight:400;color:var(--text-secondary)}}
.tn{{color:var(--text-muted);font-size:11px}}
.tile.warn{{outline:2px solid var(--yasumi)}}.tile.warn .tv{{color:var(--yasumi)}}
tr.fu td.c,tr.fu td.n{{opacity:.45}}
.legend{{display:flex;gap:16px;flex-wrap:wrap;align-items:center;color:var(--text-secondary);font-size:12px;margin:0 0 8px}}
.legend span{{display:inline-flex;align-items:center;gap:5px}}
.sw{{width:14px;height:12px;border-radius:3px;display:inline-block;position:relative}}
.ramp{{display:inline-flex;gap:2px}}
table{{border-collapse:separate;border-spacing:2px}}
th{{font-weight:500;color:var(--text-secondary);font-size:11px}}
.hh{{width:20px;text-align:left;padding:0}}
.dl{{text-align:right;padding-right:6px;white-space:nowrap;font-size:12px}}
.dl.we{{color:var(--text-primary);font-weight:600}}
td.c{{width:20px;height:18px;border-radius:3px;position:relative;padding:0}}
.l0{{background:var(--l0)}}.l1{{background:var(--l1)}}.l2{{background:var(--l2)}}.l3{{background:var(--l3)}}.l4{{background:var(--l4)}}
.aw{{background:var(--away)}}
.nd{{background:repeating-linear-gradient(45deg,var(--nodata) 0 1.5px,transparent 1.5px 4.5px)}}
.vs{{outline:1.5px dashed var(--text-secondary);outline-offset:-1.5px}}
i.g{{position:absolute;left:2px;right:2px;bottom:2px;height:4px;border-radius:2px;background:var(--game);box-shadow:0 0 0 1px var(--surface-1)}}
i.d{{position:absolute;right:2px;top:2px;width:5px;height:5px;border-radius:50%;background:var(--dot);box-shadow:0 0 0 1px var(--surface-1)}}
td.n{{padding:0 8px;text-align:right;white-space:nowrap;color:var(--text-primary)}}
td.fl{{text-align:left;color:var(--text-secondary);font-size:12px}}
.sh-yasumi{{color:var(--yasumi)!important;font-weight:600}}
.sh-unconfirmed{{color:var(--text-muted)!important}}
.colh{{padding:0 8px;text-align:right;white-space:nowrap}}
td.md{{text-align:left;font-size:12px}}
.mv{{white-space:nowrap;color:var(--text-primary)}}
.mv.strong{{font-weight:700}}
.mmore{{color:var(--text-muted);font-size:11px;margin-left:3px}}
h2{{font-size:15px;margin:18px 0 6px;font-weight:650}}
.memos{{margin:0;padding-left:18px;columns:2;column-gap:28px;max-width:1100px}}
.memos li{{break-inside:avoid;margin:0 0 3px}}
.mdate{{color:var(--text-secondary);font-size:12px}}
.note{{color:var(--text-muted);font-size:11.5px;margin-top:14px;max-width:900px}}
#tip{{position:fixed;pointer-events:none;background:var(--text-primary);color:var(--surface-1);font-size:12px;
 padding:4px 8px;border-radius:6px;display:none;z-index:9;white-space:nowrap}}
@page{{size:A4 landscape;margin:10mm}}
@media print{{.viz-root{{padding:0;min-height:auto;zoom:.70}}#tip{{display:none!important}}
 *{{-webkit-print-color-adjust:exact;print-color-adjust:exact}}}}
</style></head>
<body><div class="viz-root">
<h1>{M.REPORT_TITLE}</h1>
<p class="sub">{M.REPORT_SUB.format(start=E(data["start"]), end=E(data["end"]), days=len(data["rows"]), period=period_note, generated=E(data["generated"]))}</p>
<div class="tiles">{tile_html}</div>
<div class="legend">
 <span>{M.REPORT_LEGEND_PC}
  <span class="ramp"><i class="sw l0"></i><i class="sw l1"></i><i class="sw l2"></i><i class="sw l3"></i><i class="sw l4"></i></span>
  {M.REPORT_LEGEND_RAMP}</span>
 <span><i class="sw" style="background:var(--surface-2)"><i class="g"></i></i>{M.REPORT_LEGEND_GAME}</span>
 <span><i class="sw" style="background:var(--surface-2)"><i class="d"></i></i>{M.REPORT_LEGEND_CLAUDE}</span>
 <span><i class="sw aw"></i>{M.REPORT_LEGEND_AWAY}</span>
 <span><i class="sw nd"></i>{M.REPORT_LEGEND_NODATA}</span>
 <span><i class="sw l0 vs"></i>{M.REPORT_LEGEND_VISIT}</span>
</div>
<table aria-label="{M.REPORT_TABLE_ARIA}">
<thead><tr><th></th>{head}<th class="colh">{M.REPORT_COL_SHIFT}</th><th class="colh">{M.REPORT_COL_NERU}</th><th class="colh">{M.REPORT_COL_OKITA}</th><th class="colh">{M.REPORT_COL_SLEEP}</th>
<th class="colh">{M.REPORT_COL_PC}</th><th class="colh">{M.REPORT_COL_GAME}</th><th class="colh">{M.REPORT_COL_CLAUDE}</th>{mood_col}<th class="colh" style="text-align:left">{M.REPORT_COL_MEMO}</th></tr></thead>
<tbody>{"".join(body)}</tbody></table>
<p class="note">{note}</p>
{memo_html}
<div id="tip"></div>
</div>
<script>
(function(){{var t=document.getElementById('tip');
document.querySelectorAll('td[data-tip]').forEach(function(c){{
c.addEventListener('mouseenter',function(){{t.textContent=c.dataset.tip;t.style.display='block'}});
c.addEventListener('mousemove',function(e){{t.style.left=(e.clientX+12)+'px';t.style.top=(e.clientY+12)+'px'}});
c.addEventListener('mouseleave',function(){{t.style.display='none'}});}});}})();
</script>
</body></html>
"""
