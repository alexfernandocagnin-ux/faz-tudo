"""Tabelas do DATASUS: baixar sozinho, mas só trocar a tabela em uso quando alguém escolher."""

import io
import json
import zipfile

import pytest

from faztudo.sigtap import title_pt
from faztudo.sigtap_remote import RemoteTable, parse_listing
from faztudo.sigtap_store import SigtapStore


def proc_line(code, name, comp, cents=1000):
    return (code.ljust(10) + name.ljust(250) + "M" + "I" + "0001" + "0000" + "0000" + "0000" + "9999"
            + "0" * 12 + str(cents).zfill(12) + "0" * 12 + "06" + "000000" + "0000" + comp)


def make_zip(comp: str, cents: int = 1000) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("tb_procedimento.txt", proc_line("0301010072", "CONSULTA MEDICA EM ATENCAO ESPECIALIZADA", comp, cents))
    return buf.getvalue()


@pytest.fixture
def store(tmp_path):
    return SigtapStore(data_dir=tmp_path / "dados", project_dir=tmp_path / "projeto")


def test_listagem_do_ftp_fica_com_a_versao_mais_nova():
    names = ["TabelaUnificada_202610_v2610271043.zip", "TabelaUnificada_202611_v2611051737.zip",
             "TabelaUnificada_202611_v2611121000.zip", "antigas", "sigtap-setup-1.4.exe", "manual"]
    tables = parse_listing(names)
    assert [(t.competencia, t.version) for t in tables] == [("202611", "2611121000"), ("202610", "2610271043")]
    assert tables[0].competencia_display == "11/2026"


def test_sem_nada_guardado_usa_a_base_de_exemplo(store):
    assert store.in_use() is None
    assert not store.current().is_official


def test_baixar_nao_troca_a_tabela_em_uso(store):
    store.add_zip(make_zip("202610"), version="v1")
    store.use("202610")
    novo = RemoteTable("202611", "2611051737", "TabelaUnificada_202611_v2611051737.zip")

    store.download(novo, lambda t: make_zip("202611"))

    assert set(store.tables()) == {"202610", "202611"}
    assert store.in_use() == "202610"
    assert store.current().competencia == "202610"


def test_escolher_troca_para_todo_mundo(store, tmp_path):
    store.add_zip(make_zip("202610", 1000))
    store.add_zip(make_zip("202611", 1250))
    store.use("202611")

    # Outra instância (outro usuário / outra sessão) enxerga a mesma escolha.
    other = SigtapStore(data_dir=tmp_path / "dados", project_dir=tmp_path / "projeto")
    assert other.in_use() == "202611"
    assert str(other.current().find("0301010072").unit_value) == "12.5"


def test_reinicio_volta_para_o_padrao_do_projeto(store, tmp_path):
    store.add_zip(make_zip("202610"), to_project=True)
    store.use("202610", as_project_default=True)
    store.add_zip(make_zip("202611"))
    store.use("202611")
    assert store.in_use() == "202611" and not store.is_project_default()

    # Servidor reiniciou: a pasta de dados volta vazia, fica o que está no projeto.
    fresh = SigtapStore(data_dir=tmp_path / "dados-novo", project_dir=tmp_path / "projeto")
    assert fresh.in_use() == "202610" and fresh.is_project_default()


def test_nao_deixa_usar_tabela_que_nao_foi_baixada(store):
    with pytest.raises(ValueError, match="ainda não foi baixada"):
        store.use("209912")


def test_versao_nova_da_mesma_competencia_substitui(store):
    v1 = RemoteTable("202611", "2611051737", "a.zip")
    v2 = RemoteTable("202611", "2611121000", "b.zip")
    store.download(v1, lambda t: make_zip("202611", 1000))
    store.use("202611")
    store.download(v2, lambda t: make_zip("202611", 1100))

    assert store.tables()["202611"].version == "2611121000"
    assert str(store.current().find("0301010072").unit_value) == "11"


def test_escolha_corrompida_e_ignorada(store):
    store.add_zip(make_zip("202611"))
    store.data_dir.mkdir(parents=True, exist_ok=True)
    (store.data_dir / "em-uso.json").write_text("{ quebrado", encoding="utf-8")
    assert store.in_use() is None


def test_padrao_do_projeto_publicado():
    """O projeto precisa sair com uma tabela oficial padrão (o site nunca abre com a base de exemplo)."""
    real = SigtapStore()
    comp = json.loads((real.project_dir / "em-uso.json").read_text(encoding="utf-8"))["competencia"]
    assert comp in real.tables()
    repo = SigtapStore(data_dir=real.project_dir / "_nao_existe", project_dir=real.project_dir).current()
    assert repo.is_official and len(repo.procedures) > 4000


def test_nomes_em_portugues():
    assert title_pt("CONSULTA MEDICA EM ATENCAO ESPECIALIZADA") == "Consulta Medica em Atencao Especializada"
    assert title_pt("EXAME DE SANGUE E URINA") == "Exame de Sangue e Urina"
