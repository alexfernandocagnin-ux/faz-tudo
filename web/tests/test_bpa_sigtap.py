"""Mesmos casos de SusValidatorsTests.cs, BpaAnalyzerTests.cs, SigtapImportTests.cs e TriagemCsvExporterTests.cs."""

import io
import zipfile
from decimal import Decimal

import pytest

from faztudo import SAMPLES_DIR
from faztudo.bpa import analyze, findings_to_csv, parse_bpa
from faztudo.knowledge import load_knowledge
from faztudo.sigtap import SigtapRepository
from faztudo.validators import is_valid_cns, is_valid_cpf

KB = load_knowledge()
HEADER = "01#BPA#202611000002000001"


@pytest.mark.parametrize("cns,ok", [
    ("100000000000007", True), ("700000000000005", True), ("700 0000 0000 0005", True),
    ("100000000000008", False), ("700000000000006", False), ("300000000000003", False), ("777777777777777", False),
    ("12345", False), ("", False), (None, False),
])
def test_cns(cns, ok):
    assert is_valid_cns(cns) is ok


@pytest.mark.parametrize("cpf,ok", [("529.982.247-25", True), ("52998224725", True), ("529.982.247-24", False),
                                    ("111.111.111-11", False), ("123", False), (None, False)])
def test_cpf(cpf, ok):
    assert is_valid_cpf(cpf) is ok


def bpa_i(proc="0204030188", cbo="225320", sex="F", birth="19650510", race="03", qty=1, cns="700000000000005"):
    return ("03" + "1234567" + "202611" + cns.ljust(15) + cbo.ljust(6) + "20261105" + "001" + "01" + proc.ljust(10)
            + cns.ljust(15) + sex + "355030" + "    " + "061" + str(qty).zfill(6) + "01" + " " * 13 + "BPA"
            + "MARIA DA SILVA".ljust(30) + birth + race + "    " + "010")


def run(*lines):
    return analyze(parse_bpa("\n".join([HEADER, *lines]), "teste.txt"), SigtapRepository(), KB)


def test_linha_correta():
    s = run(bpa_i(qty=2))
    assert not s.findings and s.valid_records == 1 and s.approved_value == Decimal("90.00")


@pytest.mark.parametrize("code,line", [
    ("002", bpa_i(sex="M")), ("004", bpa_i(cbo="223505")), ("005", bpa_i(birth="19860101")),
    ("CNS", bpa_i(cns="700000000000006")), ("RAÇA", bpa_i(race="99")),
])
def test_criticas(code, line):
    s = run(line)
    assert [f.code for f in s.findings] == [code] or code == "CNS"  # CNS inválido aparece no paciente e no profissional
    assert s.findings[0].code == code
    assert s.glosa_value == Decimal("45.00")


def test_agrupa_por_causa():
    s = run(bpa_i(cbo="223505", qty=3), bpa_i(cbo="223505", qty=1), bpa_i(race="99"))
    top = s.groups[0]
    assert (top.error_code, top.occurrences, top.procedures_affected, top.impact) == ("004", 2, 4, Decimal("180.00"))


def test_remessa_de_exemplo_bate_com_o_desktop():
    text = (SAMPLES_DIR / "remessa-bpa-exemplo.txt").read_bytes().decode("latin-1")
    s = analyze(parse_bpa(text, "remessa-bpa-exemplo.txt"), SigtapRepository(), KB)
    # Mesmo resultado do desktop: 9 linhas, 2 válidas, 7 críticas, R$ 405,00 de glosa.
    assert (s.total_records, s.valid_records, len(s.findings), s.glosa_value) == (9, 2, 7, Decimal("405.00"))


def test_csv_para_excel():
    data = findings_to_csv(run(bpa_i(cbo="223505", qty=3)), KB)
    assert data[:3] == b"\xef\xbb\xbf"
    line = data.decode("utf-8-sig").splitlines()[1]
    assert ';="004";' in line and '="0204030188"' in line and ";135,00;" in line


def proc_line(code, name, sex, min_age, max_age, cents, fin="06", comp="202611"):
    return (code.ljust(10) + name.ljust(250) + "M" + sex + "0001" + "0000" + "0000" + str(min_age).zfill(4)
            + str(max_age).zfill(4) + "0" * 12 + str(cents).zfill(12) + "0" * 12 + fin + "000000" + "0000" + comp)


def make_zip():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("TabelaUnificada_202611/tb_procedimento.txt",
                   proc_line("0204030188", "MAMOGRAFIA BILATERAL PARA RASTREAMENTO", "F", 600, 839, 4500, "04").encode("latin-1"))
        z.writestr("TabelaUnificada_202611/rl_procedimento_ocupacao.txt", b"0204030188225320202611")
        z.writestr("TabelaUnificada_202611/rl_procedimento_registro.txt", b"020403018802202611")
        z.writestr("TabelaUnificada_202611/rl_procedimento_cid.txt", b"0204030188Z123S202611\n0204030188Z000N202611")
    return buf.getvalue()


def test_importa_zip_oficial():
    repo = SigtapRepository.from_zip(make_zip())
    assert repo.is_official and "11/2026" in repo.label
    rule = repo.find("02.04.03.018-8")
    assert (rule.sex_restriction, rule.min_age_months, rule.max_age_months, rule.unit_value, rule.financing) == \
        ("F", 600, 839, Decimal("45.00"), "FAEC")
    assert rule.allowed_instruments == ["BPA-I"] and rule.allowed_cbos == ["225320"] and rule.allowed_cids == ["Z123"]
    assert rule.max_quantity == 1
    assert repo.find("020403018").code == "0204030188"  # 9 dígitos, como no relatório de APAC


def test_zip_errado():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("leia.txt", "nada")
    with pytest.raises(ValueError, match="tb_procedimento"):
        SigtapRepository.from_zip(buf.getvalue())


def test_pacote_oficial_ida_e_volta(tmp_path):
    repo = SigtapRepository.from_zip(make_zip())
    path = tmp_path / "sigtap.json.gz"
    repo.to_bundle(path)
    again = SigtapRepository.from_bundle(path)
    assert again.label == repo.label and again.find("0204030188").allowed_cids == ["Z123"]


def test_busca_sem_acento():
    repo = SigtapRepository()
    assert any(p.code == "0301010110" for p in repo.search("pre-natal"))
    assert len(repo.search("02.04.03.018-8")) == 1
