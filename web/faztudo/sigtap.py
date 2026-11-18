"""Regras do SIGTAP: base de exemplo, importação da Tabela Unificada oficial (zip ou arquivos) e consulta.

Mesma lógica do SigtapRepository.cs. Nada é gravado em disco a partir de uploads: o zip é lido em memória.
"""

from __future__ import annotations

import gzip
import io
import json
import zipfile
from dataclasses import asdict, dataclass, field
from decimal import Decimal
from pathlib import Path

from . import SHARED_DIR
from .text import normalize, terms
from .validators import normalize_procedure_code


DEFAULT_LAYOUTS: dict[str, list[tuple[str, int]]] = {
    "tb_procedimento": [
        ("CO_PROCEDIMENTO", 10), ("NO_PROCEDIMENTO", 250), ("TP_COMPLEXIDADE", 1), ("TP_SEXO", 1),
        ("QT_MAXIMA_EXECUCAO", 4), ("QT_DIAS_PERMANENCIA", 4), ("QT_PONTOS", 4),
        ("VL_IDADE_MINIMA", 4), ("VL_IDADE_MAXIMA", 4),
        ("VL_SH", 12), ("VL_SA", 12), ("VL_SP", 12),
        ("CO_FINANCIAMENTO", 2), ("CO_RUBRICA", 6), ("QT_TEMPO_PERMANENCIA", 4), ("DT_COMPETENCIA", 6),
    ],
    "rl_procedimento_ocupacao": [("CO_PROCEDIMENTO", 10), ("CO_OCUPACAO", 6), ("DT_COMPETENCIA", 6)],
    "rl_procedimento_registro": [("CO_PROCEDIMENTO", 10), ("CO_REGISTRO", 2), ("DT_COMPETENCIA", 6)],
    "rl_procedimento_cid": [("CO_PROCEDIMENTO", 10), ("CO_CID", 4), ("ST_PRINCIPAL", 1), ("DT_COMPETENCIA", 6)],
    "rl_procedimento_compativel": [("CO_PROCEDIMENTO_PRINCIPAL", 10), ("CO_REGISTRO_PRINCIPAL", 2),
                                   ("CO_PROCEDIMENTO_COMPATIVEL", 10), ("CO_REGISTRO_COMPATIVEL", 2),
                                   ("TP_COMPATIBILIDADE", 1), ("QT_PERMITIDA", 4), ("DT_COMPETENCIA", 6)],
    "rl_procedimento_regra_cond": [("CO_PROCEDIMENTO", 10), ("CO_REGRA_CONDICIONADA", 4)],
    "tb_regra_condicionada": [("CO_REGRA_CONDICIONADA", 4), ("NO_REGRA_CONDICIONADA", 150), ("DS_REGRA_CONDICIONADA", 4000)],
    "rl_procedimento_detalhe": [("CO_PROCEDIMENTO", 10), ("CO_DETALHE", 3), ("DT_COMPETENCIA", 6)],
    "tb_detalhe": [("CO_DETALHE", 3), ("NO_DETALHE", 100), ("DT_COMPETENCIA", 6)],
}

REGISTRO = {"01": "BPA-C", "02": "BPA-I", "03": "AIH", "04": "AIH", "05": "AIH", "06": "APAC", "07": "APAC",
            "08": "RAAS", "09": "RAAS"}
FINANCIAMENTO = {"01": "PAB / Atenção Básica", "02": "Assistência Farmacêutica", "04": "FAEC", "05": "Incentivo - MAC",
                 "06": "MAC", "07": "Vigilância em Saúde", "": "SUS"}


def normalize_cid(cid: str | None) -> str:
    return "".join(c for c in (cid or "") if c.isalnum()).upper()


def _format_months(months: int) -> str:
    if months < 12:
        return f"{months}m"
    years, rem = divmod(months, 12)
    return f"{years}a" if rem == 0 else f"{years}a {rem}m"


@dataclass
class SigtapRule:
    code: str
    name: str
    sex_restriction: str          # I = indiferente, F, M
    min_age_months: int
    max_age_months: int           # 9999 = sem limite
    allowed_instruments: list[str]
    allowed_cbos: list[str]       # ["*"] = qualquer CBO
    requires_cid: bool
    unit_value: Decimal
    financing: str
    required_service_classification: str = ""
    allowed_cids: list[str] = field(default_factory=list)
    max_quantity: int = 0
    # Procedimentos secundários previstos (rl_procedimento_compativel): [código, quantidade permitida, tipo]
    compatibles: list[list] = field(default_factory=list)
    conditioned_rules: list[str] = field(default_factory=list)   # códigos de tb_regra_condicionada
    details: list[str] = field(default_factory=list)             # códigos de tb_detalhe

    @property
    def formatted_code(self) -> str:
        c = self.code
        return f"{c[:2]}.{c[2:4]}.{c[4:6]}.{c[6:9]}-{c[9]}" if len(c) == 10 else c

    @property
    def sex_display(self) -> str:
        return {"F": "Feminino", "M": "Masculino"}.get(self.sex_restriction, "Ambos")

    @property
    def age_range_display(self) -> str:
        low = "0m" if self.min_age_months <= 0 else _format_months(self.min_age_months)
        high = "130a" if self.max_age_months >= 1560 else _format_months(self.max_age_months)
        return f"{low} até {high}"

    @property
    def cbo_summary(self) -> str:
        if "*" in self.allowed_cbos:
            return "Amplo (Multiprofissional)"
        extra = len(self.allowed_cbos) - 5
        return ", ".join(self.allowed_cbos[:5]) + (f" (+{extra})" if extra > 0 else "")

    @property
    def instruments_display(self) -> str:
        return ", ".join(self.allowed_instruments)

    @property
    def is_mac(self) -> bool:
        f = self.financing.upper()
        return "MAC" in f and "FAEC" not in f

    def to_json(self) -> dict:
        d = asdict(self)
        d["unit_value"] = str(self.unit_value)
        return d

    @staticmethod
    def from_json(d: dict) -> "SigtapRule":
        d = dict(d)
        d["unit_value"] = Decimal(str(d["unit_value"]))
        return SigtapRule(**d)


def _rule_from_seed(p: dict) -> SigtapRule:
    return SigtapRule(
        code=p["code"], name=p["name"], sex_restriction=p["sexRestriction"],
        min_age_months=p["minAgeMonths"], max_age_months=p["maxAgeMonths"],
        allowed_instruments=list(p["allowedInstruments"]), allowed_cbos=list(p["allowedCbos"]),
        requires_cid=p["requiresCid"], unit_value=Decimal(str(p["unitValue"])), financing=p["financing"],
        required_service_classification=p.get("requiredServiceClassification", ""),
        allowed_cids=list(p.get("allowedCids", [])), max_quantity=p.get("maxQuantity", 0),
    )


class _Layout:
    """Layout posicional de uma tabela; usa o <tabela>_layout.txt do zip quando existe."""

    def __init__(self, columns: dict[str, tuple[int, int]]):
        self.columns = columns

    @staticmethod
    def load(files: dict[str, bytes], table: str) -> "_Layout":
        raw = files.get(f"{table}_layout.txt")
        if raw:
            cols: dict[str, tuple[int, int]] = {}
            for line in raw.decode("latin-1").splitlines():
                parts = [p.strip() for p in line.replace(";", ",").replace("\t", ",").split(",")]
                if len(parts) >= 3 and parts[1].isdigit() and parts[2].isdigit() and int(parts[1]) > 0 and int(parts[2]) > 0:
                    cols[parts[0].upper()] = (int(parts[2]) - 1, int(parts[1]))
            if cols:
                return _Layout(cols)
        cols, offset = {}, 0
        for name, length in DEFAULT_LAYOUTS[table]:
            cols[name] = (offset, length)
            offset += length
        return _Layout(cols)

    def read(self, line: str, column: str) -> str:
        col = self.columns.get(column)
        if not col or col[0] >= len(line):
            return ""
        start, length = col
        return line[start:start + length].strip()


class SigtapRepository:
    def __init__(self, procedures: dict[str, SigtapRule] | None = None, label: str | None = None, official: bool = False,
                 competencia: str = "", version: str = ""):
        if procedures is None:
            procedures = load_seed()
            label = "Base de exemplo embarcada (importe a tabela oficial do SIGTAP)"
        self.procedures = procedures
        self.label = label or ""
        self.is_official = official
        self.competencia = competencia  # AAAAMM
        self.version = version
        self.rule_names: dict[str, tuple[str, str]] = {}  # regra condicionada → (nome, descrição)
        self.detail_names: dict[str, str] = {}            # detalhe → nome

    @property
    def competencia_display(self) -> str:
        c = self.competencia
        return f"{c[4:]}/{c[:4]}" if len(c) == 6 else c

    # ------------------------------------------------------------ consulta
    def find(self, raw_code: str | None) -> SigtapRule | None:
        code = normalize_procedure_code(raw_code)
        rule = self.procedures.get(code)
        if rule is None and len(code) == 9:  # códigos de APAC no relatório do SIA vêm sem o dígito verificador
            rule = next((p for c, p in self.procedures.items() if c.startswith(code)), None)
        return rule

    def search(self, query: str | None, instrument: str | None = None, limit: int = 250) -> list[SigtapRule]:
        source = sorted(self.procedures.values(), key=lambda p: p.code)
        if instrument and instrument != "Todos":
            source = [p for p in source if instrument in p.allowed_instruments]
        if not query or not query.strip():
            return source[:limit]
        digits = normalize_procedure_code(query)
        is_code = len(digits) >= 4 and all(c.isdigit() or c in ".- " for c in query)
        wanted = terms(query)
        result = []
        for p in source:
            if is_code:
                ok = digits in p.code
            else:
                ok = all(t in normalize(p.name) or t in normalize(p.financing) or
                         t in normalize(p.required_service_classification) or any(t in c for c in p.allowed_cbos)
                         for t in wanted)
            if ok:
                result.append(p)
                if len(result) >= limit:
                    break
        return result

    # ------------------------------------------------------------ importação
    @staticmethod
    def from_zip(data: bytes) -> "SigtapRepository":
        try:
            archive = zipfile.ZipFile(io.BytesIO(data))
        except zipfile.BadZipFile as exc:
            raise ValueError("Esse arquivo não é um .zip válido. Baixe a Tabela Unificada no site do SIGTAP.") from exc
        files: dict[str, bytes] = {}
        for info in archive.infolist():
            name = Path(info.filename).name.lower()
            if name.endswith(".txt") and (name.startswith(("tb_procedimento", "rl_procedimento_", "tb_regra_condicionada",
                                                           "tb_detalhe"))):
                files[name] = archive.read(info)
        return SigtapRepository.from_files(files)

    @staticmethod
    def from_files(files: dict[str, bytes]) -> "SigtapRepository":
        if "tb_procedimento.txt" not in files:
            raise ValueError("Não encontrei 'tb_procedimento.txt'. Confira se é o zip da Tabela Unificada do SIGTAP.")

        def relation(table: str, column: str, transform=lambda v: v, include=None) -> dict[str, set[str]]:
            out: dict[str, set[str]] = {}
            raw = files.get(f"{table}.txt")
            if not raw:
                return out
            layout = _Layout.load(files, table)
            for line in raw.decode("latin-1").splitlines():
                proc, value = layout.read(line, "CO_PROCEDIMENTO"), layout.read(line, column)
                if len(proc) != 10 or not value or (include and not include(layout, line)):
                    continue
                out.setdefault(proc, set()).add(transform(value))
            return out

        cbos = relation("rl_procedimento_ocupacao", "CO_OCUPACAO")
        regs = relation("rl_procedimento_registro", "CO_REGISTRO", lambda r: REGISTRO.get(r, f"REG-{r}"))
        cids = relation("rl_procedimento_cid", "CO_CID", normalize_cid,
                        lambda layout, line: layout.read(line, "ST_PRINCIPAL") in ("", "S"))

        rule_codes = relation("rl_procedimento_regra_cond", "CO_REGRA_CONDICIONADA")
        detail_codes = relation("rl_procedimento_detalhe", "CO_DETALHE")

        compatibles: dict[str, list[list]] = {}
        if files.get("rl_procedimento_compativel.txt"):
            comp_layout = _Layout.load(files, "rl_procedimento_compativel")
            for line in files["rl_procedimento_compativel.txt"].decode("latin-1").splitlines():
                principal = comp_layout.read(line, "CO_PROCEDIMENTO_PRINCIPAL")
                secondary = comp_layout.read(line, "CO_PROCEDIMENTO_COMPATIVEL")
                if len(principal) != 10 or len(secondary) != 10:
                    continue
                qty = comp_layout.read(line, "QT_PERMITIDA")
                entry = [secondary, int(qty) if qty.isdigit() else 0, comp_layout.read(line, "TP_COMPATIBILIDADE")]
                if entry not in compatibles.setdefault(principal, []):
                    compatibles[principal].append(entry)

        def names(table: str, code_col: str, name_col: str, desc_col: str | None = None) -> dict:
            out = {}
            raw = files.get(f"{table}.txt")
            if not raw:
                return out
            lay = _Layout.load(files, table)
            for line in raw.decode("latin-1").splitlines():
                code = lay.read(line, code_col)
                if code:
                    name = lay.read(line, name_col)
                    out[code] = (name, lay.read(line, desc_col)) if desc_col else name
            return out

        layout = _Layout.load(files, "tb_procedimento")
        procedures: dict[str, SigtapRule] = {}
        competencia = ""
        for line in files["tb_procedimento.txt"].decode("latin-1").splitlines():
            code = layout.read(line, "CO_PROCEDIMENTO")
            if len(code) != 10 or not code.isdigit():
                continue

            def num(column: str) -> int:
                v = layout.read(line, column)
                return int(v) if v.isdigit() else 0

            if not competencia:
                comp = layout.read(line, "DT_COMPETENCIA")
                competencia = comp if len(comp) == 6 and comp.isdigit() else ""
            instruments = sorted(regs.get(code, set())) or ["SIA"]
            proc_cids = sorted(cids.get(code, set()))
            sex = layout.read(line, "TP_SEXO").upper()
            max_age = num("VL_IDADE_MAXIMA")
            procedures[code] = SigtapRule(
                code=code,
                name=title_pt(layout.read(line, "NO_PROCEDIMENTO")),
                sex_restriction=sex if sex in ("F", "M") else "I",
                min_age_months=num("VL_IDADE_MINIMA"),
                max_age_months=9999 if max_age <= 0 or max_age >= 9999 else max_age,
                allowed_instruments=instruments,
                allowed_cbos=sorted(cbos.get(code, set())) or ["*"],
                requires_cid="APAC" in instruments or bool(proc_cids),
                allowed_cids=proc_cids,
                unit_value=Decimal(num("VL_SA")) / 100,
                financing=FINANCIAMENTO.get(layout.read(line, "CO_FINANCIAMENTO"),
                                            f"FIN-{layout.read(line, 'CO_FINANCIAMENTO')}"),
                max_quantity=num("QT_MAXIMA_EXECUCAO"),
                compatibles=compatibles.get(code, []),
                conditioned_rules=sorted(rule_codes.get(code, set())),
                details=sorted(detail_codes.get(code, set())),
            )

        if not procedures:
            raise ValueError("Nenhum procedimento válido em 'tb_procedimento.txt'.")
        repo = SigtapRepository(procedures, _official_label(competencia, len(procedures)), official=True,
                                competencia=competencia)
        repo.rule_names = {k: (title_pt(v[0]).replace("Secudários", "Secundários"), v[1])
                           for k, v in names("tb_regra_condicionada", "CO_REGRA_CONDICIONADA", "NO_REGRA_CONDICIONADA",
                                             "DS_REGRA_CONDICIONADA").items()}
        repo.detail_names = names("tb_detalhe", "CO_DETALHE", "NO_DETALHE")
        return repo

    def detail_text(self, rule: SigtapRule) -> list[str]:
        return [self.detail_names[d] for d in rule.details if d in self.detail_names]

    def rule_texts(self, rule: SigtapRule) -> list[tuple[str, str]]:
        return [self.rule_names[r] for r in rule.conditioned_rules if r in self.rule_names]

    # ------------------------------------------------------------ pacote pronto (json.gz, carrega em segundos)
    def to_bundle(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = {"label": self.label, "competencia": self.competencia, "version": self.version,
                   "rule_names": self.rule_names, "detail_names": self.detail_names,
                   "procedures": [p.to_json() for p in self.procedures.values()]}
        temp = path.with_suffix(path.suffix + ".tmp")
        with gzip.open(temp, "wt", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False)
        temp.replace(path)

    @staticmethod
    def from_bundle(path: Path) -> "SigtapRepository":
        with gzip.open(path, "rt", encoding="utf-8") as fh:
            payload = json.load(fh)
        procs = {p["code"]: SigtapRule.from_json(p) for p in payload["procedures"]}
        repo = SigtapRepository(procs, payload["label"], official=True, competencia=payload.get("competencia", ""),
                                version=payload.get("version", ""))
        repo.rule_names = {k: tuple(v) for k, v in payload.get("rule_names", {}).items()}
        repo.detail_names = payload.get("detail_names", {})
        return repo


def _official_label(competencia: str, count: int) -> str:
    text = (f"SIGTAP oficial competência {competencia[4:]}/{competencia[:4]} ({count:,} procedimentos)"
            if competencia else f"SIGTAP oficial ({count:,} procedimentos)")
    return text.replace(",", ".")


_LOWER_WORDS = {"de", "da", "do", "das", "dos", "e", "em", "na", "no", "nas", "nos", "a", "o", "as", "os", "com", "por",
                "para", "p/", "ou", "ao", "aos", "à", "às", "x"}


_UPPER_WORDS = {"oci", "apac", "sus", "cid", "hiv", "aids", "cdk", "ii", "iii", "iv", "vi", "vii", "viii", "ix",
                "uti", "utin", "caps", "rx", "pet-ct", "dna", "rna", "pcr", "ivas", "dst", "ist", "tb"}


def title_pt(text: str) -> str:
    """'CONSULTA MEDICA EM ATENCAO ESPECIALIZADA' → 'Consulta Medica em Atencao Especializada' (siglas em maiúsculas)."""
    def fix(i: int, w: str) -> str:
        core = w.strip("()-,.;:/")
        if core in _UPPER_WORDS:
            return w.upper()
        if i > 0 and w in _LOWER_WORDS:
            return w
        return w[:1].upper() + w[1:]
    return " ".join(fix(i, w) for i, w in enumerate(text.lower().split()))


def load_seed() -> dict[str, SigtapRule]:
    raw = json.loads((SHARED_DIR / "sigtap-exemplo.json").read_text(encoding="utf-8"))
    return {p["code"]: _rule_from_seed(p) for p in raw["procedures"]}
