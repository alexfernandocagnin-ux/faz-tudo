"""Tabelas do SIGTAP direto do FTP do DATASUS (ftp2.datasus.gov.br/pub/sistemas/tup/downloads).

Os arquivos se chamam TabelaUnificada_AAAAMM_vAAMMDDhhmm.zip; uma competência pode ter mais de uma versão
(republicação) — vale a de versão mais nova.
"""

from __future__ import annotations

import ftplib
import io
import re
from dataclasses import dataclass

FTP_HOST = "ftp2.datasus.gov.br"
FTP_DIR = "/pub/sistemas/tup/downloads"
_NAME = re.compile(r"^TabelaUnificada_(\d{6})_v(\d+)\.zip$", re.IGNORECASE)


@dataclass(frozen=True)
class RemoteTable:
    competencia: str   # AAAAMM
    version: str
    filename: str

    @property
    def competencia_display(self) -> str:
        return f"{self.competencia[4:]}/{self.competencia[:4]}"


def parse_listing(names: list[str]) -> list[RemoteTable]:
    """Da listagem do FTP, fica com a versão mais nova de cada competência (mais recente primeiro)."""
    best: dict[str, RemoteTable] = {}
    for raw in names:
        m = _NAME.match(raw.strip().split("/")[-1])
        if not m:
            continue
        table = RemoteTable(m.group(1), m.group(2), m.group(0))
        current = best.get(table.competencia)
        if current is None or table.version > current.version:
            best[table.competencia] = table
    return sorted(best.values(), key=lambda t: t.competencia, reverse=True)


def list_remote(timeout: float = 30) -> list[RemoteTable]:
    with ftplib.FTP(FTP_HOST, timeout=timeout) as ftp:
        ftp.login()
        ftp.cwd(FTP_DIR)
        return parse_listing(ftp.nlst())


def download(table: RemoteTable, timeout: float = 120) -> bytes:
    buffer = io.BytesIO()
    with ftplib.FTP(FTP_HOST, timeout=timeout) as ftp:
        ftp.login()
        ftp.cwd(FTP_DIR)
        ftp.retrbinary(f"RETR {table.filename}", buffer.write)
    return buffer.getvalue()
