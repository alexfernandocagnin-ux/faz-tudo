"""Respostas prontas: o caso real da OCI de mama (faltava a PAAF) e a lista por responsável."""

from faztudo import ROOT_DIR
from faztudo.consistency import ConsistencySolver, decode_report, parse_apac_mirror, parse_report
from faztudo.knowledge import load_knowledge
from faztudo.sigtap_store import SigtapStore

FIX = ROOT_DIR / "tests" / "FazTudo.Tests" / "Fixtures"


def official():
    store = SigtapStore()  # tabela padrão do projeto (oficial, com compatibilidades)
    repo = store.current()
    assert repo.is_official
    return repo


def solve(with_mirror: bool):
    report = parse_report(decode_report((FIX / "R0350-exemplo.OUT").read_bytes()), "R0350.OUT")
    mirrors = parse_apac_mirror(decode_report((FIX / "R0330T-exemplo.OUT").read_bytes())) if with_mirror else []
    return ConsistencySolver(official(), load_knowledge()).solve(report, mirrors)


def test_le_o_espelho_da_apac_sem_dados_do_paciente():
    [m] = parse_apac_mirror(decode_report((FIX / "R0330T-exemplo.OUT").read_bytes()))
    assert (m.cnes, m.number, m.principal, m.apac_type) == ("9424377", "3526702075237", "090101009", "UNICA")
    assert [i.procedure_code for i in m.items] == ["0901010090", "0201010569", "0203010043", "0301010072"]
    assert m.items[0].has_error and not m.items[1].has_error
    assert m.items[1].third_party_cnes == "2082527"
    assert not hasattr(m, "patient")


def test_sigtap_traz_os_obrigatorios_da_oci():
    rule = official().find("090101009")
    obrigatorios = [c for c, q, t in rule.compatibles if t == "5"]
    assert set(obrigatorios) == {"0201010585", "0203010043"}  # PAAF de mama + citopatológico de mama


def test_com_espelho_diz_exatamente_o_que_falta():
    erro = next(p for p in solve(with_mirror=True).problems if p.code == "ERRo")
    texto = next(a.text for a in erro.actions if "3526702075237" in a.text)
    assert "FALTA lançar o secundário obrigatório 02.01.01.058-5" in texto
    assert "Punção Aspirativa de Mama por Agulha Fina" in texto
    assert "Exame Citopatologico de Mama" in texto  # aparece em "já lançados"


def test_sem_espelho_lista_os_obrigatorios_e_pede_o_espelho():
    result = solve(with_mirror=False)
    erro = next(p for p in result.problems if p.code == "ERRo")
    assert all("exige os secundários obrigatórios 02.01.01.058-5" in a.text for a in erro.actions)
    assert any("R0330T" in n for n in result.notes)


def test_lista_por_responsavel_com_mensagem_pronta():
    result = solve(with_mirror=True)
    owners = {(g.area_key, g.cnes): g for g in result.owners}
    assert set(owners) == {("BPA", "2074044"), ("APAC", "9424377"), ("APAC", "2082527")}
    unidade = owners[("BPA", "2074044")]
    assert {i.code for i in unidade.items} == {"FET2", "ERR8", "ERRI", "ERRj"}
    assert unidade.message.startswith("Olá! Na consistência do SIA da competência 10/2026")
    assert "CNES 2074044" in unidade.message and "702/34" in unidade.message


def test_respostas_usam_a_tabela_oficial():
    acts = {a.code: a.text for p in solve(with_mirror=True).problems for a in p.actions}
    assert "CIDs aceitos pelo SIGTAP: C53, D06, N87, N88" in acts["9Q"]
    assert "O máximo permitido no SIGTAP é 1" in acts["QTD2"]
    assert "validade fixa de 2 competências" in acts["Y8"]
    assert "25a até 64a 11m" in acts["FET2"] and "02.03.01.001-9" in acts["FET2"]


def test_siglas_ficam_em_maiusculas():
    assert official().find("090101009").name.startswith("OCI ")


def test_item_sem_orcamento_no_espelho_vira_dica_de_fpo():
    texto = decode_report((FIX / "R0330T-exemplo.OUT").read_bytes()).replace(
        "2074044     SEM ERROS", "2074044     PROCEDIMENTO SEM ORCAMENTO")
    report = parse_report(decode_report((FIX / "R0350-exemplo.OUT").read_bytes()), "R0350.OUT")
    result = ConsistencySolver(official(), load_knowledge()).solve(report, parse_apac_mirror(texto))
    erro = next(p for p in result.problems if p.code == "ERRo")
    notas = [n for a in erro.actions if "3526702075237" in a.refs for n in a.notes]
    assert any("PROCEDIMENTO SEM ORCAMENTO" in n and "FPO Magnético do CNES 2074044" in n for n in notas)


def test_relatorio_recortado_sem_quadro_de_totais_nao_quebra():
    report = parse_report(decode_report((FIX / "R0350-exemplo.OUT").read_bytes()), "R0350.OUT")
    report.totals = []
    result = ConsistencySolver(official(), load_knowledge()).solve(report)
    assert sum(p.quantity for p in result.problems) == len(report.records)
