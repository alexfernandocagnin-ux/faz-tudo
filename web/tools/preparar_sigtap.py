"""Define a tabela SIGTAP PADRÃO do projeto (a que o site usa ao iniciar) e grava em shared/sigtap/.

Uso (na pasta do projeto):
    python web/tools/preparar_sigtap.py              → baixa a competência mais recente do DATASUS
    python web/tools/preparar_sigtap.py 202611       → baixa essa competência do DATASUS
    python web/tools/preparar_sigtap.py arquivo.zip  → usa um zip já baixado

Depois é só publicar (commit + push) a pasta shared/sigtap/.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from faztudo import sigtap_remote  # noqa: E402
from faztudo.sigtap_store import SigtapStore  # noqa: E402


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")  # console do Windows
    arg = sys.argv[1] if len(sys.argv) > 1 else ""
    store = SigtapStore()
    if arg.lower().endswith(".zip"):
        stored = store.add_zip(Path(arg).read_bytes(), to_project=True)
    else:
        remote = sigtap_remote.list_remote()
        table = next((t for t in remote if t.competencia == arg), None) if arg else (remote[0] if remote else None)
        if table is None:
            print(f"Competência {arg or '(mais recente)'} não encontrada no DATASUS. Disponíveis: "
                  + ", ".join(t.competencia for t in remote))
            sys.exit(1)
        print(f"Baixando {table.filename}…")
        stored = store.add_zip(sigtap_remote.download(table), version=table.version, to_project=True)
    store.use(stored.competencia, as_project_default=True)
    for old in store.project_dir.glob("sigtap-*.json.gz"):  # o projeto guarda só a padrão
        if old != stored.path:
            old.unlink()
            old.with_name(old.name.replace(".json.gz", ".versao")).unlink(missing_ok=True)
    print(f"Padrão do projeto: {stored.competencia_display} → {stored.path} ({stored.path.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
