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
import streamlit.components.v1 as components

sys.path.insert(0, str(Path(__file__).resolve().parent))


def _reload_changed_modules() -> None:
    """Ao atualizar o site, o Streamlit Cloud reexecuta o app.py mas mantém os módulos antigos na memória
    (foi o que causou um ImportError). Se algum arquivo do motor mudou, descarta a versão carregada."""
    base = Path(__file__).resolve().parent
    files = sorted([*base.glob("faztudo/*.py"), base / "ui.py"])
    stamp = tuple((f.name, f.stat().st_mtime) for f in files if f.exists())
    if getattr(sys, "_conserta_sia_stamp", None) != stamp:
        for name in [n for n in sys.modules if n == "ui" or n == "faztudo" or n.startswith("faztudo.")]:
            del sys.modules[name]
        sys._conserta_sia_stamp = stamp


_reload_changed_modules()

from faztudo import sigtap_remote  # noqa: E402
from faztudo.bpa import analyze, findings_to_csv, parse_bpa  # noqa: E402
from faztudo.consistency import (COMPAT_OBRIGATORIA, ConsistencySolver, decode_report,  # noqa: E402
                                 looks_like_apac_mirror,
                                 looks_like_consistency_report, parse_apac_mirror, parse_report)
from faztudo.knowledge import load_knowledge  # noqa: E402
from faztudo.sigtap import SigtapRepository, title_pt  # noqa: E402
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

OWNER_TITLE = {
    "BPA": "Mandar para a unidade",
    "APAC": "Mandar para o prestador",
    "CNES": "Setor do CNES",
    "FPO": "Programação (FPO Magnético)",
    "SIA": "Aqui no SIA (CPD)",
}


def page_solver() -> None:
    page_header("Solucionador SIA",
                "Envie o relatório de erros da consistência do SIA e receba, pronto, o que cada estabelecimento precisa "
                "corrigir — com a mensagem para mandar.")

    uploads = st.file_uploader(
        "Relatórios do SIA (pode selecionar vários de uma vez)", accept_multiple_files=True, key="r0350",
        help="R0350.OUT = ocorrências da consistência (obrigatório). Espelhos das APACs (opcional: com eles eu digo "
             "exatamente qual procedimento obrigatório faltou) = R0330T.OUT ou os arquivos \"<nº da APAC> apac.<mês>\" "
             "(ex.: 3526702073280 apac.AGO). Ficam na pasta do processamento, ex.: "
             "C:\\Datasus\\SIA\\SIA_2026_10_Processamento. Eu reconheço cada arquivo pelo conteúdo.")

    reports, mirrors = [], []
    for up in uploads or []:
        text = decode_report(up.getvalue())
        if looks_like_consistency_report(text):
            reports.append((up.name, text))
        elif looks_like_apac_mirror(text):
            mirrors += parse_apac_mirror(text)
        else:
            st.warning(f"**{up.name}** não é o R0350 (ocorrências da consistência) nem espelho de APAC. Ignorei.",
                       icon=":material/warning:")

    if not reports:
        if mirrors:
            st.info("Recebi o espelho das APACs. Envie também o **R0350.OUT** (ocorrências da consistência).",
                    icon=":material/info:")
        html('<div class="ft-card"><div class="ft-section" style="margin-top:0">Como usar</div>'
             + bullet_list([
                 "Arraste o R0350.OUT da pasta do processamento do SIA. Se tiver erro de APAC, arraste junto os espelhos "
                 "(R0330T.OUT ou os arquivos \"<nº da APAC> apac.<mês>\"): pode selecionar todos de uma vez.",
                 "Você recebe a lista do que fazer separada por estabelecimento, já com o motivo e a correção.",
                 "Cada estabelecimento tem uma mensagem pronta: é só copiar e mandar no e-mail ou WhatsApp.",
             ]) + '</div>')
        return

    name, text = reports[0]
    report = parse_report(text, name)
    result = ConsistencySolver(sigtap(), KB).solve(report, mirrors)
    if mirrors:
        have = {m.number for m in mirrors}
        need = sorted({r for p in result.problems if p.code == "ERRo" for a in p.actions for r in a.refs})
        missing = [n for n in need if n not in have]
        if missing:
            st.info("Ainda sem espelho para: " + ", ".join(f"**{n}**" for n in missing)
                    + ". Envie os arquivos dessas APACs junto para eu dizer exatamente o que falta.", icon=":material/info:")

    # ---------- Filtro: BPA (BPA Magnético) fica escondido por padrão ----------
    meta = " · ".join(x for x in [report.source_name, report.orgao, f"competência {report.competencia}",
                                   f"tabela SIGTAP {sigtap().competencia_display}" if sigtap().is_official else "",
                                   f"espelho com {len(mirrors)} APAC(s)" if mirrors else ""] if x)
    st.caption(meta)

    def is_bpa(p) -> bool:
        return bool(p.main_area) and p.main_area.key == "BPA"

    n_bpa = sum(p.quantity for p in result.problems if is_bpa(p))
    n_other = result.total_occurrences - n_bpa
    options = {f"APAC e outros ({n_other})": "outros", f"Tudo ({result.total_occurrences})": "tudo",
               f"Só BPA ({n_bpa})": "bpa"}
    chosen = st.segmented_control(
        "Mostrar", list(options), default=list(options)[0], key=f"solver-filter-{n_other}-{n_bpa}",
        help="Os erros de BPA o BPA Magnético corrige sozinho, por isso ficam escondidos por padrão.")
    scope = options.get(chosen or list(options)[0], "outros")

    def visible(bpa: bool) -> bool:
        return scope == "tudo" or (scope == "bpa") == bpa

    problems = [p for p in result.problems if visible(is_bpa(p))]
    owners = [g for g in result.owners if visible(g.area_key == "BPA")]
    owners.sort(key=lambda g: (g.area_key == "BPA", not g.cnes))  # estabelecimentos primeiro; "Outros" e BPA no fim

    m = st.columns(4)
    m[0].metric("Ocorrências", sum(p.quantity for p in problems), border=True)
    m[1].metric("Estabelecimentos", len({g.cnes for g in owners if g.cnes}), border=True)
    m[2].metric("Tipos de erro", len(problems), border=True)
    m[3].metric("Impedem aprovação", sum(p.quantity for p in problems if p.is_blocking), border=True)
    if scope == "outros" and n_bpa:
        st.caption(f":material/visibility_off: {n_bpa} ocorrência(s) de BPA escondida(s) — o BPA Magnético corrige sozinho. "
                   "Para ver, escolha \"Tudo\" ou \"Só BPA\".")
    for note in result.notes[:-1]:
        st.info(note, icon=":material/info:")

    if not owners:
        st.success("Nada para corrigir aqui. " + ("Só há erros de BPA, que o BPA Magnético corrige sozinho." if n_bpa else ""),
                   icon=":material/check_circle:")
        return

    # ---------- Uma aba por responsável + detalhes ----------
    def tab_label(g) -> str:
        name = title_pt(g.cnes_name) if g.cnes_name else "Outros"
        prefix = "BPA · " if g.area_key == "BPA" and scope == "tudo" else ""
        name = prefix + name
        return f"{name[:32] + ('…' if len(name) > 32 else '')} ({len(g.items)})"

    tabs = st.tabs([tab_label(g) for g in owners] + ["Detalhes técnicos"])
    for tab, g in zip(tabs, owners):
        with tab:
            owner_panel(g)
    with tabs[-1]:
        technical_details(result, name, problems)


def owner_panel(g) -> None:
    """Tudo o que um estabelecimento precisa corrigir: cabeçalho, mensagem pronta e um cartão por erro."""
    if not g.cnes:
        st.caption("Estes códigos aparecem no quadro de totais do relatório, mas o SIA não listou em qual estabelecimento. "
                   "Confira no SIA (relatório completo da consistência) a quem pertencem.")
    head, action = st.columns([3, 1.2], vertical_alignment="center")
    with head:
        where = f"CNES {g.cnes}" if g.cnes else ""
        html(f'<div class="ft-row"><div class="ft-area-key" style="width:46px;height:46px;font-size:.8rem">{e(g.area_key)}</div>'
             f'<div><div class="ft-section" style="margin:0">{e(OWNER_TITLE.get(g.area_key, g.area_label))}</div>'
             f'<div style="font-weight:700;font-size:1.05rem">{e(g.cnes_name or g.area_label)}</div>'
             f'<div class="ft-meta">{e(where)} · {e(g.who)}</div></div></div>')
    with action:
        with st.popover("Mensagem pronta", icon=":material/send:", width="stretch"):
            st.caption("Copie (botão no canto do quadro) e mande por e-mail ou WhatsApp.")
            st.code(g.message, language=None, wrap_lines=True)

    for i, item in enumerate(g.items, start=1):
        with st.container(border=True):
            chips = "".join(f'<span class="ft-chip">{e(r)}</span>' for r in item.refs)
            html(f'<div class="ft-row"><span class="ft-num">{i}</span>{badge(item.code, "ft-red")}'
                 f'<span class="ft-item-title">{e(item.title)}</span></div>'
                 + (f'<div class="ft-field"><span class="ft-label">Onde</span><div>{chips}</div></div>' if chips else "")
                 + f'<div class="ft-field"><span class="ft-label">Problema</span><div>{e(item.problem)}</div></div>'
                 f'<div class="ft-fix"><span class="ft-label">Como corrigir</span><div>{e(item.fix)}</div></div>'
                 + "".join(f'<div class="ft-note">{e(n)}</div>' for n in item.notes))


def technical_details(result, name: str, problems=None) -> None:
    st.caption("Explicação completa de cada código de erro, para quem quer entender o porquê.")
    st.download_button("Baixar tudo em texto (.txt)", result.to_plain_text(), file_name=f"resposta-{Path(name).stem}.txt",
                       icon=":material/download:")
    for i, p in enumerate(result.problems if problems is None else problems):
        with st.expander(f"**{p.code}** — {p.title} · {p.quantity_display}"):
            html(f'<div class="ft-meta">{e(p.group_meaning)}</div><div class="ft-sia">No SIA: {e(p.description)}</div>')
            if p.main_area:
                html(area_html(p.main_area))
            for alt in p.areas[1:]:
                html(alt_area_html(alt))
            html('<div class="ft-section">O que eu vi no seu relatório</div>' + bullet_list(p.findings))
            if p.steps:
                html('<div class="ft-section">Passo a passo</div>' + steps_html(p.steps))
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
    page_header("Consultor SIGTAP", "Digite o código ou o nome e veja a ficha completa do procedimento: o que ele exige, "
                                    "o que aceita e quais secundários são obrigatórios.")
    repo = sigtap()

    c1, c2 = st.columns([1, 1], vertical_alignment="bottom")
    query = c1.text_input("Buscar procedimento", placeholder="Código (com ou sem pontos), nome, CBO ou financiamento")
    instrument = c2.segmented_control("Instrumento", ["Todos", "BPA-C", "BPA-I", "APAC", "RAAS", "AIH"], default="Todos")
    results = repo.search(query, instrument or "Todos")
    st.caption(f"{len(results)} de {len(repo.procedures)} procedimentos · tabela {repo.competencia_display or 'de exemplo'}" +
               (" (mostrando os primeiros 250 — refine a busca)" if len(results) >= 250 else ""))

    if results:
        chosen = st.selectbox("Ficha do procedimento", results, index=0 if query else None,
                              format_func=lambda r: f"{r.formatted_code} — {r.name}",
                              placeholder="Escolha um procedimento da lista para ver a ficha completa")
        if chosen:
            procedure_card(repo, chosen)

    st.dataframe(
        [{"Código": r.formatted_code, "Procedimento": r.name, "Instrumento": r.instruments_display, "Sexo": r.sex_display,
          "Idade": r.age_range_display, "CBOs aceitos": r.cbo_summary,
          "CIDs": str(len(r.allowed_cids)) if r.allowed_cids else "—",
          "Obrigatórios": str(sum(1 for c in r.compatibles if c[2] == COMPAT_OBRIGATORIA) or "—"),
          "Qtd. máx.": str(r.max_quantity) if r.max_quantity else "—", "Valor SA": format_brl(r.unit_value),
          "Financiamento": r.financing}
         for r in results],
        width="stretch", hide_index=True,
        column_config={"Procedimento": st.column_config.TextColumn(width="large")})

    st.divider()
    sigtap_tables_panel()


def procedure_card(repo: SigtapRepository, r) -> None:
    """Ficha do procedimento com tudo o que a tabela oficial traz."""
    def name_of(code: str) -> str:
        other = repo.find(code)
        return f"{other.formatted_code} — {other.name}" if other else code

    obligatory = [c for c in r.compatibles if c[2] == COMPAT_OBRIGATORIA]
    others = [c for c in r.compatibles if c[2] != COMPAT_OBRIGATORIA]
    with st.container(border=True):
        html(f'<div class="ft-row">{badge(r.formatted_code, "ft-teal")}<span class="ft-meta">{e(r.financing)} · '
             f'{e(r.instruments_display)}</span></div><div class="ft-title">{e(r.name)}</div>')
        m = st.columns(4)
        m[0].metric("Valor (SA)", format_brl(r.unit_value))
        m[1].metric("Sexo", r.sex_display)
        m[2].metric("Idade", r.age_range_display)
        m[3].metric("Qtd. máxima", str(r.max_quantity) if r.max_quantity else "—")

        if obligatory:
            html('<div class="ft-section">Secundários obrigatórios (têm que estar lançados na APAC)</div>'
                 + bullet_list([f"{name_of(c)} — quantidade {q}" for c, q, _ in obligatory]))
        if others:
            with st.expander(f"Outros procedimentos secundários permitidos ({len(others)})"):
                html(bullet_list([f"{name_of(c)} — até {q}" if q else name_of(c) for c, q, _ in others]))
        rules = repo.rule_texts(r)
        details = repo.detail_text(r)
        if details:
            html('<div class="ft-section">Observações do SIGTAP</div>' + bullet_list(details))
        if rules:
            with st.expander(f"Regras condicionadas ({len(rules)})"):
                for title, desc in rules:
                    st.markdown(f"**{title}** — {desc}")
        a, b = st.columns(2)
        with a.expander(f"CBOs aceitos ({'qualquer' if '*' in r.allowed_cbos else len(r.allowed_cbos)})"):
            st.write("Qualquer CBO habilitado." if "*" in r.allowed_cbos else ", ".join(r.allowed_cbos))
        with b.expander(f"CIDs aceitos como principal ({len(r.allowed_cids) or 'sem restrição'})"):
            st.write(", ".join(r.allowed_cids) if r.allowed_cids else "O SIGTAP não restringe CID para este procedimento.")


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
        header = st.columns([1.1, 1.4, 2.0, 1.3, 1.3, 1.2])
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
            cols = st.columns([1.1, 1.4, 2.0, 1.3, 1.3, 1.2], vertical_alignment="center")
            cols[0].write(f"{comp[4:]}/{comp[:4]}")
            cols[1].caption((t.version if t and t.version else (r.version if r else "—")))
            cols[2].markdown(status)
            if (t is None or outdated) and r is not None:
                if cols[3].button("Baixar", key=f"dl-{comp}", icon=":material/download:", width="stretch"):
                    with st.spinner(f"Baixando {r.filename} do DATASUS…"):
                        try:
                            s.download(r, sigtap_remote.download)
                        except Exception as exc:  # noqa: BLE001
                            st.error(f"Não consegui baixar: {exc}")
                    st.rerun()
            if t is not None and not in_use:
                if cols[4].button("Usar esta", key=f"use-{comp}", type="primary", icon=":material/check:",
                                  width="stretch", disabled=not manage):
                    s.use(comp)
                    st.rerun()
                if not t.from_project and cols[5].button("Apagar", key=f"del-{comp}", icon=":material/delete:",
                                                         width="stretch", disabled=not manage,
                                                         help="Apaga a tabela guardada (dá para baixar de novo depois)."):
                    s.delete(comp)
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
            width="stretch", hide_index=True,
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


def theme_switcher() -> None:
    """Botões Claro / Escuro / Automático. O padrão é o Claro; "Automático" segue o tema do Windows.

    O Streamlit guarda a escolha no navegador (localStorage "stActiveTheme-<página>-v2" = "System" | "Light" | "Dark")
    e só lê ao carregar a página. Os botões ficam dentro do próprio quadro do componente porque o Streamlit só deixa
    um componente recarregar a página quando o clique acontece nele (allow-top-navigation-by-user-activation).
    """
    paths = sorted({"/", *[f"/{p.url_path}" for p in PAGES.values()]})
    js_paths = ", ".join(f'"{p}"' for p in paths)
    st.caption("Tema")
    components.html(f"""
<style>
  html, body {{ margin: 0; background: transparent; }}
  .seg {{ display: flex; gap: 4px; padding: 3px; border-radius: 10px; font: 600 13px "Source Sans Pro", "Segoe UI", sans-serif; }}
  .seg button {{ flex: 1; border: 0; border-radius: 8px; padding: 7px 4px; cursor: pointer; font: inherit; background: transparent; }}
</style>
<div class="seg" id="seg">
  <button data-v="Light">Claro</button>
  <button data-v="Dark">Escuro</button>
  <button data-v="System" title="Segue o tema do Windows">Automático</button>
</div>
<script>
  const w = window.parent;
  const key = p => `stActiveTheme-${{p}}-v2`;
  const all = [{js_paths}, w.location.pathname];
  let current = "Light";
  try {{
    if (!w.localStorage.getItem("ft-tema-v1")) {{
      // 1ª visita neste navegador: fica no Claro (sozinho o Streamlit seguiria o Windows). Só acontece uma vez.
      w.localStorage.setItem("ft-tema-v1", "1");
      for (const p of all) w.localStorage.setItem(key(p), '"Light"');
      w.eval("location.reload()");
    }}
    current = JSON.parse(w.localStorage.getItem(key(w.location.pathname)) || '"Light"');
  }} catch (e) {{}}
  // Cores combinando com o tema que está na tela agora.
  const bg = w.getComputedStyle(w.document.body).backgroundColor.match(/\\d+/g).map(Number);
  const dark = (0.299 * bg[0] + 0.587 * bg[1] + 0.114 * bg[2]) < 128;
  const seg = document.getElementById("seg");
  seg.style.background = dark ? "rgba(255,255,255,.07)" : "rgba(15,23,42,.06)";
  for (const b of seg.querySelectorAll("button")) {{
    const on = b.dataset.v === current;
    b.style.color = on ? "#fff" : (dark ? "#cbd5e1" : "#334155");
    b.style.background = on ? "#0d9488" : "transparent";
    b.onclick = () => {{
      const value = JSON.stringify(b.dataset.v);
      w.localStorage.setItem("ft-tema-v1", "1");
      for (const p of all) w.localStorage.setItem(key(p), value);
      w.location.reload();
    }};
  }}
</script>""", height=44)


with st.sidebar:
    st.divider()
    sidebar_sigtap_switch()
    theme_switcher()
    st.caption(":material/lock: Relatórios e remessas enviados são lidos só em memória e não ficam salvos.")

nav.run()
