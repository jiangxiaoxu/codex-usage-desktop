r"""Desktop GUI for corrected local Codex token and API cost analysis.

Run with:
    .\.ven\Scripts\python.exe .\codex_token_usage_gui.py
"""

from __future__ import annotations

import queue
import threading
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import Decimal
from pathlib import Path
from typing import Callable, Iterable, TypeAlias
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from codex_usage_core import (
    SINGAPORE,
    CostBreakdown,
    ScanDiagnostics,
    UsageEvent,
    available_values,
    event_cost,
    export_events_csv,
    filter_events,
    parse_singapore_input,
    scan_usage,
    summarize_events,
)


DEFAULT_CODEX_HOME = Path.home() / ".codex"
TreeRow: TypeAlias = tuple[str, ...]


@dataclass(frozen=True)
class QueryState:
    start: datetime
    end: datetime
    models: frozenset[str]
    agent_roles: frozenset[str]
    thread_types: frozenset[str]
    path_query: str
    ignore_gpt56_long_context: bool


@dataclass(frozen=True)
class ScanProgress:
    current: int
    total: int


@dataclass(frozen=True)
class ScanCompleted:
    events: list[UsageEvent]
    diagnostics: ScanDiagnostics


@dataclass(frozen=True)
class ScanFailure:
    error: Exception


ScanMessage: TypeAlias = ScanProgress | ScanCompleted | ScanFailure


def format_count(value: int) -> str:
    return f"{value:,}"


def format_usd(value: Decimal) -> str:
    return f"${value:,.2f}"


def format_percent(numerator: Decimal, denominator: Decimal) -> str:
    return "-" if denominator == 0 else f"{numerator / denominator:.1%}"


class UsageApp(ttk.Frame):
    def __init__(self, master: tk.Tk) -> None:
        super().__init__(master, padding=12)
        self.master = master
        self.events: list[UsageEvent] = []
        self.filtered_events: list[UsageEvent] = []
        self.diagnostics = ScanDiagnostics()
        self.scan_queue: queue.Queue[ScanMessage] = queue.Queue()
        self.scanning = False

        now = datetime.now(SINGAPORE).replace(second=0, microsecond=0)
        self.preset_var = tk.StringVar(value="最近 72 小时")
        self.start_var = tk.StringVar(value=(now - timedelta(hours=72)).strftime("%Y-%m-%d %H:%M"))
        self.end_var = tk.StringVar(value=now.strftime("%Y-%m-%d %H:%M"))
        self.path_query_var = tk.StringVar()
        self.ignore_gpt56_var = tk.BooleanVar(value=True)
        self.include_main_var = tk.BooleanVar(value=True)
        self.include_subagent_var = tk.BooleanVar(value=True)
        self.status_var = tk.StringVar(value="准备扫描本地 Codex 历史...")
        self.summary_var = tk.StringVar(value="")

        self._build_window()
        self._start_scan()

    def _build_window(self) -> None:
        self.master.title("Codex 本地 Token 与 API 费用统计")
        self.master.minsize(1040, 700)
        self.pack(fill=tk.BOTH, expand=True)

        filters = ttk.LabelFrame(self, text="时间与筛选", padding=10)
        filters.pack(fill=tk.X)
        ttk.Label(filters, text="预设").grid(row=0, column=0, sticky=tk.W)
        preset = ttk.Combobox(
            filters,
            textvariable=self.preset_var,
            values=("最近 24 小时", "最近 48 小时", "最近 72 小时", "自定义"),
            state="readonly",
            width=14,
        )
        preset.grid(row=0, column=1, padx=(6, 14), sticky=tk.W)
        preset.bind("<<ComboboxSelected>>", self._apply_preset)
        ttk.Label(filters, text="开始 SGT").grid(row=0, column=2, sticky=tk.W)
        ttk.Entry(filters, textvariable=self.start_var, width=19).grid(row=0, column=3, padx=(6, 14), sticky=tk.W)
        ttk.Label(filters, text="结束 SGT").grid(row=0, column=4, sticky=tk.W)
        ttk.Entry(filters, textvariable=self.end_var, width=19).grid(row=0, column=5, padx=(6, 14), sticky=tk.W)
        ttk.Button(filters, text="应用筛选", command=self._apply_filters).grid(row=0, column=6, sticky=tk.W)
        ttk.Button(filters, text="重新扫描", command=self._start_scan).grid(row=0, column=7, padx=(8, 0), sticky=tk.W)

        ttk.Label(filters, text="模型").grid(row=1, column=0, pady=(10, 0), sticky=tk.NW)
        self.model_list = tk.Listbox(filters, height=4, selectmode=tk.MULTIPLE, exportselection=False, width=26)
        self.model_list.grid(row=1, column=1, padx=(6, 14), pady=(10, 0), sticky=tk.W)
        ttk.Label(filters, text="子代理角色").grid(row=1, column=2, pady=(10, 0), sticky=tk.NW)
        self.role_list = tk.Listbox(filters, height=4, selectmode=tk.MULTIPLE, exportselection=False, width=22)
        self.role_list.grid(row=1, column=3, padx=(6, 14), pady=(10, 0), sticky=tk.W)
        type_box = ttk.Frame(filters)
        type_box.grid(row=1, column=4, columnspan=2, pady=(10, 0), sticky=tk.W)
        ttk.Checkbutton(type_box, text="主线程", variable=self.include_main_var).pack(side=tk.LEFT)
        ttk.Checkbutton(type_box, text="子代理", variable=self.include_subagent_var).pack(side=tk.LEFT, padx=(10, 0))
        ttk.Checkbutton(filters, text="忽略 5.6 >272K 倍率", variable=self.ignore_gpt56_var).grid(row=1, column=6, pady=(10, 0), sticky=tk.W)

        ttk.Label(filters, text="agent path / nickname").grid(row=2, column=0, pady=(10, 0), sticky=tk.W)
        ttk.Entry(filters, textvariable=self.path_query_var, width=52).grid(row=2, column=1, columnspan=3, padx=(6, 14), pady=(10, 0), sticky=tk.W)
        ttk.Button(filters, text="导出当前明细 CSV", command=self._export_current).grid(row=2, column=4, columnspan=2, pady=(10, 0), sticky=tk.W)
        ttk.Label(filters, text="所有时间范围均为起始含, 结束不含.").grid(row=2, column=6, columnspan=2, padx=(12, 0), pady=(10, 0), sticky=tk.W)

        ttk.Label(self, textvariable=self.summary_var, anchor=tk.W).pack(fill=tk.X, pady=(10, 4))
        notebook = ttk.Notebook(self)
        notebook.pack(fill=tk.BOTH, expand=True)

        model_tab = ttk.Frame(notebook, padding=8)
        role_tab = ttk.Frame(notebook, padding=8)
        detail_tab = ttk.Frame(notebook, padding=8)
        notebook.add(model_tab, text="按模型")
        notebook.add(role_tab, text="按角色")
        notebook.add(detail_tab, text="按子代理 / 线程")

        self.model_tree = self._tree(
            model_tab,
            ("模型", "调用", "无缓存输入", "缓存输入", "输出", "思考输出", "费用", "费用占比"),
            (170, 80, 120, 120, 110, 110, 110, 100),
        )
        self.role_tree = self._tree(
            role_tab,
            ("线程类型", "角色", "调用", "无缓存输入", "缓存输入", "输出", "思考输出", "费用", "费用占比"),
            (100, 150, 80, 115, 115, 105, 105, 110, 100),
        )
        self.detail_tree = self._tree(
            detail_tab,
            ("线程类型", "角色", "agent path", "模型", "调用", "总 tokens", "费用"),
            (90, 130, 350, 150, 80, 130, 120),
        )
        ttk.Label(self, textvariable=self.status_var, anchor=tk.W).pack(fill=tk.X, pady=(6, 0))

    def _tree(self, parent: ttk.Frame, headings: tuple[str, ...], widths: tuple[int, ...]) -> ttk.Treeview:
        wrapper = ttk.Frame(parent)
        wrapper.pack(fill=tk.BOTH, expand=True)
        tree = ttk.Treeview(wrapper, columns=headings, show="headings")
        for heading, width in zip(headings, widths, strict=True):
            tree.heading(heading, text=heading)
            tree.column(heading, width=width, minwidth=70, stretch=True, anchor=tk.W)
        vertical = ttk.Scrollbar(wrapper, orient=tk.VERTICAL, command=tree.yview)
        horizontal = ttk.Scrollbar(wrapper, orient=tk.HORIZONTAL, command=tree.xview)
        tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        tree.grid(row=0, column=0, sticky=tk.NSEW)
        vertical.grid(row=0, column=1, sticky=tk.NS)
        horizontal.grid(row=1, column=0, sticky=tk.EW)
        wrapper.rowconfigure(0, weight=1)
        wrapper.columnconfigure(0, weight=1)
        return tree

    def _apply_preset(self, _event: object | None = None) -> None:
        preset = self.preset_var.get()
        hours = {"最近 24 小时": 24, "最近 48 小时": 48, "最近 72 小时": 72}.get(preset)
        if hours is None:
            return
        end = datetime.now(SINGAPORE).replace(second=0, microsecond=0)
        self.start_var.set((end - timedelta(hours=hours)).strftime("%Y-%m-%d %H:%M"))
        self.end_var.set(end.strftime("%Y-%m-%d %H:%M"))

    def _selected_values(self, widget: tk.Listbox) -> set[str]:
        return {str(widget.get(index)) for index in widget.curselection()}

    def _query_state(self) -> QueryState:
        start = parse_singapore_input(self.start_var.get())
        end = parse_singapore_input(self.end_var.get())
        if start >= end:
            raise ValueError("结束时间必须晚于开始时间.")
        thread_types: set[str] = set()
        if self.include_main_var.get():
            thread_types.add("main")
        if self.include_subagent_var.get():
            thread_types.add("subagent")
        return QueryState(
            start=start,
            end=end,
            models=frozenset(self._selected_values(self.model_list)),
            agent_roles=frozenset(self._selected_values(self.role_list)),
            thread_types=frozenset(thread_types),
            path_query=self.path_query_var.get(),
            ignore_gpt56_long_context=self.ignore_gpt56_var.get(),
        )

    def _start_scan(self) -> None:
        if self.scanning:
            return
        self.scanning = True
        self.status_var.set("正在扫描本地 JSONL 历史...")
        worker = threading.Thread(target=self._scan_worker, daemon=True)
        worker.start()
        self.after(100, self._poll_scan_queue)

    def _scan_worker(self) -> None:
        def progress(current: int, total: int) -> None:
            self.scan_queue.put(ScanProgress(current, total))

        try:
            events, diagnostics = scan_usage(DEFAULT_CODEX_HOME, progress)
        except Exception as error:  # noqa: BLE001
            self.scan_queue.put(ScanFailure(error))
            return
        self.scan_queue.put(ScanCompleted(events, diagnostics))

    def _poll_scan_queue(self) -> None:
        try:
            while True:
                message = self.scan_queue.get_nowait()
                if isinstance(message, ScanProgress):
                    self.status_var.set(f"正在扫描本地 JSONL 历史: {message.current}/{message.total}")
                elif isinstance(message, ScanFailure):
                    self.scanning = False
                    self.status_var.set("扫描失败.")
                    messagebox.showerror("扫描失败", str(message.error))
                elif isinstance(message, ScanCompleted):
                    self.scanning = False
                    self.events = message.events
                    self.diagnostics = message.diagnostics
                    self._populate_filter_lists()
                    self._apply_filters()
        except queue.Empty:
            if self.scanning:
                self.after(100, self._poll_scan_queue)

    def _populate_filter_lists(self) -> None:
        models, roles = available_values(self.events)
        for widget, values in ((self.model_list, models), (self.role_list, roles)):
            widget.delete(0, tk.END)
            for value in values:
                widget.insert(tk.END, value)
            widget.selection_set(0, tk.END)

    def _apply_filters(self) -> None:
        if not self.events:
            return
        try:
            state = self._query_state()
        except ValueError as error:
            messagebox.showerror("筛选条件无效", str(error))
            return
        self.filtered_events = filter_events(
            self.events,
            state.start,
            state.end,
            set(state.models),
            set(state.agent_roles),
            set(state.thread_types),
            state.path_query,
        )
        summary = summarize_events(self.filtered_events, state.ignore_gpt56_long_context)
        self.summary_var.set(
            " | ".join(
                (
                    f"调用 {format_count(int(summary['calls']))}",
                    f"总 tokens {format_count(int(summary['canonical_total_tokens']))}",
                    f"模型 token 费用 {format_usd(Decimal(summary['total_cost']))}",
                    f"未定价调用 {format_count(int(summary['unpriced_calls']))}",
                )
            )
        )
        self._fill_model_tree(state)
        self._fill_role_tree(state)
        self._fill_detail_tree(state)
        self.status_var.set(
            f"已扫描 {self.diagnostics.files_scanned} 个 rollout. 去重跳过 {self.diagnostics.duplicate_snapshots_skipped} 条累计快照, "
            f"跳过 {self.diagnostics.zero_breakdown_snapshots_skipped} 条无拆分快照."
        )

    def _replace_rows(self, tree: ttk.Treeview, rows: Iterable[TreeRow]) -> None:
        tree.delete(*tree.get_children())
        for row in rows:
            tree.insert("", tk.END, values=row)

    def _group_rows(self, state: QueryState, key: Callable[[UsageEvent], str]) -> list[tuple[str, dict[str, Decimal | int]]]:
        grouped: dict[str, list[UsageEvent]] = defaultdict(list)
        for event in self.filtered_events:
            grouped[key(event)].append(event)
        rows = [(name, summarize_events(events, state.ignore_gpt56_long_context)) for name, events in grouped.items()]
        return sorted(rows, key=lambda item: Decimal(item[1]["total_cost"]), reverse=True)

    def _fill_model_tree(self, state: QueryState) -> None:
        total = Decimal(summarize_events(self.filtered_events, state.ignore_gpt56_long_context)["total_cost"])
        rows: list[TreeRow] = []
        for name, summary in self._group_rows(state, lambda event: event.model):
            cost = Decimal(summary["total_cost"])
            rows.append(
                (
                    name,
                    format_count(int(summary["calls"])),
                    format_count(int(summary["uncached_input_tokens"])),
                    format_count(int(summary["cached_input_tokens"])),
                    format_count(int(summary["output_tokens"])),
                    format_count(int(summary["reasoning_output_tokens"])),
                    format_usd(cost),
                    format_percent(cost, total),
                )
            )
        self._replace_rows(self.model_tree, rows)

    def _fill_role_tree(self, state: QueryState) -> None:
        total = Decimal(summarize_events(self.filtered_events, state.ignore_gpt56_long_context)["total_cost"])
        grouped: dict[tuple[str, str], list[UsageEvent]] = defaultdict(list)
        for event in self.filtered_events:
            grouped[(event.thread_type, event.agent_role)].append(event)
        rows: list[TreeRow] = []
        for (thread_type, role), events in sorted(
            grouped.items(), key=lambda item: Decimal(summarize_events(item[1], state.ignore_gpt56_long_context)["total_cost"]), reverse=True
        ):
            summary = summarize_events(events, state.ignore_gpt56_long_context)
            cost = Decimal(summary["total_cost"])
            rows.append(
                (
                    "主线程" if thread_type == "main" else "子代理" if thread_type == "subagent" else "未知",
                    "主线程" if role == "main" else role,
                    format_count(int(summary["calls"])),
                    format_count(int(summary["uncached_input_tokens"])),
                    format_count(int(summary["cached_input_tokens"])),
                    format_count(int(summary["output_tokens"])),
                    format_count(int(summary["reasoning_output_tokens"])),
                    format_usd(cost),
                    format_percent(cost, total),
                )
            )
        self._replace_rows(self.role_tree, rows)

    def _fill_detail_tree(self, state: QueryState) -> None:
        grouped: dict[tuple[str, str, str, str], list[UsageEvent]] = defaultdict(list)
        for event in self.filtered_events:
            grouped[(event.thread_type, event.agent_role, event.agent_path, event.model)].append(event)
        rows: list[TreeRow] = []
        for (thread_type, role, path, model), events in sorted(
            grouped.items(), key=lambda item: Decimal(summarize_events(item[1], state.ignore_gpt56_long_context)["total_cost"]), reverse=True
        ):
            summary = summarize_events(events, state.ignore_gpt56_long_context)
            rows.append(
                (
                    "主线程" if thread_type == "main" else "子代理" if thread_type == "subagent" else "未知",
                    "主线程" if role == "main" else role,
                    path,
                    model,
                    format_count(int(summary["calls"])),
                    format_count(int(summary["canonical_total_tokens"])),
                    format_usd(Decimal(summary["total_cost"])),
                )
            )
        self._replace_rows(self.detail_tree, rows)

    def _export_current(self) -> None:
        if not self.filtered_events:
            messagebox.showinfo("无可导出数据", "请先扫描并应用至少一个有效筛选条件.")
            return
        try:
            state = self._query_state()
        except ValueError as error:
            messagebox.showerror("筛选条件无效", str(error))
            return
        filename = filedialog.asksaveasfilename(
            title="导出当前筛选结果",
            defaultextension=".csv",
            filetypes=(("CSV 文件", "*.csv"),),
            initialfile="codex-usage-export.csv",
        )
        if not filename:
            return
        try:
            export_events_csv(Path(filename), self.filtered_events, state.ignore_gpt56_long_context)
        except OSError as error:
            messagebox.showerror("导出失败", str(error))
            return
        messagebox.showinfo("导出完成", f"已导出 {len(self.filtered_events):,} 条已筛选事件.")


def main() -> None:
    root = tk.Tk()
    UsageApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
