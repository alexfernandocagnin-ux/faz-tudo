"""Doutor TI no site: interpreta o arquivo gerado pelo script doutor-ti.ps1 (rodado no computador do SIA).

Mesmas checagens e textos do EnvironmentDoctor.cs do desktop.
"""

from __future__ import annotations

import json
from dataclasses import dataclass

GB = 1024 ** 3


@dataclass
class CheckItem:
    status: str        # ok, atencao, critico, info
    category: str
    title: str
    detected: str
    recommendation: str
    knowledge_id: str | None = None

    @property
    def status_display(self) -> str:
        return {"ok": "OK", "atencao": "Atenção", "critico": "Crítico", "info": "Informativo"}[self.status]


def _list(value) -> list[str]:
    if value is None:
        return []
    return [value] if isinstance(value, str) else [str(v) for v in value]


def load_result(data: bytes) -> dict:
    try:
        result = json.loads(data.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError("Esse arquivo não é o resultado do Doutor TI (doutor-ti-resultado.json).") from exc
    if not isinstance(result, dict) or result.get("ferramenta") != "conserta-sia-doutor-ti":
        raise ValueError("Esse arquivo não é o resultado do Doutor TI (doutor-ti-resultado.json).")
    return result


def analyze(r: dict) -> list[CheckItem]:
    items: list[CheckItem] = []
    raiz = r.get("raiz", "C:\\")

    # Instalação
    in_pf = _list(r.get("pastas_em_program_files"))
    in_root = _list(r.get("pastas_na_raiz"))
    if in_pf:
        items.append(CheckItem("critico", "Instalação", "Sistemas DATASUS dentro de 'Arquivos de Programas'",
                               "Encontrado: " + ", ".join(in_pf),
                               "Pastas em Arquivos de Programas sofrem redirecionamento do Windows (VirtualStore). "
                               "Reinstale na raiz (ex.: C:\\SIA, C:\\BPA).", "sia-ti-virtualstore"))
    else:
        items.append(CheckItem("ok" if in_root else "info", "Instalação", "Pastas dos sistemas DATASUS na raiz do disco",
                               "Encontrado: " + ", ".join(f"{raiz}{p}" for p in in_root) if in_root
                               else f"Nenhuma pasta SIA/BPA/APAC/SCNES/FPO/RAAS em {raiz}",
                               "Instalação no local recomendado." if in_root
                               else "Se os sistemas estão instalados em outro disco, ignore este item."))

    # VirtualStore
    hijacked = _list(r.get("virtualstore_datasus"))
    if not r.get("virtualstore_existe"):
        items.append(CheckItem("ok", "Windows / UAC", "Pasta VirtualStore", "Não existe VirtualStore para este usuário.",
                               "Nenhuma ação necessária."))
    else:
        items.append(CheckItem(
            "critico" if hijacked else "ok", "Windows / UAC", "Arquivos DATASUS desviados para a VirtualStore",
            ("Cópias paralelas em: " + "; ".join(f"VirtualStore\\{h}" for h in hijacked)) if hijacked
            else "VirtualStore existe, mas sem pastas dos sistemas DATASUS.",
            "O BDSIA gravou na VirtualStore e o sistema lê a base antiga. Faça backup dessas pastas, remova-as e rode o "
            "BDSIA como Administrador." if hijacked else "Nenhuma ação necessária.",
            "sia-ti-virtualstore" if hijacked else None))

    # Firebird
    port = bool(r.get("porta_3050_aberta"))
    items.append(CheckItem(
        "ok" if port else "atencao", "Banco Firebird", "Porta 3050 do Firebird",
        "Porta 3050 aceitando conexões em localhost." if port else "Nada respondendo na porta 3050 em localhost.",
        "Se mesmo assim der 'Unavailable database', verifique se não é outra versão do Firebird (3.0+) ocupando a porta."
        if port else "Se esta máquina roda SIA/SCNES local, inicie o serviço 'Firebird Server' em services.msc. "
                     "Em estação cliente de rede, ignore.",
        None if port else "sia-ti-firebird"))

    procs = [p if p.endswith(".exe") else f"{p}.exe" for p in _list(r.get("processos_firebird"))]
    conflict = "firebird.exe" in procs and ("fbserver.exe" in procs or "fb_inet_server.exe" in procs)
    items.append(CheckItem(
        "critico" if conflict else "ok" if procs else "info", "Banco Firebird", "Processos do Firebird",
        "Em execução: " + ", ".join(procs) if procs else "Nenhum processo do Firebird em execução.",
        "Firebird 1.5/2.x (fbserver) e 3.0+ (firebird.exe) rodando juntos: configure portas diferentes para não derrubar "
        "o legado DATASUS." if conflict else "Nenhuma ação necessária." if procs
        else "Normal em estações que não hospedam a base local.",
        "sia-ti-firebird" if conflict else None))

    # midas.dll / BDE
    sys_wow = bool(r.get("midas_syswow64"))
    items.append(CheckItem(
        "ok" if sys_wow else "atencao", "Bibliotecas Delphi", "midas.dll (32 bits)",
        "Presente em C:\\Windows\\SysWOW64." if sys_wow else
        "Só existe em C:\\Windows\\System32 (pasta de 64 bits)." if r.get("midas_system32") else "midas.dll não encontrada no Windows.",
        "Se ainda der 'Error loading midas.dll', registre-a com SysWOW64\\regsvr32.exe como Administrador." if sys_wow
        else "Copie midas.dll da pasta do sistema DATASUS para SysWOW64 e registre com C:\\Windows\\SysWOW64\\regsvr32.exe.",
        None if sys_wow else "sia-ti-bde"))
    bde = bool(r.get("bde_encontrado"))
    items.append(CheckItem(
        "ok" if bde else "info", "Bibliotecas Delphi", "BDE (Borland Database Engine)",
        "Configuração do BDE encontrada." if bde else "BDE não encontrado.",
        "Se aparecer erro de memória compartilhada, ajuste SHAREDMEMLOCATION no BDE Administrator." if bde
        else "Só é necessário para BPA-Mag/APAC-Mag/FPO antigos. Se eles abrem normalmente, ignore."))

    # Disco
    free = r.get("disco_livre_bytes")
    if isinstance(free, (int, float)) and free > 0:
        trap = free % (4 * GB) < 256 * 1024 ** 2
        low = free < 2 * GB
        items.append(CheckItem(
            "critico" if low else "atencao" if trap else "ok", "Disco", "Espaço livre e armadilha do BDE",
            f"{free / GB:.1f} GB livres em {raiz}".replace(".", ","),
            "Libere espaço: consistência e backup do SIA precisam de folga em disco." if low else
            "O espaço livre está perto de um múltiplo de 4 GB, faixa em que o BDE acusa 'Insufficient disk space'. "
            "Crie ou apague um arquivo de ~500 MB." if trap else "Nenhuma ação necessária.",
            "sia-ti-bde" if trap else None))

    order = {"critico": 0, "atencao": 1, "info": 2, "ok": 3}
    return sorted(items, key=lambda i: order[i.status])
