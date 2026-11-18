"""Validadores de documentos do DATASUS (mesmas regras do SusValidators.cs)."""


def _digits(value: str | None) -> str:
    return "".join(c for c in (value or "") if c.isdigit())


def is_valid_cns(raw: str | None) -> bool:
    """Cartão Nacional de Saúde (15 dígitos) pelo Módulo 11 do DATASUS."""
    d = _digits(raw)
    if len(d) != 15 or len(set(d)) == 1:
        return False

    if d[0] in "12":
        pis = d[:11]
        total = sum(int(pis[i]) * (15 - i) for i in range(11))
        dv = 11 - total % 11
        if dv == 11:
            dv = 0
        if dv == 10:
            total += 2
            dv = 11 - total % 11
            expected = f"{pis}001{dv}"
        else:
            expected = f"{pis}000{dv}"
        return d == expected

    if d[0] in "789":
        return sum(int(d[i]) * (15 - i) for i in range(15)) % 11 == 0

    return False


def is_valid_cpf(raw: str | None) -> bool:
    d = _digits(raw)
    if len(d) != 11 or len(set(d)) == 1:
        return False
    s1 = sum(int(d[i]) * (10 - i) for i in range(9))
    r1 = (s1 * 10) % 11
    r1 = 0 if r1 == 10 else r1
    if r1 != int(d[9]):
        return False
    s2 = sum(int(d[i]) * (11 - i) for i in range(10))
    r2 = (s2 * 10) % 11
    r2 = 0 if r2 == 10 else r2
    return r2 == int(d[10])


def normalize_procedure_code(raw: str | None) -> str:
    return _digits(raw)
