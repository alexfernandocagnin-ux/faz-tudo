"""Solucionador SIA: lê o R0350 (Relatório de Ocorrências na Consistência) e explica cada ocorrência.

Mesma lógica de ConsistencyReport.cs + ConsistencySolver.cs: para cada código, diz ONDE resolver (BPA, APAC,
CNES, FPO ou SIA), PARA QUEM mandar, o que se vê nas linhas do relatório + regras do SIGTAP, e o passo a passo.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import date

from .knowledge import GuidedStep, KnowledgeBase, KnowledgeEntry
from .sigtap import SigtapRepository
from .text import format_brl

GROUP_MEANINGS = {
    "GRV": "Erro grave: impede o cálculo do valor bruto. Acerte na origem (FPO ou CNES).",
    "ACE": "Acerto: o SIA corrigiu algo na produção. Execute a consistência de novo.",
    "AVS": "Aviso de correção na produção. Execute a consistência de novo.",
    "NOR": "Problema na produção: não será aprovado enquanto o problema persistir.",
    "ADV": "Advertência: vale verificar, mas não gera glosa.",
}
GROUP_ORDER = {"GRV": 0, "NOR": 1, "AVS": 2, "ACE": 3, "ADV": 4}
MONTHS = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"]

AGE_ALTERNATIVES = {
    "0203010086": ("0203010019", "se a paciente está fora da faixa de rastreamento e o exame tem indicação clínica (não é rastreamento)"),
}

_ESC = re.compile(r"\x1b[^A-Z@]*[A-Z@]")
_TOTAL = re.compile(r"^\s*(GRV|ACE|AVS|NOR|ADV)\s+(\S+)\s+(\d+)\s+(.+?)\s*$")
_CNES = re.compile(r"^\s*CNES\s*:\s*(\d{7})\s*(.*?)\s*$")
_RECORD = re.compile(r"^\s*(BPAI|BPAC|BPA|APAC|RAAS\S*)\s+(\d{2}/\d{4})\s+(.*?)\s*$")
_TITLE = re.compile(r"RELATORIO DE OCORRENCIAS NA CONSISTENCIA\s*-\s*([A-Z]{3})/(\d{4})")
_DATETIME = re.compile(r"^\s*(\d{2}/\d{2}/\d{4}).*?(\d{2}:\d{2}:\d{2})\s*$")


@dataclass
class OccurrenceTotal:
    group: str
    code: str
    quantity: int
    description: str


@dataclass
class RelatedApac:
    patient_document: str
    apac_number: str
    procedure_code: str
    start: date | None
    end: date | None
    exit_date: date | None
    exit_reason: str
    cnes: str
    competencia: str
    movement: str


@dataclass
class OccurrenceRecord:
    cnes: str
    cnes_name: str
    instrument: str
    competencia: str
    reference: str
    procedure_code: str
    cbo: str
    third_column: str
    description: str
    code: str
    group: str
    line_number: int
    related_apacs: list[RelatedApac] = field(default_factory=list)


@dataclass
class ConsistencyReport:
    source_name: str
    orgao: str
    competencia: str
    bdsia_version: str
    generated_at: str
    totals: list[OccurrenceTotal]
    records: list[OccurrenceRecord]


def looks_like_consistency_report(text: str) -> bool:
    return "OCORRENCIAS NA CONSISTENCIA" in text.upper()


def decode_report(data: bytes) -> str:
    return data.decode("latin-1")


def _date(v: str) -> date | None:
    try:
        return date(int(v[:4]), int(v[4:6]), int(v[6:8])) if len(v) == 8 and v.isdigit() else None
    except ValueError:
        return None


def _parse_related(line: str) -> RelatedApac | None:
    t = line.split()
    if len(t) not in (9, 10) or not t[0].isdigit():
        return None
    o = 1 if len(t) == 10 else 0
    return RelatedApac(t[0], t[1], t[2], _date(t[3]), _date(t[4]), _date(t[5]) if o else None,
                       t[5 + o], t[6 + o], t[7 + o], t[8 + o])


def _parse_record(m: re.Match, totals: list[OccurrenceTotal], cnes: str, cnes_name: str, n: int) -> OccurrenceRecord:
    rest = m.group(3)
    candidates = [t for t in totals
                  if rest.endswith(t.description) or (len(t.description) > 20 and t.description[:20] in rest)]
    total = max(candidates, key=lambda t: len(t.description)) if candidates else None
    if total:
        at = rest.rfind(total.description)
        if at < 0:
            at = rest.find(total.description[:20])
        prefix, description = rest[:at], total.description
    else:
        tokens = rest.split()
        first_text = next((i for i, t in enumerate(tokens) if not t.replace("/", "").replace("-", "").isdigit()), None)
        prefix = " ".join(tokens if first_text is None else tokens[:first_text])
        description = "" if first_text is None else " ".join(tokens[first_text:])

    parts = prefix.split()
    reference = parts[0] if parts else ""
    procedure = parts[1] if len(parts) > 1 else ""
    remaining = parts[2:]
    cbo = third = ""
    if len(remaining) >= 2:
        cbo, third = remaining[0], remaining[1]
    elif len(remaining) == 1:
        if len(remaining[0]) == 6:
            cbo = remaining[0]
        else:
            third = remaining[0]
    return OccurrenceRecord(cnes, cnes_name, m.group(1), m.group(2), reference, procedure, cbo, third, description,
                            total.code if total else "", total.group if total else "", n)


def parse_report(text: str, source_name: str) -> ConsistencyReport:
    if not looks_like_consistency_report(text):
        raise ValueError("Este arquivo não parece ser o Relatório de Ocorrências na Consistência do SIA (R0350). "
                         "No SIA, ele é gerado na consistência e fica na pasta do processamento (ex.: R0350.OUT).")
    lines = _ESC.sub("", text).replace("\f", "\n").replace("\r", "").split("\n")
    orgao = competencia = version = generated = ""
    totals: list[OccurrenceTotal] = []
    records: list[OccurrenceRecord] = []
    cnes = cnes_name = ""
    in_related = False
    block: list[OccurrenceRecord] = []

    for i, line in enumerate(lines, start=1):
        s = line.strip()
        if not s:
            in_related = False
            block = []
            continue
        if not version and s.startswith("BDSIA"):
            version = re.split(r"[* ]", s)[0]
            continue
        if not orgao and "SISTEMA DE INFORMACOES AMBULATORIAIS" in s:
            orgao = s[:s.index("SISTEMA DE INFORMACOES")].strip()
            continue
        title = _TITLE.search(s)
        if title:
            if not competencia:
                month = MONTHS.index(title.group(1)) + 1 if title.group(1) in MONTHS else 0
                competencia = f"{month:02d}/{title.group(2)}" if month else f"{title.group(1)}/{title.group(2)}"
                dt = _DATETIME.match(s)
                generated = f"{dt.group(1)} {dt.group(2)}" if dt else ""
            continue
        total = _TOTAL.match(line)
        if total and not records and not cnes:
            totals.append(OccurrenceTotal(total.group(1), total.group(2), int(total.group(3)), total.group(4).strip()))
            continue
        cm = _CNES.match(line)
        if cm:
            cnes, cnes_name = cm.group(1), cm.group(2)
            continue
        if s.startswith("CNS/CPF"):
            in_related = True
            continue
        if in_related:
            related = _parse_related(s)
            if related:
                for r in block:
                    r.related_apacs.append(related)
            continue
        rm = _RECORD.match(line)
        if rm and cnes:
            rec = _parse_record(rm, totals, cnes, cnes_name, i)
            if block and block[-1].reference != rec.reference:
                block = []
            block.append(rec)
            records.append(rec)

    return ConsistencyReport(source_name, orgao, competencia, version, generated, totals, records)


# ====================================================================== solucionador

@dataclass
class ResolvedArea:
    key: str
    label: str
    who: str
    action: str
    details: list[str]
    is_alternative: bool


@dataclass
class ProblemExplanation:
    code: str
    group: str
    group_meaning: str
    description: str
    quantity: int
    title: str
    knowledge: KnowledgeEntry | None
    findings: list[str]
    steps: tuple[GuidedStep, ...]
    records: list[OccurrenceRecord]
    areas: list[ResolvedArea]

    @property
    def quantity_display(self) -> str:
        return "1 ocorrência" if self.quantity == 1 else f"{self.quantity} ocorrências"

    @property
    def is_blocking(self) -> bool:
        return self.group in ("GRV", "NOR")

    @property
    def main_area(self) -> ResolvedArea | None:
        return self.areas[0] if self.areas else None


@dataclass
class SolverResult:
    report: ConsistencyReport
    problems: list[ProblemExplanation]
    headline: str
    notes: list[str]

    @property
    def total_occurrences(self) -> int:
        return sum(p.quantity for p in self.problems)

    @property
    def establishment_count(self) -> int:
        return len({r.cnes for r in self.report.records})

    def to_plain_text(self) -> str:
        out = [f"Relatório de consistência do SIA — {self.report.orgao} — competência {self.report.competencia}",
               self.headline]
        for n, p in enumerate(self.problems, start=1):
            out += ["", f"{n}. [{p.code}] {p.title} — {p.quantity_display}"]
            for a in p.areas:
                prefix = "   → Também pode ser" if a.is_alternative else "   → Onde resolver"
                out.append(f"{prefix}: {a.label}. Quem resolve: {a.who} {a.action}")
                out += [f"      - {d}" for d in a.details]
            out += [f"   • {f}" for f in p.findings]
            out += [f"   {s.step_number}) {s.action_title}: {s.detailed_instruction}" for s in p.steps]
        out.append("")
        out += [f"Obs.: {note}" for note in self.notes]
        return "\n".join(out) + "\n"


def _mask(doc: str) -> str:
    return f"{doc[:3]}…{doc[-4:]}" if len(doc) >= 8 else doc


def _join(items: list[str], limit: int) -> str:
    return ", ".join(items) if len(items) <= limit else ", ".join(items[:limit]) + f" e mais {len(items) - limit}"


def _plural(n: int, one: str, many: str) -> str:
    return f"1 {one}" if n == 1 else f"{n} {many}"


def _fmt(d: date | None) -> str:
    return d.strftime("%d/%m/%Y") if d else "?"


class ConsistencySolver:
    def __init__(self, sigtap: SigtapRepository, kb: KnowledgeBase):
        self.sigtap = sigtap
        self.kb = kb

    def solve(self, report: ConsistencyReport) -> SolverResult:
        totals = report.totals or self._totals_from_records(report)
        problems = []
        for t in totals:
            records = [r for r in report.records if r.code == t.code and r.description == t.description]
            entry = self.kb.by_id(self.kb.sia_codes.get(t.code))
            problems.append(ProblemExplanation(
                code=t.code, group=t.group, group_meaning=GROUP_MEANINGS.get(t.group, ""), description=t.description,
                quantity=t.quantity, title=entry.title if entry else t.description.capitalize(), knowledge=entry,
                findings=self._findings(t.code, records, entry is None, t.quantity),
                steps=entry.guided_steps if entry else (), records=records,
                areas=self._areas(entry, t, records)))
        problems.sort(key=lambda p: (GROUP_ORDER.get(p.group, 9), -p.quantity))

        total = sum(p.quantity for p in problems)
        blocking = sum(p.quantity for p in problems if p.is_blocking)
        establishments = len({r.cnes for r in report.records})
        if total == 0:
            headline = f"Recebi o relatório de {report.competencia}: nenhuma ocorrência na consistência."
        else:
            headline = (f"Recebi o relatório de {report.competencia}: {total} ocorrência(s) de {len(problems)} tipo(s) "
                        f"em {establishments} estabelecimento(s).")
            headline += (" Todas impedem a aprovação até serem corrigidas." if blocking == total
                         else f" {blocking} impedem a aprovação; o resto é aviso." if blocking
                         else " Nenhuma impede a aprovação.")
        notes = []
        if not self.sigtap.is_official:
            notes.append("Estou usando a base de exemplo do SIGTAP. Importe a tabela oficial da competência (Consultor SIGTAP) "
                         "para eu dizer faixas de idade, quantidades máximas e CIDs aceitos de todos os procedimentos.")
        notes.append("Depois de corrigir na origem (prontuário, BPA-Mag, APAC-Mag, CNES ou FPO), reimporte e rode a "
                     "consistência de novo.")
        return SolverResult(report, problems, headline, notes)

    @staticmethod
    def _totals_from_records(report: ConsistencyReport) -> list[OccurrenceTotal]:
        counts: dict[tuple, int] = {}
        for r in report.records:
            counts[(r.group, r.code, r.description)] = counts.get((r.group, r.code, r.description), 0) + 1
        return [OccurrenceTotal(g, c, n, d) for (g, c, d), n in counts.items()]

    # ------------------------------------------------------------ onde resolver
    def _areas(self, entry: KnowledgeEntry | None, total: OccurrenceTotal, records: list[OccurrenceRecord]) -> list[ResolvedArea]:
        keys = ([entry.area] + ([entry.secondary_area] if entry.secondary_area else [])) if entry else self._infer(total)
        result: list[ResolvedArea] = []
        for raw in keys:
            key = self._production_area(records) if raw == "PRODUCAO" else raw
            info = self.kb.areas.get(key)
            if not info or any(a.key == key for a in result):
                continue
            if key == "SIA":
                details: list[str] = []
            elif key == "FPO":
                details = self._send_to(records, key) + self._fpo_advice(records)
            else:
                details = self._send_to(records, key)
            result.append(ResolvedArea(key, info.label, info.who, info.action, details, is_alternative=bool(result)))
        return result

    @staticmethod
    def _infer(total: OccurrenceTotal) -> list[str]:
        d = total.description
        if any(k in d for k in ("FPO", "ORCAMENTO", "TETO")):
            return ["FPO"]
        if any(k in d for k in ("CNES", "HABILITA", "SERVICO", "EQUIPAMENTO", "ESTABELECIMENTO")):
            return ["CNES"]
        return ["FPO", "CNES"] if total.group == "GRV" else ["PRODUCAO"]

    @staticmethod
    def _production_area(records: list[OccurrenceRecord]) -> str:
        return "APAC" if sum(r.instrument == "APAC" for r in records) > len(records) / 2 else "BPA"

    @staticmethod
    def _send_to(records: list[OccurrenceRecord], key: str) -> list[str]:
        out = []
        for (cnes, name) in dict.fromkeys((r.cnes, r.cnes_name) for r in records):
            group = [r for r in records if r.cnes == cnes]
            refs = len({r.reference for r in group})
            what = (_plural(refs, "APAC", "APACs") if key == "APAC" else _plural(refs, "ficha", "fichas") if key == "BPA"
                    else _plural(len(group), "ocorrência", "ocorrências"))
            out.append(f"CNES {cnes} — {name} ({what})")
        return out

    def _fpo_advice(self, records: list[OccurrenceRecord]) -> list[str]:
        out = []
        for (cnes, proc) in dict.fromkeys((r.cnes, r.procedure_code) for r in records):
            qty = sum(1 for r in records if r.cnes == cnes and r.procedure_code == proc)
            rule = self.sigtap.find(proc)
            if rule is None:
                out.append(f"Procedimento {proc} (CNES {cnes}): importe o SIGTAP oficial para eu dizer se é MAC ou não MAC e o valor.")
                continue
            section = "na parte MAC" if rule.is_mac else f"na parte não MAC ({rule.financing})"
            value = (f" e valor de {format_brl(rule.unit_value * qty)} ({qty} × {format_brl(rule.unit_value)} do SIGTAP)"
                     if rule.unit_value > 0 else
                     " (o valor unitário no SIGTAP é zero: confira se o procedimento é pago por incentivo antes de programar valor)")
            out.append(f"FPO Magnético → CNES {cnes}, competência da produção: {section}, incluir {rule.formatted_code} "
                       f"({rule.name}) com meta física de pelo menos {qty}{value}.")
        return out

    # ------------------------------------------------------------ o que eu vi
    def _findings(self, code: str, records: list[OccurrenceRecord], unknown: bool, quantity: int) -> list[str]:
        f: list[str] = []
        if records and len(records) < quantity:
            f.append(f"O quadro de totais soma {quantity} ocorrências, mas o detalhe só lista {len(records)} linha(s). "
                     "Ao corrigir, confira o registro inteiro (todas as linhas/procedimentos), não só as linhas listadas.")
        for (cnes, name) in dict.fromkeys((r.cnes, r.cnes_name) for r in records):
            refs = list(dict.fromkeys(self._describe(r) for r in records if r.cnes == cnes))
            f.append(f"CNES {cnes} ({name}): {_join(refs, 6)}.")

        procs = list(dict.fromkeys(r.procedure_code for r in records))
        if code == "FET2":
            for proc in procs:
                rule = self.sigtap.find(proc)
                if rule is None:
                    f.append(f"O procedimento {proc} não está na tabela carregada; importe o SIGTAP oficial para eu dizer a faixa de idade.")
                    continue
                f.append(f"{rule.formatted_code} ({rule.name}) só aceita pacientes de {rule.age_range_display}. "
                         "Confira a data de nascimento nessas fichas: digitação errada é a causa mais comum.")
                if rule.code in AGE_ALTERNATIVES:
                    alt_code, when = AGE_ALTERNATIVES[rule.code]
                    alt = self.sigtap.find(alt_code)
                    f.append(f"Se a idade estiver certa: {when}, o código é "
                             f"{f'{alt.formatted_code} ({alt.name})' if alt else alt_code}.")
        elif code == "QTD2":
            for proc in procs:
                rule = self.sigtap.find(proc)
                f.append(f"{rule.formatted_code} ({rule.name}): máximo de {rule.max_quantity} por registro no SIGTAP."
                         if rule and rule.max_quantity > 0 else
                         f"Procedimento {proc}: importe o SIGTAP oficial para eu dizer a quantidade máxima.")
            comps = list(dict.fromkeys(r.competencia for r in records))
            if comps:
                f.append(f"Competência(s) dos registros: {', '.join(comps)}. "
                         "Se forem de meses anteriores, confira se não são sessões já apresentadas antes.")
        elif code == "9Q":
            for proc in procs:
                rule = self.sigtap.find(proc)
                f.append(f"{rule.formatted_code} ({rule.name}) aceita {len(rule.allowed_cids)} CID(s) como principal, "
                         f"por exemplo: {_join(rule.allowed_cids, 8)}." if rule and rule.allowed_cids else
                         f"Procedimento {proc}: importe o SIGTAP oficial para eu listar os CIDs aceitos.")
            f.append("O CID é informação clínica: a troca precisa estar no laudo/prontuário, assinada pelo médico.")
        elif code in ("67", "12"):
            f += self._apac_conflicts(records)
        elif code == "Y8":
            f += [f"APAC {r.reference} é da competência {r.competencia}. APACs do PMAE (OCI) valem só por 2 competências "
                  "(início e fim); fora disso o SIA não aceita." for r in records]
        elif code == "ERRo":
            if any(r.procedure_code.startswith("09") for r in records):
                f.append("São procedimentos do grupo 09 (OCI, Ofertas de Cuidados Integrados): a APAC precisa trazer as consultas "
                         "e exames que compõem a OCI como procedimentos secundários.")
        elif code == "ERRj":
            f.append("O CPF tem dígito verificador errado. Se o paciente tiver CNS válido, informar o CNS também resolve.")
        elif code == "ERRI":
            f.append("Raça/cor precisa ser 01 a 05 (autodeclarada); 05 (indígena) exige também o código da etnia.")

        if unknown:
            f.append("Ainda não tenho este código na base de erros. Procure a descrição no manual do SIA ou no BDNOTAS da versão.")
        return f

    @staticmethod
    def _describe(r: OccurrenceRecord) -> str:
        if r.instrument == "APAC":
            return f"APAC {r.reference} (proc. {r.procedure_code}, comp. {r.competencia})"
        return f"folha/seq {r.reference}" + (f" (prof. {_mask(r.third_column)})" if r.third_column else "")

    def _apac_conflicts(self, records: list[OccurrenceRecord]) -> list[str]:
        out, seen = [], set()
        for r in records:
            if not r.related_apacs or r.related_apacs[0].patient_document in seen:
                continue
            patient = r.related_apacs[0].patient_document
            seen.add(patient)
            apacs = sorted({a.apac_number: a for a in r.related_apacs}.values(), key=lambda a: a.start or date.min)
            parts = []
            for a in apacs:
                exit_ = f", saída em {_fmt(a.exit_date)}" if a.exit_date else ""
                rule = self.sigtap.find(a.procedure_code)
                proc = f"{a.procedure_code} {rule.name}" if rule else a.procedure_code
                parts.append(f"APAC {a.apac_number} (proc. {proc}, {_fmt(a.start)} a {_fmt(a.end)}{exit_}, motivo {a.exit_reason})")
            text = f"Paciente {_mask(patient)}: {' e '.join(parts)}."
            if len(apacs) >= 2:
                a, b = apacs[0], apacs[1]
                end_a = a.exit_date or a.end
                if end_a and b.start and b.start <= end_a:
                    text += f" Elas valem ao mesmo tempo de {_fmt(b.start)} até {_fmt(end_a)}."
                if a.procedure_code != b.procedure_code:
                    text += (" Como o procedimento mudou, a anterior precisa estar encerrada (mudança de procedimento) antes do "
                             "início da nova, e a nova entra como APAC inicial.")
            out.append(text)
        return out
