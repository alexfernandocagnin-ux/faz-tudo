"""Faz Tudo — versão web (Streamlit) do solucionador de faturamento SIA/SUS.

Rodar localmente (na pasta do projeto):   streamlit run web/app.py
Streamlit Community Cloud: arquivo principal = web/app.py (as dependências estão em web/requirements.txt).

Privacidade: os arquivos enviados são lidos só em memória, para aquela sessão, e não são gravados.
"""

from __future__ import annotations

import hashlib
import sys
from pathlib import Path

import streamlit as st

sys.path.insert(0, str(Path(__file__).resolve().parent))

from faztudo import SAMPLES_DIR  # noqa: E402
from faztudo.bpa import analyze, findings_to_csv, parse_bpa  # noqa: E402
from faztudo.consistency import ConsistencySolver, decode_report, parse_report  # noqa: E402
from faztudo.knowledge import load_knowledge  # noqa: E402
from faztudo.sigtap import SigtapRepository, default_repository  # noqa: E402
from faztudo.text import format_brl  # noqa: E402
from ui import (GROUP_CLASS, SEVERITY_CLASS, alt_area_html, area_html, badge, bullet_list, e, html,  # noqa: E402
                inject_css, page_header, steps_html)

st.set_page_config(page_title="Faz Tudo · SIA/SUS", page_icon="🩺", layout="wide")
inject_css()

KB = load_knowledge()


# ---------------------------------------------------------------- estado

@st.cache_resource(show_spinner=False)
def _default_sigtap() -> SigtapRepository:
    return default_repository()


@st.cache_resource(show_spinner=False, max_entries=4)
def _sigtap_from_zip(digest: str, data: bytes) -> SigtapRepository:  # noqa: ARG001 (digest = chave do cache)
    return SigtapRepository.from_zip(data)


def sigtap() -> SigtapRepository:
    return st.session_state.get("sigtap") or _default_sigtap()


def open_knowledge(entry_id: str) -> None:
    st.session_state["kb_focus"] = entry_id
    st.switch_page(PAGES["base"])


# ---------------------------------------------------------------- Solucionador SIA

def page_solver() -> None:
    page_header("Solucionador SIA",
                "Mande o relatório de ocorrências da consistência do SIA (R0350.OUT) e eu digo, problema por problema, "
                "onde se resolve, para quem mandar e como corrigir.")

    col_up, col_demo = st.columns([4, 1.3], vertical_alignment="bottom")
    with col_up:
        upload = st.file_uploader("Relatório do SIA (R0350.OUT)", type=["out", "txt"], key="r0350",
                                  help="Fica na pasta do processamento, ex.: C:\\Datasus\\SIA\\SIA_2026_10_Processamento\\R0350.OUT")
    with col_demo:
        if st.button("Testar com o exemplo", icon=":material/science:", use_container_width=True):
            st.session_state["solver_demo"] = True

    if upload is not None:
        data, name = upload.getvalue(), upload.name
        st.session_state["solver_demo"] = False
    elif st.session_state.get("solver_demo"):
        sample = SAMPLES_DIR / "R0350-exemplo.OUT"
        data, name = sample.read_bytes(), sample.name
    else:
        html('<div class="ft-card"><div class="ft-section" style="margin-top:0">O que você recebe</div>'
             + bullet_list([
                 "Cada ocorrência explicada em português claro.",
                 "Onde resolver — BPA, APAC, CNES, FPO ou aqui no SIA — e para qual estabelecimento mandar.",
                 "O que dá para concluir das linhas do relatório e das regras do SIGTAP (faixa de idade, código certo, APACs sobrepostas…).",
                 "Passo a passo para corrigir, e um texto pronto para colar no e-mail ou WhatsApp.",
             ]) + '</div>')
        return

    try:
        report = parse_report(decode_report(data), name)
    except ValueError as exc:
        st.error(str(exc), icon=":material/error:")
        return

    result = ConsistencySolver(sigtap(), KB).solve(report)

    meta = " · ".join(x for x in [report.source_name, report.orgao, f"competência {report.competencia}",
                                   report.bdsia_version, f"gerado em {report.generated_at}" if report.generated_at else ""] if x)
    groups: dict[str, int] = {}
    for p in result.problems:
        groups[p.group] = groups.get(p.group, 0) + p.quantity
    pills = " ".join(badge(f"{g}: {n} ocorrência(s)", GROUP_CLASS.get(g, "ft-gray")) for g, n in groups.items())
    html(f'<div class="ft-card"><div class="ft-meta">{e(meta)}</div>'
         f'<div class="ft-headline">{e(result.headline)}</div><div class="ft-row">{pills}</div></div>')

    for note in result.notes:
        st.info(note, icon=":material/info:")

    text = result.to_plain_text()
    c1, c2 = st.columns([1, 1])
    c1.download_button("Baixar resposta (.txt)", text, file_name=f"resposta-{Path(name).stem}.txt",
                       icon=":material/download:", use_container_width=True)
    with c2.popover("Texto para copiar", icon=":material/content_copy:", use_container_width=True):
        st.caption("Use o botão de copiar no canto do quadro.")
        st.code(text, language=None, wrap_lines=True)

    for i, p in enumerate(result.problems):
        with st.container(border=True):
            css = GROUP_CLASS.get(p.group, "ft-gray")
            html(f'<div class="ft-row">{badge(p.code, css)}<span class="ft-meta">{e(p.group_meaning)}</span>'
                 f'<span class="ft-spacer"></span><span class="ft-qty">{e(p.quantity_display)}</span></div>'
                 f'<div class="ft-title">{e(p.title)}</div><div class="ft-sia">No SIA: {e(p.description)}</div>')
            if p.main_area:
                html(area_html(p.main_area))
            for alt in p.areas[1:]:
                html(alt_area_html(alt))
            html('<div class="ft-section">O que eu vi no seu relatório</div>' + bullet_list(p.findings))
            if p.steps:
                html('<div class="ft-section">Como resolver</div>' + steps_html(p.steps))
            if p.knowledge and st.button("Ver na Base de Erros", key=f"kb-{i}", icon=":material/menu_book:", type="tertiary"):
                open_knowledge(p.knowledge.id)


# ---------------------------------------------------------------- Base de Erros

def page_knowledge() -> None:
    page_header("Base de Erros", f"{len(KB.entries)} erros de faturamento com causa, onde resolver e passo a passo.")
    focus = st.session_state.pop("kb_focus", None)

    query = st.text_input("Buscar", placeholder="Código, sintoma ou sistema — ex.: 004, cbo, firebird, sem orçamento",
                          label_visibility="collapsed", value="" if focus else None)
    layer = st.pills("Camada", KB.layers, selection_mode="single", label_visibility="collapsed")
    entries = KB.search(query, layer)
    if focus:
        entries = [x for x in KB.entries if x.id == focus] + [x for x in entries if x.id != focus]
    if not entries:
        st.info("Nada encontrado. Tente outro termo ou limpe o filtro.")
        return

    st.caption(f"{len(entries)} resultado(s)")
    for entry in entries:
        area = KB.areas.get(entry.area if entry.area != "PRODUCAO" else "BPA")
        area_label = "BPA ou APAC (produção)" if entry.area == "PRODUCAO" else (area.label if area else entry.area)
        with st.expander(f"**{entry.error_code}** — {entry.title}", expanded=entry.id == focus):
            html(f'<div class="ft-row">{badge(entry.severity_display, SEVERITY_CLASS.get(entry.severity, "ft-gray"))}'
                 f'{badge("Onde resolver: " + area_label, "ft-teal")}<span class="ft-meta">{e(entry.layer)} · '
                 f'{e(entry.affected_systems)}</span></div>')
            a, b = st.columns(2)
            a.markdown("**O que aparece**")
            a.write(entry.symptom)
            b.markdown("**Por que acontece**")
            b.write(entry.root_cause)
            html(f'<div class="ft-impact"><b>Impacto financeiro:</b> {e(entry.financial_impact_note)}</div>'
                 f'<div class="ft-section">Como resolver</div>{steps_html(entry.guided_steps)}')


# ---------------------------------------------------------------- Consultor SIGTAP

def page_sigtap() -> None:
    page_header("Consultor SIGTAP", "Regras de cada procedimento: instrumento, CBOs aceitos, sexo, faixa etária, CIDs e valor.")
    repo = sigtap()

    if repo.is_official:
        st.success(f"{repo.label}.", icon=":material/check_circle:")
    else:
        st.warning("Você está vendo a base de exemplo (poucos procedimentos, valores ilustrativos). Baixe o zip da competência "
                   "em sigtap.datasus.gov.br (Tabela Unificada) e envie abaixo.", icon=":material/warning:")

    with st.expander("Importar a Tabela Unificada do SIGTAP (zip)", expanded=not repo.is_official):
        up = st.file_uploader("Zip da Tabela Unificada", type=["zip"], key="sigtap_zip", label_visibility="collapsed")
        if up is not None:
            data = up.getvalue()
            try:
                with st.spinner("Lendo a tabela…"):
                    st.session_state["sigtap"] = _sigtap_from_zip(hashlib.sha1(data).hexdigest(), data)
                st.rerun()
            except ValueError as exc:
                st.error(str(exc))
        st.caption("A tabela importada vale para a sua sessão. Para o app já abrir sempre com a tabela oficial, quem cuida "
                   "do projeto roda `python web/tools/preparar_sigtap.py TabelaUnificada_AAAAMM.zip` e publica o arquivo gerado.")

    c1, c2 = st.columns([1, 1], vertical_alignment="bottom")
    query = c1.text_input("Buscar procedimento", placeholder="Código (com ou sem pontos), nome, CBO ou financiamento")
    instrument = c2.segmented_control("Instrumento", ["Todos", "BPA-C", "BPA-I", "APAC", "RAAS", "AIH"], default="Todos")
    results = repo.search(query, instrument or "Todos")
    st.caption(f"{len(results)} de {len(repo.procedures)} procedimentos" +
               (" (mostrando os primeiros 250 — refine a busca)" if len(results) >= 250 else ""))
    st.dataframe(
        [{"Código": r.formatted_code, "Procedimento": r.name, "Instrumento": r.instruments_display, "Sexo": r.sex_display,
          "Idade": r.age_range_display, "CBOs aceitos": r.cbo_summary, "CIDs": len(r.allowed_cids) or "—",
          "Qtd. máx.": r.max_quantity or "—", "Valor SA": format_brl(r.unit_value), "Financiamento": r.financing}
         for r in results],
        use_container_width=True, hide_index=True,
        column_config={"Procedimento": st.column_config.TextColumn(width="large")})


# ---------------------------------------------------------------- Triador BPA

def page_triador() -> None:
    page_header("Triador BPA", "Descubra antes do envio quais linhas da remessa o SIA vai rejeitar — e quanto isso custa.")
    col_up, col_demo = st.columns([4, 1.3], vertical_alignment="bottom")
    upload = col_up.file_uploader("Remessa do BPA-Mag (PA*.* ou .txt)", key="bpa")
    if col_demo.button("Testar com o exemplo", icon=":material/science:", use_container_width=True, key="bpa_demo_button"):
        st.session_state["bpa_demo"] = True

    if upload is not None:
        data, name = upload.getvalue(), upload.name
    elif st.session_state.get("bpa_demo"):
        sample = SAMPLES_DIR / "remessa-bpa-exemplo.txt"
        data, name = sample.read_bytes(), sample.name
    else:
        st.caption("Verifica: CNS do paciente e do profissional · CBO × procedimento · sexo · faixa etária · instrumento "
                   "(BPA-C/BPA-I) · CID · raça/cor e etnia · competência.")
        return

    summary = analyze(parse_bpa(data.decode("latin-1"), name), sigtap(), KB)
    st.caption(f"{summary.source_name} · {summary.detected_format} · competência {summary.competencia} · valores pela {sigtap().label}")

    total_value = summary.glosa_value + summary.approved_value
    m1, m2, m3, m4 = st.columns(4)
    m1.metric("Linhas avaliadas", summary.total_records, border=True)
    m2.metric("Sem crítica", summary.valid_records, border=True,
              delta=f"{summary.valid_records / summary.total_records:.0%} da remessa" if summary.total_records else None,
              delta_color="off")
    m3.metric("Críticas", len(summary.findings), border=True, delta=f"{len(summary.groups)} causa(s)", delta_color="off")
    m4.metric("Glosa estimada", format_brl(summary.glosa_value), border=True,
              delta=f"{summary.glosa_value / total_value:.0%} do valor" if total_value else None, delta_color="inverse")

    for w in summary.warnings:
        st.warning(w, icon=":material/warning:")
    if not summary.findings:
        st.success("Nenhuma crítica encontrada. Todas as linhas passaram nas verificações.", icon=":material/check_circle:")
        return

    st.download_button("Exportar para Excel (.csv)", findings_to_csv(summary, KB), file_name=f"criticas-{Path(name).stem}.csv",
                       mime="text/csv", icon=":material/download:")
    tab_groups, tab_lines = st.tabs([f"Causas raiz ({len(summary.groups)})", f"Linhas com crítica ({len(summary.findings)})"])
    with tab_groups:
        top = max((g.impact for g in summary.groups), default=0) or 1
        for g in summary.groups:
            with st.container(border=True):
                left, right = st.columns([3, 1])
                left.markdown(f'{badge(g.error_code, "ft-red")} &nbsp;**{e(g.detail)}**', unsafe_allow_html=True)
                left.markdown(f"**{g.title}**  \n{g.procedure}")
                if g.recommended_action:
                    left.caption(f"→ {g.recommended_action}")
                right.metric("Impacto", format_brl(g.impact), f"{g.occurrences} linha(s)", delta_color="off")
                right.progress(float(g.impact / top))
    with tab_lines:
        st.dataframe(
            [{"Linha": f.record.line_number, "Crítica": f.code, "Instr.": f.record.instrument,
              "Procedimento": f.record.procedure_code, "CBO": f.record.cbo,
              "Referência": f"Folha {f.record.folha}/{f.record.sequencia}" +
                            (f" · {f.record.patient_name}" if f.record.individual and f.record.patient_name else ""),
              "O que está errado": f.description,
              "Valor": format_brl(summary.line_values.get(f.record.line_number, 0))}
             for f in summary.findings],
            use_container_width=True, hide_index=True,
            column_config={"O que está errado": st.column_config.TextColumn(width="large")})


# ---------------------------------------------------------------- navegação

PAGES = {
    "solver": st.Page(page_solver, title="Solucionador SIA", icon=":material/lightbulb:", default=True, url_path="solucionador"),
    "base": st.Page(page_knowledge, title="Base de Erros", icon=":material/menu_book:", url_path="base-de-erros"),
    "sigtap": st.Page(page_sigtap, title="Consultor SIGTAP", icon=":material/table_view:", url_path="sigtap"),
    "triador": st.Page(page_triador, title="Triador BPA", icon=":material/fact_check:", url_path="triador"),
}

st.logo(str(Path(__file__).resolve().parent / "assets" / "logo.svg"), size="large")
nav = st.navigation(list(PAGES.values()))

with st.sidebar:
    st.divider()
    repo = sigtap()
    if repo.is_official:
        st.caption(f":material/check_circle: **Tabela SIGTAP:** {repo.label}")
    else:
        st.caption(":material/warning: **Tabela SIGTAP:** base de exemplo — importe a oficial no Consultor SIGTAP.")
    st.caption(":material/lock: Os arquivos enviados são lidos só em memória e não ficam salvos.")
    st.caption(":material/computer: O Doutor TI (checagem do Windows) existe só no programa desktop.")

nav.run()
