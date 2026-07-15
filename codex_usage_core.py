"""Corrected local Codex usage parser and API token-cost estimator.

The module deliberately uses only the Python standard library so it can run
inside the project's local virtual environment without additional packages.
"""

from __future__ import annotations

import csv
import json
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


try:
    SINGAPORE = ZoneInfo("Asia/Singapore")
except ZoneInfoNotFoundError:
    # Singapore has no daylight-saving transitions, so this stdlib fallback is exact.
    SINGAPORE = timezone(timedelta(hours=8), name="Asia/Singapore")
MILLION = Decimal("1000000")
LONG_CONTEXT_LIMIT = 272_000


@dataclass(frozen=True)
class ModelRate:
    input_per_million: Decimal
    cached_input_per_million: Decimal
    output_per_million: Decimal


STANDARD_RATES: dict[str, ModelRate] = {
    "gpt-5.6-sol": ModelRate(Decimal("5"), Decimal("0.5"), Decimal("30")),
    "gpt-5.6-terra": ModelRate(Decimal("2.5"), Decimal("0.25"), Decimal("15")),
    "gpt-5.6-luna": ModelRate(Decimal("1"), Decimal("0.1"), Decimal("6")),
    "gpt-5.5": ModelRate(Decimal("5"), Decimal("0.5"), Decimal("30")),
    "gpt-5.4": ModelRate(Decimal("2.5"), Decimal("0.25"), Decimal("15")),
    "gpt-5.4-mini": ModelRate(Decimal("0.75"), Decimal("0.075"), Decimal("4.5")),
    "gpt-5.4-nano": ModelRate(Decimal("0.2"), Decimal("0.02"), Decimal("1.25")),
}


@dataclass(frozen=True)
class UsageEvent:
    timestamp_utc: datetime
    sequence: int
    conversation_id: str
    rollout_id: str
    parent_thread_id: str
    thread_type: str
    agent_role: str
    agent_path: str
    agent_nickname: str
    model: str
    input_tokens: int
    cached_input_tokens: int
    output_tokens: int
    reasoning_output_tokens: int

    @property
    def uncached_input_tokens(self) -> int:
        return self.input_tokens - self.cached_input_tokens

    @property
    def other_output_tokens(self) -> int:
        return self.output_tokens - self.reasoning_output_tokens

    @property
    def canonical_total_tokens(self) -> int:
        return self.input_tokens + self.output_tokens


@dataclass
class ScanDiagnostics:
    files_scanned: int = 0
    malformed_lines: int = 0
    duplicate_snapshots_skipped: int = 0
    zero_breakdown_snapshots_skipped: int = 0
    invalid_token_relationships_skipped: int = 0
    unpriced_events: int = 0


@dataclass(frozen=True)
class CostBreakdown:
    uncached_input: Decimal
    cached_input: Decimal
    reasoning_output: Decimal
    other_output: Decimal
    total: Decimal
    priced: bool


def parse_timestamp(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("timestamp must include a UTC offset")
    return parsed


def parse_singapore_input(value: str) -> datetime:
    """Parse GUI input in Singapore local time using YYYY-MM-DD HH:MM."""
    parsed = datetime.strptime(value.strip(), "%Y-%m-%d %H:%M")
    return parsed.replace(tzinfo=SINGAPORE)


def rollout_paths(codex_home: Path) -> list[Path]:
    session_root = codex_home / "sessions"
    archive_root = codex_home / "archived_sessions"
    paths: list[Path] = []
    if session_root.exists():
        paths.extend(session_root.glob("**/rollout-*.jsonl"))
    if archive_root.exists():
        paths.extend(archive_root.glob("rollout-*.jsonl"))
    return sorted(paths)


def _read_json_lines(path: Path, diagnostics: ScanDiagnostics) -> Iterator[dict[str, Any]]:
    with path.open(encoding="utf-8") as source:
        for line in source:
            try:
                payload = json.loads(line)
            except json.JSONDecodeError:
                diagnostics.malformed_lines += 1
                continue
            if isinstance(payload, dict):
                yield payload


def _first_pass(path: Path, diagnostics: ScanDiagnostics) -> tuple[dict[str, Any], dict[str, str], str]:
    meta: dict[str, Any] = {}
    turn_models: dict[str, str] = {}
    fallback_model = "unknown"
    for event in _read_json_lines(path, diagnostics):
        if event.get("type") == "session_meta" and not meta:
            candidate = event.get("payload")
            if isinstance(candidate, dict):
                meta = candidate
        if event.get("type") != "turn_context":
            continue
        payload = event.get("payload")
        if not isinstance(payload, dict):
            continue
        turn_id = payload.get("turn_id")
        model = payload.get("model")
        if isinstance(turn_id, str) and isinstance(model, str) and model:
            turn_models[turn_id] = model
            if fallback_model == "unknown":
                fallback_model = model
    return meta, turn_models, fallback_model


def _token_snapshot(payload: dict[str, Any]) -> tuple[int, int, int, int, int] | None:
    info = payload.get("info")
    if not isinstance(info, dict):
        return None
    total = info.get("total_token_usage")
    if not isinstance(total, dict):
        return None
    return (
        int(total.get("input_tokens", 0)),
        int(total.get("cached_input_tokens", 0)),
        int(total.get("output_tokens", 0)),
        int(total.get("reasoning_output_tokens", 0)),
        int(total.get("total_tokens", 0)),
    )


def _last_usage(payload: dict[str, Any]) -> tuple[int, int, int, int] | None:
    info = payload.get("info")
    if not isinstance(info, dict):
        return None
    usage = info.get("last_token_usage")
    if not isinstance(usage, dict):
        return None
    return (
        int(usage.get("input_tokens", 0)),
        int(usage.get("cached_input_tokens", 0)),
        int(usage.get("output_tokens", 0)),
        int(usage.get("reasoning_output_tokens", 0)),
    )


def _metadata(meta: dict[str, Any], fallback_rollout_id: str) -> tuple[str, str, str, str, str, str, str]:
    source = meta.get("thread_source")
    if source == "subagent":
        thread_type = "subagent"
        agent_role = str(meta.get("agent_role") or "unknown")
    elif source in ("user", None, ""):
        thread_type = "main"
        agent_role = "main"
    else:
        thread_type = "unknown"
        agent_role = "unknown"
    return (
        str(meta.get("session_id") or "unknown"),
        str(meta.get("id") or fallback_rollout_id),
        str(meta.get("parent_thread_id") or ""),
        thread_type,
        agent_role,
        str(meta.get("agent_path") or "/root"),
        str(meta.get("agent_nickname") or ""),
    )


def scan_usage(
    codex_home: Path,
    progress: Callable[[int, int], None] | None = None,
) -> tuple[list[UsageEvent], ScanDiagnostics]:
    """Read all rollout files with per-rollout cumulative-snapshot deduplication."""
    paths = rollout_paths(codex_home)
    diagnostics = ScanDiagnostics()
    result: list[UsageEvent] = []
    sequence = 0
    for file_index, path in enumerate(paths, start=1):
        meta, turn_models, fallback_model = _first_pass(path, diagnostics)
        (
            conversation_id,
            rollout_id,
            parent_thread_id,
            thread_type,
            agent_role,
            agent_path,
            agent_nickname,
        ) = _metadata(meta, path.stem)
        current_turn: str | None = None
        current_model = fallback_model
        prior_snapshot: tuple[int, int, int, int, int] | None = None

        for event in _read_json_lines(path, diagnostics):
            event_type = event.get("type")
            payload = event.get("payload")
            if not isinstance(payload, dict):
                continue
            if event_type == "turn_context":
                current_turn = payload.get("turn_id") if isinstance(payload.get("turn_id"), str) else None
                current_model = turn_models.get(current_turn or "", fallback_model)
                continue
            if event_type == "event_msg" and payload.get("type") == "task_started":
                current_turn = payload.get("turn_id") if isinstance(payload.get("turn_id"), str) else None
                current_model = turn_models.get(current_turn or "", current_model)
                continue
            if event_type != "event_msg" or payload.get("type") != "token_count":
                continue

            snapshot = _token_snapshot(payload)
            if snapshot is not None and snapshot == prior_snapshot:
                diagnostics.duplicate_snapshots_skipped += 1
                continue
            if snapshot is not None:
                prior_snapshot = snapshot

            usage = _last_usage(payload)
            if usage is None:
                continue
            input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens = usage
            if input_tokens == output_tokens == reasoning_output_tokens == 0:
                diagnostics.zero_breakdown_snapshots_skipped += 1
                continue
            if cached_input_tokens > input_tokens or reasoning_output_tokens > output_tokens:
                diagnostics.invalid_token_relationships_skipped += 1
                continue
            timestamp = event.get("timestamp")
            if not isinstance(timestamp, str):
                diagnostics.malformed_lines += 1
                continue
            try:
                timestamp_utc = parse_timestamp(timestamp)
            except ValueError:
                diagnostics.malformed_lines += 1
                continue
            model = turn_models.get(current_turn or "", current_model or fallback_model)
            sequence += 1
            result.append(
                UsageEvent(
                    timestamp_utc=timestamp_utc,
                    sequence=sequence,
                    conversation_id=conversation_id,
                    rollout_id=rollout_id,
                    parent_thread_id=parent_thread_id,
                    thread_type=thread_type,
                    agent_role=agent_role,
                    agent_path=agent_path,
                    agent_nickname=agent_nickname,
                    model=model or "unknown",
                    input_tokens=input_tokens,
                    cached_input_tokens=cached_input_tokens,
                    output_tokens=output_tokens,
                    reasoning_output_tokens=reasoning_output_tokens,
                )
            )
        diagnostics.files_scanned += 1
        if progress is not None:
            progress(file_index, len(paths))
    return result, diagnostics


def event_cost(event: UsageEvent, ignore_gpt56_long_context: bool) -> CostBreakdown:
    rate = STANDARD_RATES.get(event.model)
    if rate is None:
        zero = Decimal("0")
        return CostBreakdown(zero, zero, zero, zero, zero, False)
    long_context = event.input_tokens > LONG_CONTEXT_LIMIT
    apply_long_multiplier = long_context and not (
        ignore_gpt56_long_context and event.model.startswith("gpt-5.6-")
    )
    input_multiplier = Decimal("2") if apply_long_multiplier else Decimal("1")
    output_multiplier = Decimal("1.5") if apply_long_multiplier else Decimal("1")
    uncached = Decimal(event.uncached_input_tokens) * rate.input_per_million * input_multiplier / MILLION
    cached = Decimal(event.cached_input_tokens) * rate.cached_input_per_million * input_multiplier / MILLION
    reasoning = Decimal(event.reasoning_output_tokens) * rate.output_per_million * output_multiplier / MILLION
    other = Decimal(event.other_output_tokens) * rate.output_per_million * output_multiplier / MILLION
    return CostBreakdown(uncached, cached, reasoning, other, uncached + cached + reasoning + other, True)


def filter_events(
    events: Iterable[UsageEvent],
    start: datetime,
    end: datetime,
    models: set[str],
    agent_roles: set[str],
    thread_types: set[str],
    path_query: str,
) -> list[UsageEvent]:
    query = path_query.casefold().strip()
    selected: list[UsageEvent] = []
    for event in events:
        if not start <= event.timestamp_utc < end:
            continue
        if models and event.model not in models:
            continue
        if agent_roles and event.agent_role not in agent_roles:
            continue
        if thread_types and event.thread_type not in thread_types:
            continue
        haystack = " ".join((event.agent_path, event.agent_nickname, event.rollout_id, event.conversation_id)).casefold()
        if query and query not in haystack:
            continue
        selected.append(event)
    return selected


def summarize_events(events: Iterable[UsageEvent], ignore_gpt56_long_context: bool) -> dict[str, Decimal | int]:
    summary: dict[str, Decimal | int] = {
        "calls": 0,
        "input_tokens": 0,
        "cached_input_tokens": 0,
        "uncached_input_tokens": 0,
        "output_tokens": 0,
        "reasoning_output_tokens": 0,
        "other_output_tokens": 0,
        "canonical_total_tokens": 0,
        "uncached_cost": Decimal("0"),
        "cached_cost": Decimal("0"),
        "reasoning_cost": Decimal("0"),
        "other_output_cost": Decimal("0"),
        "total_cost": Decimal("0"),
        "unpriced_calls": 0,
    }
    for event in events:
        summary["calls"] = int(summary["calls"]) + 1
        for field in (
            "input_tokens",
            "cached_input_tokens",
            "uncached_input_tokens",
            "output_tokens",
            "reasoning_output_tokens",
            "other_output_tokens",
            "canonical_total_tokens",
        ):
            summary[field] = int(summary[field]) + int(getattr(event, field))
        cost = event_cost(event, ignore_gpt56_long_context)
        if not cost.priced:
            summary["unpriced_calls"] = int(summary["unpriced_calls"]) + 1
            continue
        summary["uncached_cost"] = Decimal(summary["uncached_cost"]) + cost.uncached_input
        summary["cached_cost"] = Decimal(summary["cached_cost"]) + cost.cached_input
        summary["reasoning_cost"] = Decimal(summary["reasoning_cost"]) + cost.reasoning_output
        summary["other_output_cost"] = Decimal(summary["other_output_cost"]) + cost.other_output
        summary["total_cost"] = Decimal(summary["total_cost"]) + cost.total
    return summary


def group_events(events: Iterable[UsageEvent], key: Callable[[UsageEvent], str]) -> dict[str, list[UsageEvent]]:
    grouped: dict[str, list[UsageEvent]] = defaultdict(list)
    for event in events:
        grouped[key(event)].append(event)
    return dict(grouped)


def export_events_csv(path: Path, events: Iterable[UsageEvent], ignore_gpt56_long_context: bool) -> None:
    fieldnames = [
        "timestamp_sgt",
        "conversation_id",
        "rollout_id",
        "parent_thread_id",
        "thread_type",
        "agent_role",
        "agent_path",
        "agent_nickname",
        "model",
        "input_tokens",
        "cached_input_tokens",
        "uncached_input_tokens",
        "output_tokens",
        "reasoning_output_tokens",
        "other_output_tokens",
        "canonical_total_tokens",
        "uncached_cost_usd",
        "cached_cost_usd",
        "reasoning_cost_usd",
        "other_output_cost_usd",
        "total_cost_usd",
        "pricing_status",
    ]
    with path.open("w", newline="", encoding="utf-8-sig") as destination:
        writer = csv.DictWriter(destination, fieldnames=fieldnames)
        writer.writeheader()
        for event in sorted(events, key=lambda item: (item.timestamp_utc, item.rollout_id, item.sequence)):
            cost = event_cost(event, ignore_gpt56_long_context)
            row = {
                "timestamp_sgt": event.timestamp_utc.astimezone(SINGAPORE).isoformat(),
                "conversation_id": event.conversation_id,
                "rollout_id": event.rollout_id,
                "parent_thread_id": event.parent_thread_id,
                "thread_type": event.thread_type,
                "agent_role": event.agent_role,
                "agent_path": event.agent_path,
                "agent_nickname": event.agent_nickname,
                "model": event.model,
                "input_tokens": event.input_tokens,
                "cached_input_tokens": event.cached_input_tokens,
                "uncached_input_tokens": event.uncached_input_tokens,
                "output_tokens": event.output_tokens,
                "reasoning_output_tokens": event.reasoning_output_tokens,
                "other_output_tokens": event.other_output_tokens,
                "canonical_total_tokens": event.canonical_total_tokens,
                "uncached_cost_usd": str(cost.uncached_input),
                "cached_cost_usd": str(cost.cached_input),
                "reasoning_cost_usd": str(cost.reasoning_output),
                "other_output_cost_usd": str(cost.other_output),
                "total_cost_usd": str(cost.total),
                "pricing_status": "priced" if cost.priced else "unpriced_model",
            }
            writer.writerow({name: _csv_safe(value) for name, value in row.items()})


def _csv_safe(value: object) -> object:
    if isinstance(value, str) and value[:1] in {"=", "+", "-", "@", "\t", "\r"}:
        return "'" + value
    return value


def available_values(events: Iterable[UsageEvent]) -> tuple[list[str], list[str]]:
    models = sorted({event.model for event in events})
    roles = sorted({event.agent_role for event in events}, key=lambda value: (value != "main", value))
    return models, roles
