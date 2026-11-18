"""Doutor TI no site: o resultado do script vira os mesmos itens do desktop."""

import json

import pytest

from faztudo import doctor

BASE = {
    "ferramenta": "conserta-sia-doutor-ti", "versao": 1, "gerado_em": "2026-11-18T11:32:38",
    "windows": "Microsoft Windows 11 Pro", "raiz": "C:\\", "pastas_na_raiz": ["BPA"], "pastas_em_program_files": [],
    "virtualstore_existe": False, "virtualstore_datasus": [], "porta_3050_aberta": True,
    "processos_firebird": ["fbserver", "fbguard"], "midas_syswow64": True, "midas_system32": False,
    "bde_encontrado": False, "disco_livre_bytes": 53458083840,
}


def run(**changes):
    return {i.title: i for i in doctor.analyze({**BASE, **changes})}


def test_maquina_saudavel():
    items = doctor.analyze(BASE)
    assert not [i for i in items if i.status in ("critico", "atencao")]
    assert any("C:\\BPA" in i.detected for i in items)


def test_le_o_arquivo_com_bom_do_powershell():
    data = ("\ufeff" + json.dumps(BASE)).encode("utf-8")
    assert doctor.load_result(data)["windows"] == "Microsoft Windows 11 Pro"


def test_arquivo_errado():
    with pytest.raises(ValueError, match="Doutor TI"):
        doctor.load_result(b'{"outra": "coisa"}')


def test_virtualstore_sequestrando_o_sia():
    item = run(virtualstore_existe=True, virtualstore_datasus=["Program Files (x86)\\SIA"])[
        "Arquivos DATASUS desviados para a VirtualStore"]
    assert item.status == "critico" and item.knowledge_id == "sia-ti-virtualstore"


def test_instalado_em_arquivos_de_programas():
    item = run(pastas_em_program_files=["Program Files (x86)\\SIA"])["Sistemas DATASUS dentro de 'Arquivos de Programas'"]
    assert item.status == "critico"


def test_firebird_parado_e_conflito_de_versoes():
    assert run(porta_3050_aberta=False)["Porta 3050 do Firebird"].status == "atencao"
    assert run(processos_firebird=["fbserver", "firebird"])["Processos do Firebird"].status == "critico"


def test_powershell_que_achatou_lista_de_um_item():
    # O ConvertTo-Json do PowerShell 5.1 às vezes grava lista de 1 item como texto.
    assert run(processos_firebird="fbserver")["Processos do Firebird"].detected == "Em execução: fbserver.exe"


def test_midas_e_disco():
    assert run(midas_syswow64=False, midas_system32=True)["midas.dll (32 bits)"].status == "atencao"
    assert run(disco_livre_bytes=1 * 1024 ** 3)["Espaço livre e armadilha do BDE"].status == "critico"
    assert run(disco_livre_bytes=8 * 1024 ** 3 + 10)["Espaço livre e armadilha do BDE"].status == "atencao"


def test_script_do_site_existe_e_so_le():
    from faztudo import ROOT_DIR
    script = (ROOT_DIR / "web" / "assets" / "doutor-ti.ps1").read_text(encoding="utf-8-sig")
    assert "conserta-sia-doutor-ti" in script
    for forbidden in ("Remove-Item", "Set-ItemProperty", "Stop-Process", "Start-Service", "regsvr32 "):
        assert forbidden not in script
