"""Faz Tudo — motor do solucionador de faturamento SIA/SUS (versão Python, usada pelo app Streamlit).

Espelha a lógica do desktop em C# (src/FazTudo.Modules.SiaSus). A base de erros e a base de exemplo do
SIGTAP vêm da pasta shared/, que os dois leem.
"""

from pathlib import Path

ROOT_DIR = Path(__file__).resolve().parents[2]
SHARED_DIR = ROOT_DIR / "shared"
SAMPLES_DIR = ROOT_DIR / "samples"
