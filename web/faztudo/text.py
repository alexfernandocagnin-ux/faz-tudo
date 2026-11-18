"""Normalização para busca: ignora acentos e maiúsculas ("atencao" encontra "Atenção")."""

import unicodedata


def normalize(value: str | None) -> str:
    if not value:
        return ""
    decomposed = unicodedata.normalize("NFD", value)
    return "".join(c for c in decomposed if unicodedata.category(c) != "Mn").lower()


def terms(query: str | None) -> list[str]:
    return normalize(query).split()


def format_brl(value) -> str:
    """1234.5 → 'R$ 1.234,50'."""
    text = f"{float(value):,.2f}"
    return "R$ " + text.replace(",", "X").replace(".", ",").replace("X", ".")
