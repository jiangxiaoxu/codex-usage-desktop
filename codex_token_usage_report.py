#!/usr/bin/env python3
r"""Aggregate local Codex rollout token usage into Markdown and CSV reports.

Example:
    .\.ven\Scripts\python.exe .\codex_token_usage_report.py \
        --start 2026-07-10T18:00:00+08:00 \
        --end 2026-07-14T18:00:00+08:00
"""

from __future__ import annotations

import argparse
import csv
import json
from collections import defaultdict
from dataclasses import dataclass, fields
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


DEFAULT_CODEX_HOME = Path.home() / ".codex"
DEFAULT_OUTPUT_DIR = Path.cwd() / "outputs"
METRICS = (
    "input_tokens",
    "cached_input_tokens",
    "uncached_input_tokens",
    "output_tokens",
    "reasoning_output_tokens",
    "codex_total_tokens",
)


@dataclass(frozen=True)
class UsageEvent:
    time_sgt: str
    thread_id: str
    thread_name: str
    role: str
    agent_path: str
    parent_thread_id: str
    model: str
    input_tokens: int
    cached_input_tokens: int
    uncached_input_tokens: int
    output_tokens: int
    reasoning_output_tokens: int
    codex_total_tokens: int
    source_file: str


def parse_timestamp(value: str) -> datetime:
    """Parse ISO-8601 while preserving the original UTC offset."""
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def number(value: int) -> str:
    return f"{value:,}"


def percent(numerator: int, denominator: int) -> str:
    return "0.00%" if denominator == 0 else f"{numerator / denominator:.2%}"


def load_thread_names(codex_home: Path) -> dict[str, str]:
    names: dict[str, str] = {}
    index_path = codex_home / "session_index.jsonl"
    if not index_path.exists():
        return names

    for line in index_path.read_text(encoding="utf-8").splitlines():
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            continue
        if item.get("id") and item.get("thread_name"):
            names[item["id"]] = item["thread_name"]
    return names


def rollout_paths(codex_home: Path) -> Iterable[Path]:
    sessions = codex_home / "sessions"
    archived = codex_home / "archived_sessions"
    paths = list(sessions.glob("**/rollout-*.jsonl")) if sessions.exists() else []
    paths += list(archived.glob("rollout-*.jsonl")) if archived.exists() else []
    return sorted(set(paths))


def load_json_lines(path: Path) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    with path.open(encoding="utf-8") as source:
        for line in source:
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return events


def collect_usage(
    path: Path,
    start: datetime,
    end: datetime,
    thread_names: dict[str, str],
) -> list[UsageEvent]:
    events = load_json_lines(path)
    meta: dict[str, Any] = next(
        (event.get("payload", {}) for event in events if event.get("type") == "session_meta"), {}
    )
    turn_models = {
        event.get("payload", {}).get("turn_id"): event.get("payload", {}).get("model", "unknown")
        for event in events
        if event.get("type") == "turn_context" and event.get("payload", {}).get("turn_id")
    }
    fallback_model = next((model for model in turn_models.values() if model), "unknown")
    current_model = "unknown"
    current_turn: str | None = None
    result: list[UsageEvent] = []

    for event in events:
        event_type = event.get("type")
        payload = event.get("payload", {})
        if event_type == "turn_context":
            current_turn = payload.get("turn_id")
            current_model = turn_models.get(current_turn, fallback_model)
            continue
        if event_type == "event_msg" and payload.get("type") == "task_started":
            current_turn = payload.get("turn_id")
            current_model = turn_models.get(current_turn, current_model)
            continue
        if event_type != "event_msg" or payload.get("type") != "token_count":
            continue

        timestamp = event.get("timestamp")
        info = payload.get("info")
        usage = info.get("last_token_usage") if isinstance(info, dict) else None
        if not timestamp or not isinstance(usage, dict):
            continue
        when = parse_timestamp(timestamp)
        if not start <= when < end:
            continue
        if current_turn in turn_models:
            current_model = turn_models[current_turn]
        elif current_model == "unknown":
            current_model = fallback_model

        input_tokens = int(usage.get("input_tokens", 0))
        cached_input_tokens = int(usage.get("cached_input_tokens", 0))
        thread_id = str(meta.get("session_id") or meta.get("id") or path.stem)
        role = "子代理" if meta.get("thread_source") == "subagent" else "主线程"
        result.append(
            UsageEvent(
                time_sgt=when.astimezone(start.tzinfo).strftime("%Y-%m-%d %H:%M:%S %z"),
                thread_id=thread_id,
                thread_name=thread_names.get(thread_id, ""),
                role=role,
                agent_path=str(meta.get("agent_path") or "/root"),
                parent_thread_id=str(meta.get("parent_thread_id") or ""),
                model=current_model,
                input_tokens=input_tokens,
                cached_input_tokens=cached_input_tokens,
                uncached_input_tokens=input_tokens - cached_input_tokens,
                output_tokens=int(usage.get("output_tokens", 0)),
                reasoning_output_tokens=int(usage.get("reasoning_output_tokens", 0)),
                codex_total_tokens=int(usage.get("total_tokens", 0)),
                source_file=str(path),
            )
        )
    return result


def sum_usage(items: Iterable[UsageEvent]) -> dict[str, int]:
    rows = list(items)
    return {"calls": len(rows), **{metric: sum(getattr(row, metric) for row in rows) for metric in METRICS}}


def group_usage(items: list[UsageEvent], *keys: str) -> list[tuple[tuple[str, ...], dict[str, int], list[UsageEvent]]]:
    grouped: dict[tuple[str, ...], list[UsageEvent]] = defaultdict(list)
    for item in items:
        grouped[tuple(str(getattr(item, key)) for key in keys)].append(item)
    return sorted(
        ((key, sum_usage(group), group) for key, group in grouped.items()),
        key=lambda item: item[1]["codex_total_tokens"],
        reverse=True,
    )


def write_csv(path: Path, rows: Iterable[UsageEvent]) -> None:
    items = list(rows)
    with path.open("w", newline="", encoding="utf-8-sig") as destination:
        writer = csv.DictWriter(destination, fieldnames=[field.name for field in fields(UsageEvent)])
        writer.writeheader()
        writer.writerows(item.__dict__ for item in items)


def write_report(path: Path, rows: list[UsageEvent], start: datetime, end: datetime) -> None:
    total = sum_usage(rows)
    model_groups = group_usage(rows, "model")
    role_groups = group_usage(rows, "role")
    model_role_groups = group_usage(rows, "model", "role")
    thread_groups = group_usage(rows, "thread_id", "role", "agent_path", "model")
    thread_count = len({row.thread_id for row in rows})
    lines = [
        "# Codex Token Usage Report",
        "",
        f"统计窗口: {start.isoformat()} 至 {end.isoformat()} (Asia/Singapore, 起始含、结束不含)",
        "",
        f"纳入 {len(rows)} 次模型调用, {len(thread_groups)} 个线程-模型组合, {thread_count} 个线程.",
        "",
        "## 口径",
        "",
        "- 数据来自每个 rollout 的 `event_msg.token_count.info.last_token_usage`; 仅累加时间窗口内的增量调用。",
        "- `input_tokens` 已包含 `cached_input_tokens`; `uncached_input_tokens = input_tokens - cached_input_tokens`, 因而缓存输入不能再与输入相加。",
        "- `codex_total_tokens` 是 Codex 原始 `total_tokens = input_tokens + output_tokens`, 不包含 `reasoning_output_tokens`; 推理 token 单列展示。",
        "- `主线程` 为用户直接发起的线程; `子代理` 为 `thread_source=subagent`。",
        "",
        "## 全部模型汇总",
        "",
        "| 模型 | 调用数 | 输入 | 缓存输入 | 非缓存输入 | 输出 | 推理输出 | Codex 总 tokens |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for (model,), values, _ in model_groups:
        lines.append(
            f"| {model} | {number(values['calls'])} | {number(values['input_tokens'])} | "
            f"{number(values['cached_input_tokens'])} | {number(values['uncached_input_tokens'])} | "
            f"{number(values['output_tokens'])} | {number(values['reasoning_output_tokens'])} | "
            f"{number(values['codex_total_tokens'])} |"
        )
    lines.append(
        f"| **合计** | **{number(total['calls'])}** | **{number(total['input_tokens'])}** | "
        f"**{number(total['cached_input_tokens'])}** | **{number(total['uncached_input_tokens'])}** | "
        f"**{number(total['output_tokens'])}** | **{number(total['reasoning_output_tokens'])}** | "
        f"**{number(total['codex_total_tokens'])}** |"
    )
    lines += [
        "",
        "## 主线程与子代理占比",
        "",
        "| 类型 | 调用数 | 输入 (占比) | 缓存输入 (占比) | 非缓存输入 (占比) | 输出 (占比) | 推理输出 (占比) | Codex 总 tokens (占比) |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for (role,), values, _ in sorted(role_groups):
        lines.append(
            f"| {role} | {number(values['calls'])} | {number(values['input_tokens'])} ({percent(values['input_tokens'], total['input_tokens'])}) | "
            f"{number(values['cached_input_tokens'])} ({percent(values['cached_input_tokens'], total['cached_input_tokens'])}) | "
            f"{number(values['uncached_input_tokens'])} ({percent(values['uncached_input_tokens'], total['uncached_input_tokens'])}) | "
            f"{number(values['output_tokens'])} ({percent(values['output_tokens'], total['output_tokens'])}) | "
            f"{number(values['reasoning_output_tokens'])} ({percent(values['reasoning_output_tokens'], total['reasoning_output_tokens'])}) | "
            f"{number(values['codex_total_tokens'])} ({percent(values['codex_total_tokens'], total['codex_total_tokens'])}) |"
        )
    lines += [
        "",
        "## 模型 × 线程类型",
        "",
        "| 模型 | 类型 | 调用数 | 输入 | 缓存输入 | 输出 | 推理输出 | Codex 总 tokens |",
        "|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    for (model, role), values, _ in sorted(model_role_groups):
        lines.append(
            f"| {model} | {role} | {number(values['calls'])} | {number(values['input_tokens'])} | "
            f"{number(values['cached_input_tokens'])} | {number(values['output_tokens'])} | "
            f"{number(values['reasoning_output_tokens'])} | {number(values['codex_total_tokens'])} |"
        )
    lines += [
        "",
        "## 各聊天线程 × 模型明细",
        "",
        "| 线程 ID | 类型 / agent path | 模型 | 调用数 | 输入 | 缓存输入 | 非缓存输入 | 输出 | 推理输出 | Codex 总 tokens (占比) |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for (thread_id, role, agent_path, model), values, group in thread_groups:
        thread_name = group[0].thread_name.replace("|", "/")
        name = f" ({thread_name})" if thread_name else ""
        lines.append(
            f"| {thread_id}{name} | {role} / {agent_path} | {model} | {number(values['calls'])} | "
            f"{number(values['input_tokens'])} | {number(values['cached_input_tokens'])} | "
            f"{number(values['uncached_input_tokens'])} | {number(values['output_tokens'])} | "
            f"{number(values['reasoning_output_tokens'])} | {number(values['codex_total_tokens'])} "
            f"({percent(values['codex_total_tokens'], total['codex_total_tokens'])}) |"
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--start", required=True, help="ISO-8601 start time, inclusive")
    parser.add_argument("--end", required=True, help="ISO-8601 end time, exclusive")
    parser.add_argument("--codex-home", type=Path, default=DEFAULT_CODEX_HOME)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    args = parser.parse_args()
    start, end = parse_timestamp(args.start), parse_timestamp(args.end)
    if start.tzinfo is None or end.tzinfo is None or start >= end:
        raise SystemExit("--start and --end must be timezone-aware ISO-8601 timestamps, with start < end")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    thread_names = load_thread_names(args.codex_home)
    rows = [
        event
        for rollout in rollout_paths(args.codex_home)
        for event in collect_usage(rollout, start, end, thread_names)
    ]
    label = f"{start.strftime('%Y-%m-%d-%H%M')}-to-{end.strftime('%Y-%m-%d-%H%M')}"
    report_path = args.output_dir / f"codex-token-usage-report-{label}.md"
    thread_csv_path = args.output_dir / f"codex-token-usage-events-{label}.csv"
    write_report(report_path, rows, start, end)
    write_csv(thread_csv_path, rows)
    total = sum_usage(rows)
    print(f"Report: {report_path}")
    print(f"Raw events CSV: {thread_csv_path}")
    print(f"Calls: {total['calls']:,}; Codex total tokens: {total['codex_total_tokens']:,}")


if __name__ == "__main__":
    main()
