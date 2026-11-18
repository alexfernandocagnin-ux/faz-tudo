"""Conserta SIA · CPD — versão web (Streamlit) do solucionador de faturamento SIA/SUS.

Rodar localmente (na pasta do projeto):   streamlit run web/app.py
Streamlit Community Cloud: arquivo principal = web/app.py (as dependências estão em web/requirements.txt).

Privacidade: os arquivos de produção enviados (relatório do SIA, remessa BPA) são lidos só em memória e não são
gravados. As únicas coisas guardadas são as tabelas públicas do SIGTAP.
"""

from __future__ import annotations

import sys
from pathlib import Path

import streamlit as st

sys.path.insert(0, str(Path(__file__).resolve().parent))

from faztudo import doctor, sigtap_remote  # noqa: E402
from faztudo.bpa import analyze, findings_to_csv, parse_bpa  # noqa: E402
from faztudo.consistency import ConsistencySolver, decode_report, parse_report  # noqa: E402
from faztudo.knowledge import load_knowledge  # noqa: E402
from faztudo.sigtap import SigtapRepository  # noqa: E402
from faztudo.sigtap_store import SigtapStore  # noqa: E402
from faztudo.text import format_brl  # noqa: E402
from ui import (GROUP_CLASS, SEVERITY_CLASS, alt_area_html, area_html, badge, bullet_list, e, html,  # noqa: E402
                inject_css, page_header, steps_html)

APP_NAME = "Conserta SIA · CPD"
st.set_page_config(page_title=APP_NAME, page_icon="🩺", layout="wide")
inject_css()

KB = load_knowledge()


# ---------------------------------------------------------------- tabelas SIGTAP (compartilhadas por todos)

@st.cache_resource(show_spinner=False)
def store() -> SigtapStore:
    return SigtapStore()


@st.cache_data(ttl=6 * 3600, show_spinner=False)
def remote_tables() -> list[sigtap_remote.RemoteTable] | None:
    """Tabelas publicadas no FTP do DATASUS (consulta a cada 6 horas). None = DATASUS fora do ar/inacessível."""
    try:
        return sigtap_remote.list_remote()
    except Exception:  # noqa: BLE001 — FTP fora do ar, timeout, bloqueio de rede
        return None


@st.cache_data(ttl=6 * 3600, show_spinner=False)
def auto_download() -> list[str]:
    """Baixa sozinho as tabelas mais novas que a mais recente guardada. NÃO troca a tabela em uso."""
    remote = remote_tables() or []
    stored = store().tables()
    newest = max(stored, default="")
    fetched = []
    for table in remote:
        if table.competencia > newest or (table.competencia in stored and stored[table.competencia].version
                                          and table.version > stored[table.competencia].version):
            try:
                store().download(table, sigtap_remote.download)
                fetched.append(table.competencia)
            except Exception:  # noqa: BLE001
                pass
    return fetched


def sigtap() -> SigtapRepository:
    return store().current()


def can_manage_tables() -> bool:
    """Se houver uma senha em st.secrets["senha_cpd"], só quem digitou pode trocar a tabela em uso."""
    try:
        password = st.secrets.get("senha_cpd", "")
    except Exception:  # noqa: BLE001 — sem secrets configurado
        password = ""
    return not password or st.session_state.get("cpd_ok", False)


def open_knowledge(entry_id: str) -> None:
    st.session_state["kb_focus"] = entry_id
    st.switch_page(PAGES["base"])


# ---------------------------------------------------------------- Solucionador SIA

def page_solver() -> None:
    page_header("Solucionador SIA",
                "Mande o relatório de ocorrências da consistência do SIA (R0350.OUT) e eu digo, problema por problema, "
                "onde se resolve, para quem mandar e como corrigir.")

    upload = st.file_uploader("Relatório do SIA (R0350.OUT)", type=["out", "txt"], key="r0350",
                              help="Fica na pasta do processamento, ex.: C:\\Datasus\\SIA\\SIA_2026_10_Processamento\\R0350.OUT")

    if upload is not None:
        data, name = upload.getvalue(), upload.name
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
    sigtap_tables_panel()
    repo = sigtap()

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


def sigtap_tables_panel() -> None:
    """Tabela em uso + tabelas do DATASUS: baixar e escolher qual vale para todo mundo."""
    s = store()
    repo = sigtap()
    if repo.is_official:
        version = f" · versão {repo.version}" if repo.version else ""
        st.success(f"**Tabela em uso: competência {repo.competencia_display}**{version} — vale para todos que usam o site.",
                   icon=":material/check_circle:")
    else:
        st.warning("Nenhuma tabela oficial em uso — usando a base de exemplo. Baixe uma competência abaixo e clique em "
                   "**Usar esta**.", icon=":material/warning:")

    remote = remote_tables()
    stored = s.tables()
    newer = [t for t in (remote or []) if t.competencia > (repo.competencia or "")]
    with st.expander("Tabelas do SIGTAP — baixar e escolher qual usar", expanded=bool(newer) or not repo.is_official,
                     icon=":material/table_view:"):
        if remote is None:
            st.info("Não consegui falar com o FTP do DATASUS agora. Você pode tentar de novo mais tarde ou enviar o zip abaixo.",
                    icon=":material/cloud_off:")

        manage = can_manage_tables()
        if not manage:
            pwd = st.text_input("Senha do CPD para trocar a tabela em uso", type="password")
            if pwd:
                if pwd == st.secrets.get("senha_cpd"):
                    st.session_state["cpd_ok"] = True
                    st.rerun()
                st.error("Senha incorreta.")

        # As 12 competências mais recentes do DATASUS + qualquer uma já guardada.
        recent = {t.competencia for t in (remote or [])[:12]}
        competencias = sorted(recent | set(stored), reverse=True)
        remote_by_comp = {t.competencia: t for t in (remote or [])}
        header = st.columns([1.2, 1.6, 2.2, 1.4, 1.4])
        for col, title in zip(header, ["Competência", "Versão", "Situação"]):
            col.markdown(f"**{title}**")
        for comp in competencias:
            r, t = remote_by_comp.get(comp), stored.get(comp)
            in_use = comp == repo.competencia and repo.is_official
            outdated = t is not None and r is not None and t.version and r.version > t.version
            if in_use:
                status = ":green[**● Em uso**]"
            elif t is not None:
                status = "Baixada" + (" (há versão mais nova)" if outdated else "")
            else:
                status = ":gray[Disponível no DATASUS]"
            cols = st.columns([1.2, 1.6, 2.2, 1.4, 1.4], vertical_alignment="center")
            cols[0].write(f"{comp[4:]}/{comp[:4]}")
            cols[1].caption((t.version if t and t.version else (r.version if r else "—")))
            cols[2].markdown(status)
            if (t is None or outdated) and r is not None:
                if cols[3].button("Baixar", key=f"dl-{comp}", icon=":material/download:", use_container_width=True):
                    with st.spinner(f"Baixando {r.filename} do DATASUS…"):
                        try:
                            s.download(r, sigtap_remote.download)
                        except Exception as exc:  # noqa: BLE001
                            st.error(f"Não consegui baixar: {exc}")
                    st.rerun()
            if t is not None and not in_use:
                if cols[4].button("Usar esta", key=f"use-{comp}", type="primary", icon=":material/check:",
                                  use_container_width=True, disabled=not manage):
                    s.use(comp)
                    st.rerun()

        st.divider()
        up = st.file_uploader("Ou envie o zip da Tabela Unificada", type=["zip"], key="sigtap_zip")
        if up is not None and st.session_state.get("sigtap_zip_done") != up.file_id:
            try:
                with st.spinner("Lendo a tabela…"):
                    saved = s.add_zip(up.getvalue())
                st.session_state["sigtap_zip_done"] = up.file_id
                st.success(f"Tabela {saved.competencia_display} guardada. Clique em **Usar esta** para colocá-la em uso.")
                st.rerun()
            except ValueError as exc:
                st.error(str(exc))
        st.caption("O site procura tabelas novas no DATASUS a cada 6 horas e baixa sozinho, mas só troca a tabela em uso "
                   "quando alguém clica em **Usar esta**. Se o servidor reiniciar, volta para a tabela padrão do projeto "
                   f"({s.project_dir.name}/em-uso.json).")


# ---------------------------------------------------------------- Triador BPA

def page_triador() -> None:
    page_header("Triador BPA", "Descubra antes do envio quais linhas da remessa o SIA vai rejeitar — e quanto isso custa.")
    upload = st.file_uploader("Remessa do BPA-Mag (PA*.* ou .txt)", key="bpa")

    if upload is not None:
        data, name = upload.getvalue(), upload.name
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


# ---------------------------------------------------------------- Doutor TI

DOCTOR_SCRIPT = (Path(__file__).resolve().parent / "assets" / "doutor-ti.ps1").read_text(encoding="utf-8-sig")
DOCTOR_CLASS = {"critico": "ft-red", "atencao": "ft-amber", "info": "ft-gray", "ok": "ft-teal"}


def page_doctor() -> None:
    page_header("Doutor TI DATASUS",
                "Examina o computador onde rodam SIA, BPA-Mag, APAC-Mag e SCNES: VirtualStore, Firebird (porta 3050), "
                "midas.dll, BDE e espaço em disco.")

    left, right = st.columns(2, gap="large")
    with left:
        html('<div class="ft-section" style="margin-top:0">Passo 1 — no computador do SIA</div>')
        st.markdown("Abra o **PowerShell** (menu Iniciar → digite *PowerShell*), **cole** o script abaixo "
                    "(botão direito do mouse) e aperte **Enter**. Ele **só lê**: não altera nem instala nada.")
        st.code(DOCTOR_SCRIPT, language="powershell", height=220)
        st.download_button("Ou baixe o script (.ps1)", DOCTOR_SCRIPT.encode("utf-8-sig"), file_name="doutor-ti.ps1",
                           icon=":material/download:")
    with right:
        html('<div class="ft-section" style="margin-top:0">Passo 2 — envie o resultado</div>')
        st.markdown("O script cria o arquivo **doutor-ti-resultado.json** na Área de Trabalho. Envie aqui:")
        upload = st.file_uploader("Resultado do Doutor TI", type=["json"], key="doctor", label_visibility="collapsed")

    if upload is None:
        return
    try:
        result = doctor.load_result(upload.getvalue())
    except ValueError as exc:
        st.error(str(exc), icon=":material/error:")
        return

    items = doctor.analyze(result)
    st.divider()
    st.caption(f"{result.get('windows', 'Windows')} · examinado em {str(result.get('gerado_em', '')).replace('T', ' às ')}")
    counts = {k: sum(i.status == k for i in items) for k in ("ok", "atencao", "critico", "info")}
    m = st.columns(4)
    m[0].metric("Tudo certo", counts["ok"], border=True)
    m[1].metric("Pedem atenção", counts["atencao"], border=True)
    m[2].metric("Críticos", counts["critico"], border=True)
    m[3].metric("Informativos", counts["info"], border=True)

    for i, item in enumerate(items):
        with st.container(border=True):
            html(f'<div class="ft-row">{badge(item.status_display, DOCTOR_CLASS[item.status])}'
                 f'<span class="ft-meta">{e(item.category)}</span></div>'
                 f'<div class="ft-title" style="font-size:1.08rem">{e(item.title)}</div>'
                 f'<div style="margin-top:4px">{e(item.detected)}</div>'
                 f'<div style="opacity:.78;margin-top:4px">{e(item.recommendation)}</div>')
            if item.knowledge_id and st.button("Ver solução na Base de Erros", key=f"doc-kb-{i}",
                                               icon=":material/menu_book:", type="tertiary"):
                open_knowledge(item.knowledge_id)


# ---------------------------------------------------------------- navegação

PAGES = {
    "solver": st.Page(page_solver, title="Solucionador SIA", icon=":material/lightbulb:", default=True, url_path="solucionador"),
    "base": st.Page(page_knowledge, title="Base de Erros", icon=":material/menu_book:", url_path="base-de-erros"),
    "sigtap": st.Page(page_sigtap, title="Consultor SIGTAP", icon=":material/table_view:", url_path="sigtap"),
    "triador": st.Page(page_triador, title="Triador BPA", icon=":material/fact_check:", url_path="triador"),
    "doctor": st.Page(page_doctor, title="Doutor TI DATASUS", icon=":material/build:", url_path="doutor-ti"),
}

st.logo(str(Path(__file__).resolve().parent / "assets" / "logo.svg"), size="large")
nav = st.navigation(list(PAGES.values()))

auto_download()  # baixa tabelas novas do DATASUS em segundo plano (no máximo a cada 6 h); não troca a em uso

def sidebar_sigtap_switch() -> None:
    """Troca rápida da tabela SIGTAP em uso (vale para todo mundo)."""
    s = store()
    repo = sigtap()
    stored = s.tables()
    remote = {t.competencia: t for t in (remote_tables() or [])[:12]}
    options = sorted(set(stored) | set(remote), reverse=True)
    if not options:
        st.caption(":material/warning: Sem tabela SIGTAP oficial — o DATASUS não respondeu.")
        return
    current = repo.competencia if repo.is_official and repo.competencia in options else None

    def label(comp: str) -> str:
        text = f"{comp[4:]}/{comp[:4]}"
        if comp not in stored:
            text += " · baixar do DATASUS"
        return text

    manage = can_manage_tables()
    choice = st.selectbox("Tabela SIGTAP em uso", options, index=options.index(current) if current else None,
                          format_func=label, placeholder="Escolha a competência", disabled=not manage,
                          # A chave muda quando a tabela em uso muda: assim a caixa de quem está com o site aberto
                          # nunca "desfaz" a troca feita por outra pessoa.
                          key=f"sigtap-switch-{current}",
                          help="Vale para todos que usam o site. Tabelas novas do DATASUS são baixadas sozinhas, "
                               "mas só entram em uso quando alguém escolhe aqui."
                               + ("" if manage else " Para trocar, digite a senha do CPD no Consultor SIGTAP."))
    if choice and choice != current:
        if choice not in stored:
            with st.spinner(f"Baixando a tabela {label(choice)[:7]} do DATASUS…"):
                try:
                    s.download(remote[choice], sigtap_remote.download)
                except Exception as exc:  # noqa: BLE001
                    st.error(f"Não consegui baixar: {exc}")
                    return
        s.use(choice)
        st.toast(f"Tabela SIGTAP {choice[4:]}/{choice[:4]} em uso para todos.", icon=":material/check_circle:")
        st.rerun()
    newer = [c for c in stored if repo.competencia and c > repo.competencia]
    if newer:
        st.caption(f":material/new_releases: A tabela **{newer[0][4:]}/{newer[0][:4]}** já foi baixada e está pronta para uso.")


with st.sidebar:
    st.divider()
    sidebar_sigtap_switch()
    st.caption(":material/lock: Relatórios e remessas enviados são lidos só em memória e não ficam salvos.")

nav.run()
