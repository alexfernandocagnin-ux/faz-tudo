"""Base de erros do SIA/SUS — lida de shared/sia-knowledge.json (o mesmo arquivo do desktop)."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from functools import lru_cache

from . import SHARED_DIR
from .text import normalize, terms

SEVERITY_DISPLAY = {
    "CriticalGlosa": "Glosa total / rejeição",
    "PartialGlosa": "Retenção / glosa parcial",
    "SystemBlocker": "Travamento técnico / envio",
    "Warning": "Alerta preventivo",
}


@dataclass(frozen=True)
class GuidedStep:
    step_number: int
    target_system: str
    action_title: str
    detailed_instruction: str
    verification_tip: str = ""


@dataclass(frozen=True)
class KnowledgeEntry:
    id: str
    error_code: str
    title: str
    layer: str
    severity: str
    area: str
    affected_systems: str
    symptom: str
    root_cause: str
    financial_impact_note: str
    search_keywords: tuple[str, ...]
    guided_steps: tuple[GuidedStep, ...]
    secondary_area: str | None = None

    @property
    def severity_display(self) -> str:
        return SEVERITY_DISPLAY.get(self.severity, "Alerta")


@dataclass(frozen=True)
class FixAreaInfo:
    label: str
    who: str
    action: str


@dataclass(frozen=True)
class KnowledgeBase:
    entries: tuple[KnowledgeEntry, ...]
    areas: dict[str, FixAreaInfo] = field(default_factory=dict)
    sia_codes: dict[str, str] = field(default_factory=dict)

    def by_id(self, entry_id: str | None) -> KnowledgeEntry | None:
        return next((e for e in self.entries if e.id == entry_id), None)

    @property
    def layers(self) -> list[str]:
        seen: list[str] = []
        for e in self.entries:
            if e.layer not in seen:
                seen.append(e.layer)
        return seen

    def search(self, query: str | None, layer: str | None = None) -> list[KnowledgeEntry]:
        wanted = terms(query)
        result = []
        for e in self.entries:
            if layer and e.layer != layer:
                continue
            haystack = normalize(" ".join([e.error_code, e.title, e.layer, e.symptom, e.root_cause, e.affected_systems,
                                           *e.search_keywords]))
            if all(t in haystack for t in wanted):
                result.append(e)
        return result


@lru_cache(maxsize=1)
def load_knowledge() -> KnowledgeBase:
    raw = json.loads((SHARED_DIR / "sia-knowledge.json").read_text(encoding="utf-8"))
    entries = tuple(
        KnowledgeEntry(
            id=e["id"],
            error_code=e["errorCode"],
            title=e["title"],
            layer=e["layer"],
            severity=e["severity"],
            area=e.get("area", "PRODUCAO"),
            secondary_area=e.get("secondaryArea"),
            affected_systems=e["affectedSystems"],
            symptom=e["symptom"],
            root_cause=e["rootCause"],
            financial_impact_note=e["financialImpactNote"],
            search_keywords=tuple(e.get("searchKeywords", [])),
            guided_steps=tuple(
                GuidedStep(s["stepNumber"], s["targetSystem"], s["actionTitle"], s["detailedInstruction"],
                           s.get("verificationTip", ""))
                for s in e["guidedSteps"]
            ),
        )
        for e in raw["entries"]
    )
    areas = {k: FixAreaInfo(v["label"], v["who"], v["action"]) for k, v in raw["areas"].items()}
    return KnowledgeBase(entries=entries, areas=areas, sia_codes=dict(raw["siaCodes"]))
