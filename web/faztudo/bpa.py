"""Triador BPA: lê a remessa do BPA-Mag e aplica as críticas do SIGTAP e de cadastro (mesma lógica do BpaAnalyzer.cs)."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from decimal import Decimal

from .knowledge import KnowledgeBase
from .sigtap import SigtapRepository, SigtapRule, normalize_cid
from .text import format_brl
from .validators import is_valid_cns


def _cut(line: str, start: int, length: int) -> str:
    return line[start:start + length].strip() if start < len(line) else ""


def _int(value: str) -> int | None:
    return int(value) if value.isdigit() else None


def _date(value: str) -> date | None:
    if len(value) != 8 or not value.isdigit():
        return None
    try:
        return date(int(value[:4]), int(value[4:6]), int(value[6:]))
    except ValueError:
        return None


@dataclass
class BpaRecord:
    line_number: int
    individual: bool
    cnes: str
    competencia: str
    cbo: str
    folha: str
    sequencia: str
    procedure_code: str
    age_years: int | None
    quantity: int
    professional_cns: str = ""
    attendance_date: date | None = None
    patient_cns: str = ""
    sex: str = ""
    cid: str = ""
    patient_name: str = ""
    birth_date: date | None = None
    race_color: str = ""
    ethnicity: str = ""

    @property
    def instrument(self) -> str:
        return "BPA-I" if self.individual else "BPA-C"


@dataclass
class BpaFile:
    source_name: str
    has_header: bool
    header_competencia: str
    records: list[BpaRecord]
    warnings: list[str]


def parse_bpa(text: str, source_name: str) -> BpaFile:
    records: list[BpaRecord] = []
    has_header, header_comp, unknown = False, "", 0
    for n, raw in enumerate(text.splitlines(), start=1):
        line = raw.rstrip("\r\n")
        if not line:
            continue
        if line.startswith("01#BPA#"):
            has_header, header_comp = True, _cut(line, 7, 6)
        elif line.startswith("02") and len(line) >= 45:
            records.append(BpaRecord(n, False, _cut(line, 2, 7), _cut(line, 9, 6), _cut(line, 15, 6), _cut(line, 21, 3),
                                     _cut(line, 24, 2), _cut(line, 26, 10), _int(_cut(line, 36, 3)),
                                     _int(_cut(line, 39, 6)) or 0))
        elif line.startswith("03") and len(line) >= 94:
            records.append(BpaRecord(
                n, True, _cut(line, 2, 7), _cut(line, 9, 6), _cut(line, 30, 6), _cut(line, 44, 3), _cut(line, 47, 2),
                _cut(line, 49, 10), _int(_cut(line, 85, 3)), _int(_cut(line, 88, 6)) or 0,
                professional_cns=_cut(line, 15, 15), attendance_date=_date(_cut(line, 36, 8)),
                patient_cns=_cut(line, 59, 15), sex=_cut(line, 74, 1).upper(), cid=_cut(line, 81, 4),
                patient_name=_cut(line, 112, 30), birth_date=_date(_cut(line, 142, 8)),
                race_color=_cut(line, 150, 2), ethnicity=_cut(line, 152, 4)))
        else:
            unknown += 1
    warnings = []
    if not has_header:
        warnings.append('Arquivo sem cabeçalho "01#BPA#": pode não ser uma remessa do BPA-Mag.')
    if unknown:
        warnings.append(f"{unknown} linha(s) não reconhecida(s) como BPA-C (02) ou BPA-I (03) foram ignoradas.")
    return BpaFile(source_name, has_header, header_comp, records, warnings)


@dataclass
class Finding:
    record: BpaRecord
    rule: SigtapRule | None
    code: str
    knowledge_id: str
    description: str
    group_detail: str


@dataclass
class RootCauseGroup:
    error_code: str
    detail: str
    procedure: str
    knowledge_id: str
    title: str
    recommended_action: str
    occurrences: int
    procedures_affected: int
    impact: Decimal


@dataclass
class TriagemSummary:
    source_name: str
    detected_format: str
    competencia: str
    total_records: int
    valid_records: int
    findings: list[Finding]
    groups: list[RootCauseGroup]
    glosa_value: Decimal
    approved_value: Decimal
    warnings: list[str] = field(default_factory=list)
    line_values: dict[int, Decimal] = field(default_factory=dict)


def _fmt_comp(aaaamm: str) -> str:
    return f"{aaaamm[4:]}/{aaaamm[:4]}" if len(aaaamm) == 6 else aaaamm


def _mask(cns: str) -> str:
    return f"{cns[:3]}…{cns[-4:]}" if len(cns) >= 8 else cns


def _age_months(r: BpaRecord) -> int | None:
    if r.birth_date and r.attendance_date and r.birth_date <= r.attendance_date:
        months = (r.attendance_date.year - r.birth_date.year) * 12 + r.attendance_date.month - r.birth_date.month
        if r.attendance_date.day < r.birth_date.day:
            months -= 1
        return months
    return r.age_years * 12 + 6 if r.age_years is not None and r.age_years < 150 else None


def _fmt_age(months: int) -> str:
    return f"{months} meses" if months < 24 else f"{months // 12} anos"


def analyze(file: BpaFile, sigtap: SigtapRepository, kb: KnowledgeBase) -> TriagemSummary:
    findings: list[Finding] = []
    warnings = list(file.warnings)
    unknown: set[str] = set()
    mismatch = 0

    def add(r, rule, code, kid, desc, detail):
        findings.append(Finding(r, rule, code, kid, desc, detail))

    for r in file.records:
        if file.header_competencia and r.competencia != file.header_competencia:
            mismatch += 1
        rule = sigtap.find(r.procedure_code)
        if rule is None:
            if sigtap.is_official:
                add(r, None, "001", "sia-bpa-001", f"Procedimento {r.procedure_code} não existe na tabela SIGTAP importada.",
                    "procedimento inexistente")
            else:
                unknown.add(r.procedure_code)
        else:
            known = any(i in ("BPA-C", "BPA-I", "APAC", "RAAS", "AIH") for i in rule.allowed_instruments)
            if known and r.instrument not in rule.allowed_instruments:
                add(r, rule, "006/007", "sia-bpa-006",
                    f"Procedimento aceita {rule.instruments_display}, mas foi lançado em {r.instrument}.",
                    f"{r.instrument} não permitido")
            if "*" not in rule.allowed_cbos and r.cbo not in rule.allowed_cbos:
                cbo = r.cbo or "(vazio)"
                add(r, rule, "004", "sia-bpa-004",
                    f"CBO {cbo} não é permitido para o procedimento (aceitos: {rule.cbo_summary}).", f"CBO {cbo}")
            if r.individual:
                if rule.sex_restriction in ("F", "M") and r.sex in ("F", "M") and r.sex != rule.sex_restriction:
                    add(r, rule, "002", "sia-bpa-002",
                        f"Procedimento exclusivo do sexo {rule.sex_display.lower()}, paciente informado como '{r.sex}'.",
                        "sexo incompatível")
                cid = normalize_cid(r.cid)
                if rule.requires_cid and not cid:
                    add(r, rule, "CID", "sia-bpa-cid", "Procedimento exige CID-10 e o campo veio vazio.", "CID ausente")
                elif cid and rule.allowed_cids and cid not in rule.allowed_cids:
                    add(r, rule, "CID", "sia-bpa-cid",
                        f"CID {cid} não é compatível com o procedimento no SIGTAP ({len(rule.allowed_cids)} CIDs aceitos).",
                        f"CID {cid} incompatível")
            months = _age_months(r)
            limited = rule.min_age_months > 0 or rule.max_age_months < 9999
            if limited and months is not None and not (rule.min_age_months <= months <= rule.max_age_months):
                add(r, rule, "005", "sia-bpa-005",
                    f"Idade {_fmt_age(months)} fora da faixa do procedimento ({rule.age_range_display}).", "idade fora da faixa")

        if r.individual:
            if r.patient_cns and not is_valid_cns(r.patient_cns):
                add(r, rule, "CNS", "sia-bpa-cns",
                    f"CNS do paciente {_mask(r.patient_cns)} reprova no dígito verificador (Módulo 11).", "CNS do paciente inválido")
            if r.professional_cns and not is_valid_cns(r.professional_cns):
                add(r, rule, "CNS", "sia-bpa-cns",
                    f"CNS do profissional {_mask(r.professional_cns)} reprova no dígito verificador (Módulo 11).",
                    "CNS do profissional inválido")
            if r.birth_date and r.attendance_date and r.birth_date > r.attendance_date:
                add(r, rule, "005", "sia-bpa-005",
                    f"Data de nascimento ({r.birth_date:%d/%m/%Y}) posterior ao atendimento ({r.attendance_date:%d/%m/%Y}).",
                    "nascimento após atendimento")
            if r.race_color == "99":
                add(r, rule, "RAÇA", "sia-bpa-raca", "Raça/cor '99 - Sem informação' não é aceita.", "raça/cor 99")
            elif r.race_color == "05" and not r.ethnicity:
                add(r, rule, "RAÇA", "sia-bpa-raca", "Raça/cor 05 (Indígena) sem código de etnia.", "etnia ausente")

    if unknown:
        warnings.append(f"{len(unknown)} procedimento(s) não verificados contra o SIGTAP porque a tabela oficial não foi "
                        "importada (importe-a no Consultor SIGTAP para validar CBO, sexo, idade e instrumento de todos).")
    if mismatch:
        warnings.append(f"{mismatch} linha(s) com competência diferente do cabeçalho ({_fmt_comp(file.header_competencia)}).")

    def value(r: BpaRecord) -> Decimal:
        rule = sigtap.find(r.procedure_code)
        return (rule.unit_value if rule else Decimal(0)) * r.quantity

    rejected = {id(f.record): f.record for f in findings}
    valid = [r for r in file.records if id(r) not in rejected]

    grouped: dict[tuple, list[Finding]] = {}
    for f in findings:
        grouped.setdefault((f.knowledge_id, f.code, f.group_detail, f.record.procedure_code), []).append(f)
    groups = []
    for (kid, code, detail, proc), items in grouped.items():
        entry = kb.by_id(kid)
        lines = list({id(f.record): f.record for f in items}.values())
        groups.append(RootCauseGroup(
            error_code=code, detail=detail[:1].upper() + detail[1:], procedure=items[0].rule.name if items[0].rule else proc,
            knowledge_id=kid, title=entry.title if entry else detail,
            recommended_action=entry.guided_steps[0].action_title if entry and entry.guided_steps else "",
            occurrences=len(items), procedures_affected=sum(r.quantity for r in lines),
            impact=sum((value(r) for r in lines), Decimal(0))))
    groups.sort(key=lambda g: (-g.impact, -g.occurrences))

    return TriagemSummary(
        source_name=file.source_name,
        detected_format="Remessa BPA-Mag" if file.has_header else "Arquivo BPA sem cabeçalho",
        competencia=_fmt_comp(file.header_competencia or (file.records[0].competencia if file.records else "")),
        total_records=len(file.records), valid_records=len(valid),
        findings=sorted(findings, key=lambda f: f.record.line_number), groups=groups,
        glosa_value=sum((value(r) for r in rejected.values()), Decimal(0)),
        approved_value=sum((value(r) for r in valid), Decimal(0)),
        warnings=warnings,
        line_values={r.line_number: value(r) for r in file.records})


def _excel_cell(value: str) -> str:
    """Igual ao TriagemCsvExporter.cs: protege contra fórmula, preserva zeros à esquerda e só põe aspas quando precisa."""
    if value[:1] in ("=", "+", "-", "@"):
        value = "'" + value
    if len(value) > 1 and value[0] == "0" and value.isdigit():
        return f'="{value}"'
    if any(c in value for c in (";", '"', "\n", "\r")):
        return '"' + value.replace('"', '""') + '"'
    return value


def findings_to_csv(summary: TriagemSummary, kb: KnowledgeBase) -> bytes:
    """CSV para o Excel brasileiro: ';', vírgula decimal e UTF-8 com BOM."""
    rows: list[str] = []

    def writerow(cells: list[str]) -> None:
        rows.append(";".join(cells))

    writerow(["Linha", "Crítica", "Instrumento", "CNES", "Competência", "Procedimento", "Nome do procedimento", "CBO",
                     "Referência", "Quantidade", "O que está errado", "Valor estimado (R$)", "Como resolver"])
    for f in summary.findings:
        r = f.record
        entry = kb.by_id(f.knowledge_id)
        ref = f"Folha {r.folha}/{r.sequencia}" + (f" · {r.patient_name or _mask(r.patient_cns)}" if r.individual else "")
        val = summary.line_values.get(r.line_number, Decimal(0))
        writerow([_excel_cell(c) for c in [
            str(r.line_number), f.code, r.instrument, r.cnes, _fmt_comp(r.competencia), r.procedure_code,
            f.rule.name if f.rule else "(não encontrado)", r.cbo, ref, str(r.quantity), f.description,
            format_brl(val).replace("R$ ", ""),
            entry.guided_steps[0].action_title if entry and entry.guided_steps else ""]])
    return ("﻿" + "\r\n".join(rows) + "\r\n").encode("utf-8")
