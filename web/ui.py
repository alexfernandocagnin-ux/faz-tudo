"""Peças visuais do app. Todas as cores usam transparência sobre o fundo e o texto herda a cor do tema,
então ficam legíveis tanto no tema claro quanto no escuro."""

from __future__ import annotations

from html import escape

import streamlit as st

CSS = """
<style>
  .block-container { padding-top: 2.2rem; max-width: 1180px; }
  h1 { letter-spacing: -0.02em; }
  .ft-lead { opacity: .78; font-size: 1.02rem; margin: -0.4rem 0 1.4rem 0; }
  .ft-card { border: 1px solid rgba(127,127,127,.22); border-radius: 14px; padding: 18px 22px; margin-bottom: 14px;
             background: rgba(127,127,127,.045); }
  .ft-meta { font-size: .82rem; opacity: .7; }
  .ft-headline { font-size: 1.22rem; font-weight: 650; line-height: 1.45; margin: 4px 0 10px 0; }
  .ft-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .ft-spacer { flex: 1; }
  .ft-badge { display: inline-block; padding: 2px 11px; border-radius: 999px; font-weight: 700; font-size: .78rem;
              border: 1px solid transparent; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
  .ft-red    { background: rgba(239,68,68,.14);  border-color: rgba(239,68,68,.45); }
  .ft-amber  { background: rgba(245,158,11,.16); border-color: rgba(245,158,11,.5); }
  .ft-teal   { background: rgba(20,184,166,.14); border-color: rgba(20,184,166,.5); }
  .ft-gray   { background: rgba(127,127,127,.14); border-color: rgba(127,127,127,.4); }
  .ft-violet { background: rgba(139,92,246,.15); border-color: rgba(139,92,246,.45); }
  .ft-qty { font-weight: 700; font-size: .92rem; }
  .ft-title { font-size: 1.25rem; font-weight: 680; margin: 12px 0 2px 0; line-height: 1.35; }
  .ft-sia { font-family: ui-monospace, Consolas, monospace; font-size: .78rem; opacity: .6; }
  .ft-section { font-size: .72rem; font-weight: 800; letter-spacing: .06em; opacity: .62; margin: 18px 0 8px 0; text-transform: uppercase; }
  .ft-area { display: flex; gap: 14px; border: 1px solid rgba(20,184,166,.45); background: rgba(20,184,166,.09);
             border-radius: 12px; padding: 14px 16px; margin-top: 14px; }
  .ft-area-key { flex: 0 0 auto; width: 58px; height: 58px; border-radius: 12px; background: #0d9488; color: #fff;
                 display: flex; align-items: center; justify-content: center; font-weight: 800; font-size: .9rem; }
  .ft-area-label { font-weight: 700; font-size: 1.02rem; }
  .ft-area p { margin: 3px 0; }
  .ft-alt { border: 1px dashed rgba(127,127,127,.45); border-radius: 10px; padding: 9px 14px; margin-top: 8px; font-size: .93rem; }
  .ft-list { margin: 4px 0 0 0; padding-left: 1.1rem; }
  .ft-list li { margin: 4px 0; line-height: 1.5; }
  .ft-send li { font-weight: 600; }
  .ft-step { display: flex; gap: 12px; margin-bottom: 14px; }
  .ft-step-n { flex: 0 0 auto; width: 28px; height: 28px; border-radius: 50%; background: #0d9488; color: #fff;
               display: flex; align-items: center; justify-content: center; font-weight: 800; font-size: .85rem; }
  .ft-step-title { font-weight: 650; }
  .ft-step-sys { font-size: .8rem; opacity: .65; }
  .ft-step-text { opacity: .9; margin-top: 3px; line-height: 1.5; }
  .ft-tip { margin-top: 6px; padding: 6px 10px; border-radius: 8px; background: rgba(20,184,166,.10); font-size: .9rem; }
  .ft-impact { border: 1px solid rgba(239,68,68,.45); background: rgba(239,68,68,.08); border-radius: 10px; padding: 10px 14px; }
  .ft-brand { display: flex; gap: 10px; align-items: center; margin-bottom: 4px; }
  .ft-logo { width: 38px; height: 38px; border-radius: 10px; background: linear-gradient(135deg,#2dd4bf,#0f766e);
             display: flex; align-items: center; justify-content: center; color: #fff; font-weight: 800; }
  .ft-brand-name { font-weight: 750; font-size: 1.1rem; line-height: 1.1; }
  .ft-brand-sub { font-size: .78rem; opacity: .65; }
  .ft-group { border-left: 4px solid rgba(239,68,68,.8); }
  .ft-num { width: 24px; height: 24px; border-radius: 50%; background: rgba(127,127,127,.18); display: inline-flex;
            align-items: center; justify-content: center; font-weight: 700; font-size: .8rem; }
  .ft-item-title { font-weight: 700; font-size: 1.02rem; }
  .ft-field { display: grid; grid-template-columns: 110px 1fr; gap: 10px; margin-top: 10px; line-height: 1.5; }
  .ft-fix { display: grid; grid-template-columns: 110px 1fr; gap: 10px; margin-top: 10px; padding: 10px 12px;
            border-radius: 10px; background: rgba(20,184,166,.12); border: 1px solid rgba(20,184,166,.35); line-height: 1.5; }
  .ft-label { font-size: .72rem; font-weight: 800; letter-spacing: .05em; text-transform: uppercase; opacity: .65; padding-top: 3px; }
  .ft-fix .ft-label { opacity: .9; }
  .ft-chip { display: inline-block; margin: 0 6px 4px 0; padding: 1px 9px; border-radius: 6px; font-size: .85rem;
             font-family: ui-monospace, Consolas, monospace; background: rgba(127,127,127,.14); border: 1px solid rgba(127,127,127,.3); }
  .ft-note { margin-top: 8px; font-size: .88rem; opacity: .75; padding-left: 120px; }
  @media (max-width: 640px) {
    .ft-field, .ft-fix { grid-template-columns: 1fr; gap: 2px; }
    .ft-note { padding-left: 0; }
  }
</style>
"""

GROUP_CLASS = {"GRV": "ft-red", "NOR": "ft-red", "ACE": "ft-amber", "AVS": "ft-amber", "ADV": "ft-gray"}
SEVERITY_CLASS = {"CriticalGlosa": "ft-red", "PartialGlosa": "ft-amber", "SystemBlocker": "ft-violet", "Warning": "ft-gray"}


def e(text) -> str:
    return escape(str(text or ""))


def inject_css() -> None:
    st.markdown(CSS, unsafe_allow_html=True)


def html(markup: str) -> None:
    st.markdown(markup, unsafe_allow_html=True)


def page_header(title: str, lead: str) -> None:
    st.title(title)
    html(f'<div class="ft-lead">{e(lead)}</div>')


def badge(text: str, css: str) -> str:
    return f'<span class="ft-badge {css}">{e(text)}</span>'


def bullet_list(items, css: str = "") -> str:
    return f'<ul class="ft-list {css}">' + "".join(f"<li>{e(i)}</li>" for i in items) + "</ul>"


def steps_html(steps) -> str:
    out = []
    for s in steps:
        tip = f'<div class="ft-tip">✓ {e(s.verification_tip)}</div>' if s.verification_tip else ""
        out.append(
            f'<div class="ft-step"><div class="ft-step-n">{s.step_number}</div><div>'
            f'<div class="ft-step-title">{e(s.action_title)}</div>'
            f'<div class="ft-step-sys">{e(s.target_system)}</div>'
            f'<div class="ft-step-text">{e(s.detailed_instruction)}</div>{tip}</div></div>')
    return "".join(out)


def area_html(area) -> str:
    details = bullet_list(area.details, "ft-send") if area.details else ""
    return (f'<div class="ft-area"><div class="ft-area-key">{e(area.key)}</div><div>'
            f'<div class="ft-section" style="margin:0">Onde resolver</div>'
            f'<div class="ft-area-label">{e(area.label)}</div>'
            f'<p>{e(area.who)}</p><p style="opacity:.8">{e(area.action)}</p>{details}</div></div>')


def alt_area_html(area) -> str:
    return (f'<div class="ft-alt"><span style="opacity:.75">Também pode ser</span> <b>{e(area.label)}</b>: {e(area.who)} '
            f'<span style="opacity:.75">{e(area.action)}</span></div>')
