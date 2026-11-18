"""Gera shared/sigtap/sigtap-oficial.json.gz a partir do zip da Tabela Unificada do SIGTAP.

Com esse arquivo publicado junto do projeto, o app web abre sempre com a tabela oficial da competência
(o Streamlit Cloud "dorme" e esquece o que foi importado pela tela).

Uso (na pasta do projeto):
    python web/tools/preparar_sigtap.py TabelaUnificada_202611_v2611.zip
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from faztudo.sigtap import OFFICIAL_BUNDLE, SigtapRepository  # noqa: E402


def main() -> None:
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)
    repo = SigtapRepository.from_zip(Path(sys.argv[1]).read_bytes())
    repo.to_bundle(OFFICIAL_BUNDLE)
    print(f"{repo.label} → {OFFICIAL_BUNDLE} ({OFFICIAL_BUNDLE.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
