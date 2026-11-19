"""Tabelas SIGTAP guardadas pelo app e qual delas está EM USO — a escolha vale para todo mundo que abre o site.

- As tabelas baixadas do DATASUS (ou enviadas em zip) ficam em <data_dir>/sigtap-AAAAMM.json.gz.
- A competência em uso fica em <data_dir>/em-uso.json.
- O padrão do projeto fica em shared/sigtap/ (em-uso.json + a tabela daquela competência). É o que vale quando
  o servidor reinicia — no Streamlit Cloud isso acontece quando o app "acorda", e a pasta de dados volta vazia.

Baixar uma tabela nova NUNCA troca a tabela em uso sozinha: isso só acontece em use().
"""

from __future__ import annotations

import json
import os
import threading
from dataclasses import dataclass
from pathlib import Path

from . import ROOT_DIR, SHARED_DIR
from .sigtap import BUNDLE_FORMAT, SigtapRepository
from .sigtap_remote import RemoteTable

DEFAULT_DATA_DIR = Path(os.environ.get("CONSERTA_SIA_DATA", ROOT_DIR / "web" / ".data"))
PROJECT_DIR = SHARED_DIR / "sigtap"


@dataclass(frozen=True)
class StoredTable:
    competencia: str
    version: str
    path: Path
    from_project: bool

    @property
    def competencia_display(self) -> str:
        return f"{self.competencia[4:]}/{self.competencia[:4]}"


class SigtapStore:
    def __init__(self, data_dir: Path = DEFAULT_DATA_DIR, project_dir: Path = PROJECT_DIR):
        self.data_dir = Path(data_dir)
        self.project_dir = Path(project_dir)
        self._lock = threading.RLock()
        self._loaded: dict[str, SigtapRepository] = {}
        self._formats: dict[tuple[str, float], bool] = {}

    # ------------------------------------------------------------ o que existe
    def tables(self) -> dict[str, StoredTable]:
        """Competência → tabela guardada (a da pasta de dados ganha da do projeto)."""
        found: dict[str, StoredTable] = {}
        for folder, from_project in ((self.project_dir, True), (self.data_dir, False)):
            for path in sorted(folder.glob("sigtap-*.json.gz")):
                comp = path.name[len("sigtap-"):-len(".json.gz")]
                if len(comp) == 6 and comp.isdigit():
                    if not from_project and not self._current_format(path):
                        continue  # baixada num formato antigo: é ignorada e o site baixa de novo
                    found[comp] = StoredTable(comp, self._version_of(path), path, from_project)
        return dict(sorted(found.items(), reverse=True))

    def _current_format(self, path: Path) -> bool:
        key = (str(path), path.stat().st_mtime)
        if key not in self._formats:
            self._formats[key] = SigtapRepository.bundle_format(path) >= BUNDLE_FORMAT
        return self._formats[key]

    @staticmethod
    def _version_of(path: Path) -> str:
        meta = path.with_name(path.name.replace(".json.gz", ".versao"))
        return meta.read_text(encoding="utf-8").strip() if meta.exists() else ""

    def in_use(self) -> str | None:
        """Competência em uso: a escolhida no site; senão, o padrão do projeto."""
        for folder in (self.data_dir, self.project_dir):
            choice = folder / "em-uso.json"
            if choice.exists():
                try:
                    comp = json.loads(choice.read_text(encoding="utf-8")).get("competencia", "")
                except (json.JSONDecodeError, OSError):
                    continue
                if comp in self.tables():
                    return comp
        return None

    def is_project_default(self) -> bool:
        """true quando a tabela em uso é a padrão do projeto (sobrevive a reinícios do servidor)."""
        default = self.project_dir / "em-uso.json"
        if not default.exists():
            return False
        try:
            return json.loads(default.read_text(encoding="utf-8")).get("competencia") == self.in_use()
        except (json.JSONDecodeError, OSError):
            return False

    # ------------------------------------------------------------ ações
    def add_zip(self, data: bytes, version: str = "", to_project: bool = False) -> StoredTable:
        repo = SigtapRepository.from_zip(data)
        if not repo.competencia:
            raise ValueError("Não consegui identificar a competência dessa tabela.")
        repo.version = version
        folder = self.project_dir if to_project else self.data_dir
        path = folder / f"sigtap-{repo.competencia}.json.gz"
        with self._lock:
            repo.to_bundle(path)
            if version:
                path.with_name(path.name.replace(".json.gz", ".versao")).write_text(version, encoding="utf-8")
            self._loaded[repo.competencia] = repo
        return StoredTable(repo.competencia, version, path, to_project)

    def download(self, table: RemoteTable, downloader) -> StoredTable:
        """Baixa e guarda. Não muda a tabela em uso."""
        stored = self.tables().get(table.competencia)
        if stored and stored.version == table.version:
            return stored
        return self.add_zip(downloader(table), version=table.version)

    def use(self, competencia: str, as_project_default: bool = False) -> None:
        if competencia not in self.tables():
            raise ValueError(f"A tabela {competencia} ainda não foi baixada.")
        folder = self.project_dir if as_project_default else self.data_dir
        folder.mkdir(parents=True, exist_ok=True)
        with self._lock:
            (folder / "em-uso.json").write_text(json.dumps({"competencia": competencia}), encoding="utf-8")

    def delete(self, competencia: str) -> None:
        """Apaga uma tabela baixada. Não apaga a que está em uso nem a padrão do projeto."""
        if competencia == self.in_use():
            raise ValueError("Não dá para apagar a tabela que está em uso.")
        path = self.data_dir / f"sigtap-{competencia}.json.gz"
        with self._lock:
            path.unlink(missing_ok=True)
            path.with_name(path.name.replace(".json.gz", ".versao")).unlink(missing_ok=True)
            self._loaded.pop(competencia, None)

    def current(self) -> SigtapRepository:
        """A tabela em uso, já carregada; sem nenhuma oficial guardada, a base de exemplo."""
        comp = self.in_use()
        if comp is None:
            return SigtapRepository()
        with self._lock:
            repo = self._loaded.get(comp)
            stored = self.tables()[comp]
            if repo is None or (stored.version and repo.version != stored.version):
                repo = SigtapRepository.from_bundle(stored.path)
                repo.version = repo.version or stored.version
                self._loaded[comp] = repo
            return repo
