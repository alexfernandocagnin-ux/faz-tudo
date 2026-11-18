"""Mesmos casos do ConsistencySolverTests.cs / SolverAreasTests.cs — as duas versões precisam dar o mesmo resultado."""

from pathlib import Path

import pytest

from faztudo import ROOT_DIR
from faztudo.consistency import ConsistencySolver, decode_report, parse_report
from faztudo.knowledge import load_knowledge
from faztudo.sigtap import SigtapRepository

FIXTURE = ROOT_DIR / "tests" / "FazTudo.Tests" / "Fixtures" / "R0350-exemplo.OUT"


def report():
    return parse_report(decode_report(FIXTURE.read_bytes()), FIXTURE.name)


def solve(text: str | None = None):
    rep = report() if text is None else parse_report(text, "x.OUT")
    return ConsistencySolver(SigtapRepository(), load_knowledge()).solve(rep)


def problem(code, result=None):
    return next(p for p in (result or solve()).problems if p.code == code)


def test_cabecalho():
    r = report()
    assert (r.orgao, r.competencia, r.bdsia_version, r.generated_at) == \
        ("SMS-ARARAQ", "10/2026", "BDSIA202610b", "17/11/2026 14:14:04")


def test_quadro_de_totais():
    totals = report().totals
    assert len(totals) == 10
    assert sum(t.quantity for t in totals) == 28
    assert all(t.group == "NOR" for t in totals)
    assert any(t.code == "ERRo" and t.quantity == 12 for t in totals)


def test_linha_bpa_i():
    first = [r for r in report().records if r.instrument == "BPAI"][0]
    assert (first.cnes, first.cnes_name, first.reference, first.procedure_code, first.cbo, first.code) == \
        ("2074044", "IPC DR NICOLINO LIA NETO", "701/97", "0203010086", "221105", "FET2")
    assert len(first.third_column) == 15


def test_linhas_apac_com_e_sem_cbo():
    apac = [r for r in report().records if r.instrument == "APAC"]
    assert len(apac) == 14
    qtd = next(r for r in apac if r.code == "QTD2")
    assert (qtd.reference, qtd.procedure_code, qtd.cbo, qtd.third_column, qtd.competencia) == \
        ("3526291001332", "0303050012", "225265", "2003", "08/2026")
    sem_cbo = next(r for r in apac if r.code == "12")
    assert (sem_cbo.procedure_code, sem_cbo.cbo, sem_cbo.third_column) == ("030402014", "", "1292")


def test_apacs_relacionadas():
    recs = [r for r in report().records if r.reference == "3526291013553"]
    assert len(recs) == 2 and all(len(r.related_apacs) == 2 for r in recs)
    closed = next(a for a in recs[0].related_apacs if a.apac_number == "352629101355-3")
    assert str(closed.exit_date) == "2026-10-01" and closed.exit_reason == "51"


def test_explica_todos_os_tipos():
    result = solve()
    assert len(result.problems) == 10 and result.total_occurrences == 28 and result.establishment_count == 3
    assert all(p.knowledge and p.steps and p.findings for p in result.problems)
    assert result.problems[0].code == "ERRo"
    assert "Todas impedem a aprovação" in result.headline


def test_faixa_de_idade_e_codigo_alternativo():
    f = problem("FET2").findings
    assert any("25a até 64a 11m" in x for x in f)
    assert any("02.03.01.001-9" in x for x in f)
    assert any("701/97" in x and "715/72" in x for x in f)


def test_sobreposicao_de_apacs():
    assert any("352629101355-3" in x and "352629103674-1" in x and "de 02/09/2026 até 01/10/2026" in x
               and "mudança de procedimento" in x for x in problem("67").findings)


@pytest.mark.parametrize("code,area", [
    ("FET2", "BPA"), ("ERR8", "BPA"), ("ERRI", "BPA"), ("ERRj", "BPA"),
    ("67", "APAC"), ("12", "APAC"), ("ERRo", "APAC"), ("QTD2", "APAC"), ("9Q", "APAC"), ("Y8", "APAC"),
])
def test_area_de_cada_ocorrencia(code, area):
    assert problem(code).main_area.key == area


def test_para_quem_mandar():
    assert "CNES 2074044 — IPC DR NICOLINO LIA NETO (2 fichas)" in problem("FET2").main_area.details
    assert "CNES 2082527 — SANTA CASA DE ARARAQUARA (4 APACs)" in problem("67").main_area.details


FPO_REPORT = (
    "RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n"
    "  GRV F07          3 PROCEDIMENTO SEM ORCAMENTO NA FPO\n"
    " CNES : 1234567 POLICLINICA CENTRAL\n"
    " TIPO COMPET. FL/SEQ         PROCED.    CBO    CNS PROFISS.  OCORRENCIA\n"
    + "".join(f" BPAI 10/2026         001/0{i} 0301010072 225125 700000000000005 PROCEDIMENTO SEM ORCAMENTO NA FPO\n"
              for i in (1, 2, 3))
)


def test_fpo_parte_mac_e_valor():
    area = solve(FPO_REPORT).problems[0].main_area
    assert area.key == "FPO"
    assert any("na parte MAC" in d and "03.01.01.007-2" in d and "meta física de pelo menos 3" in d and "R$ 30,00" in d
               for d in area.details)


def test_grave_desconhecido_vai_para_fpo_e_cnes():
    p = solve("RELATORIO DE OCORRENCIAS NA CONSISTENCIA - OUT/2026\n  GRV ZZ1          1 ALGUMA COISA NOVA\n").problems[0]
    assert [a.key for a in p.areas] == ["FPO", "CNES"] and p.areas[1].is_alternative
    assert p.title == "Alguma coisa nova"


def test_texto_para_copiar():
    text = solve().to_plain_text()
    assert "→ Onde resolver: BPA — produção da unidade" in text
    for code in ["ERRo", "67", "12", "ERR8", "FET2", "QTD2", "9Q", "ERRI", "ERRj", "Y8"]:
        assert f"[{code}]" in text


def test_arquivo_errado():
    with pytest.raises(ValueError, match="R0350"):
        parse_report("01#BPA#202611", "remessa.txt")
