#!/usr/bin/env python3
"""Build the private owner lifetime prompt archive SQLite database.

Reads local prompt archives (staged document/spreadsheet exports under saves/prompt-archive-sources,
IdeogramHistoryExtractor exports, ChatGPT image prompt JSONL snapshot, local MultiImageClient
UiHistory / generation-history / grok_ledger / UiGoalLoops, and owner-only production snapshots)
and writes a deduplicated, FTS5-indexed SQLite database atomically.

Never commit the output database or staged source files to git.
"""

from __future__ import annotations

import argparse
import csv
import datetime
import glob
import hashlib
import json
import os
import re
import sqlite3
import sys
import tempfile
from collections import defaultdict
from typing import Any

SCHEMA_VERSION = "1"
IMPORTER_VERSION = "2026-11-20"

DEFAULT_DESCRIBE_INSTRUCTION = (
    "Describe this image in detail. Cover the main subjects, setting, composition, "
    "style, colors, lighting, and any visible text."
)

SOURCE_DEFS: dict[str, tuple[str, str, str]] = {
    "gdocs-midjourney-all": (
        "Midjourney History (2022–2023)",
        "midjourney",
        "Exported Midjourney prompt spreadsheet and TSV archive",
    ),
    "gdocs-early-2022": (
        "Early AI Image Prompts (2022)",
        "early-doc",
        "Early 2022 plain-text prompt document",
    ),
    "gdocs-hard-prompts": (
        "Hard AI Prompts (2023)",
        "benchmark",
        "2023 benchmark prompt document",
    ),
    "gdocs-midjourney-physiognomy": (
        "Midjourney Physiognomy Study (2022)",
        "midjourney",
        "2022 Midjourney portrait study spreadsheet",
    ),
    "ideogram-archive": (
        "Ideogram Lifetime Archive (2024–2026)",
        "ideogram",
        "IdeogramHistoryExtractor (ideogram_data_all.json + page_*.json)",
    ),
    "chatgpt-images": (
        "ChatGPT Image Generation (2023–2026)",
        "chatgpt",
        "Exported ChatGPT DALL-E / GPT-Image tool prompts",
    ),
    "mic-local": (
        "MultiImageClient Local (2026)",
        "multi-image-client",
        "Local saves/UiHistory, generation-history.sqlite3, grok_ledger.jsonl, UiGoalLoops",
    ),
    "mic-ave": (
        "MultiImageClient AVE Production (2026)",
        "multi-image-client",
        "AVE production owner-only UiHistory, generation-history, UiGoalLoops",
    ),
    "mic-vibecoders": (
        "MultiImageClient Vibecoders Production (2026)",
        "multi-image-client",
        "Vibecoders production owner-only UiHistory, UiGoalLoops",
    ),
}

WS_RE = re.compile(r"\s+")
IMAGINE_PREFIX_RE = re.compile(r"^\s*/imagine\s+prompt:\s*", re.IGNORECASE)
URL_ONLY_RE = re.compile(r"^https?://\S+$", re.IGNORECASE)


def clean_prompt(text: str) -> str:
    if not text:
        return ""
    s = text.replace("\r\n", "\n").replace("\r", "\n").strip()
    s = IMAGINE_PREFIX_RE.sub("", s).strip()
    return s


def is_skippable_prompt(text: str) -> bool:
    if not text or len(text) < 2:
        return True
    if URL_ONLY_RE.match(text):
        return True
    if text == DEFAULT_DESCRIBE_INSTRUCTION:
        return True
    return False


def compute_norm_hash(kind: str, text: str) -> str:
    norm = WS_RE.sub(" ", text.strip()).lower()
    payload = f"{kind}\0{norm}".encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def parse_iso_utc(raw: str | None) -> str | None:
    if not raw:
        return None
    s = raw.strip()
    if not s:
        return None
    # Try ISO 8601 first
    iso_candidate = s[:-1] + "+00:00" if s.endswith("Z") else s
    # Truncate fractional seconds > 6 digits for Python <3.11 compatibility
    iso_candidate = re.sub(r"\.(\d{6})\d+", r".\1", iso_candidate)
    try:
        dt = datetime.datetime.fromisoformat(iso_candidate)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=datetime.timezone.utc)
        return dt.astimezone(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        pass
    # Try US M/D/YYYY H:MM:SS AM/PM (Google Sheets / C# SocialAI export)
    for fmt in ("%m/%d/%Y %I:%M:%S %p", "%m/%d/%Y %H:%M:%S", "%Y-%m-%d %H:%M:%S", "%Y-%m-%d"):
        try:
            dt = datetime.datetime.strptime(s, fmt).replace(tzinfo=datetime.timezone.utc)
            return dt.strftime("%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            continue
    return None


def unix_to_iso_utc(ts: float | int | None, is_ms: bool = False) -> str | None:
    if ts is None:
        return None
    try:
        val = float(ts)
    except (TypeError, ValueError):
        return None
    if val <= 0:
        return None
    if is_ms:
        val /= 1000.0
    try:
        dt = datetime.datetime.fromtimestamp(val, tz=datetime.timezone.utc)
        return dt.strftime("%Y-%m-%dT%H:%M:%SZ")
    except (OverflowError, OSError, ValueError):
        return None


def year_of(iso_utc: str | None) -> int:
    if not iso_utc or len(iso_utc) < 4:
        return 0
    try:
        return int(iso_utc[:4])
    except ValueError:
        return 0


def format_ideogram_model(version: str | None) -> str:
    if not version:
        return "Ideogram"
    v = version.strip()
    if v.startswith("V_"):
        return "Ideogram " + v[2:].replace("_", ".")
    return f"Ideogram {v}"


def format_midjourney_model(version: str | None, niji: str | None) -> str:
    n = (niji or "").strip()
    if n and n.lower() not in ("0", "false", "no"):
        return f"Midjourney Niji {n}" if n.lower() not in ("true", "1", "yes") else "Midjourney Niji"
    v = (version or "").strip()
    if v:
        return f"Midjourney v{v}"
    return "Midjourney"


def classify_mic_kind(models: list[str]) -> str:
    clean = [m.strip().lower() for m in models if m and m.strip()]
    if not clean:
        return "image"
    if all(m == "grok-web-video" for m in clean):
        return "video"
    if all(m.startswith("describe-") or m == "layout-map" for m in clean):
        return "describe"
    return "image"


class ArchiveBuilder:
    def __init__(self) -> None:
        self.records: list[dict[str, Any]] = []
        self.seen_source_keys: set[tuple[str, str]] = set()
        self.skipped_counts: dict[str, int] = defaultdict(int)

    def add(
        self,
        *,
        source_id: str,
        source_key: str,
        prompt: str,
        kind: str = "image",
        occurred_at_utc: str | None = None,
        models: list[str] | None = None,
        negative_prompt: str = "",
        origin_instance: str = "",
        origin_job_id: str = "",
        details: dict[str, Any] | None = None,
    ) -> None:
        cleaned = clean_prompt(prompt)
        if is_skippable_prompt(cleaned):
            self.skipped_counts[source_id] += 1
            return
        dedupe_occ = (source_id, source_key)
        if dedupe_occ in self.seen_source_keys:
            return
        self.seen_source_keys.add(dedupe_occ)
        clean_models = []
        for m in models or []:
            ms = str(m or "").strip()
            if ms and ms not in clean_models:
                clean_models.append(ms)
        self.records.append(
            {
                "source_id": source_id,
                "source_key": source_key,
                "prompt": cleaned,
                "kind": kind or "image",
                "occurred_at_utc": occurred_at_utc,
                "models": clean_models,
                "negative_prompt": (negative_prompt or "").strip(),
                "origin_instance": (origin_instance or "").strip(),
                "origin_job_id": (origin_job_id or "").strip(),
                "details": details or {},
            }
        )

    def ingest_midjourney_csv(self, path: str, delimiter: str = ",", key_prefix: str = "mj") -> None:
        if not os.path.isfile(path):
            return
        with open(path, "r", encoding="utf-8-sig", newline="") as f:
            reader = csv.DictReader(f, delimiter=delimiter)
            for idx, row in enumerate(reader):
                msg = (row.get("Message") or "").strip()
                if not msg:
                    continue
                iso = parse_iso_utc(row.get("CreatedAtUtc"))
                model = format_midjourney_model(row.get("Version"), row.get("niji"))
                details: dict[str, Any] = {}
                for col, key in [
                    ("GenerationType", "generationType"),
                    ("Chaos", "chaos"),
                    ("AR", "ar"),
                    ("seed", "seed"),
                    ("stylize", "stylize"),
                    ("CreatedChannelName", "channel"),
                    ("refsCSV", "refs"),
                ]:
                    val = (row.get(col) or "").strip()
                    if val:
                        details[key] = val
                occ_key = f"{key_prefix}:{iso or ''}:{idx}:{hashlib.sha1(msg.encode('utf-8')).hexdigest()[:12]}"
                self.add(
                    source_id="gdocs-midjourney-all",
                    source_key=occ_key,
                    prompt=msg,
                    kind="image",
                    occurred_at_utc=iso,
                    models=[model],
                    details=details,
                )

    def ingest_early_2022_doc(self, path: str) -> None:
        if not os.path.isfile(path):
            return
        with open(path, "r", encoding="utf-8-sig") as f:
            for idx, raw_line in enumerate(f):
                line = raw_line.strip()
                if not line:
                    continue
                self.add(
                    source_id="gdocs-early-2022",
                    source_key=f"early2022:{idx}",
                    prompt=line,
                    kind="image",
                    occurred_at_utc=None,
                    models=["Early 2022 (Midjourney / DALL-E)"],
                )

    def ingest_hard_prompts_doc(self, path: str) -> None:
        if not os.path.isfile(path):
            return
        category = ""
        with open(path, "r", encoding="utf-8-sig") as f:
            for idx, raw_line in enumerate(f):
                stripped = raw_line.strip()
                if not stripped:
                    continue
                if stripped.startswith("*"):
                    item = stripped.lstrip("*").strip()
                    # Strip wrapping quotes if present
                    if len(item) >= 2 and item[0] == '"' and item[-1] == '"':
                        item = item[1:-1].strip()
                    if not item or item in ("Physical Configurations", "Riding"):
                        if item:
                            category = item
                        continue
                    # Skip meta-sub-note that isn't a standalone prompt
                    if item.lower().startswith("it should have a single waldo"):
                        continue
                    self.add(
                        source_id="gdocs-hard-prompts",
                        source_key=f"hard:{idx}",
                        prompt=item,
                        kind="image",
                        occurred_at_utc="2023-05-17T00:00:00Z",
                        models=["Benchmark / Hard Prompt"],
                        details={"category": category} if category else {},
                    )
                else:
                    if stripped not in ("Hard AI Prompts",) and not stripped.startswith(("Testing overfitting", "Realization:")):
                        category = stripped

    def ingest_midjourney_physiognomy(self, path: str) -> None:
        if not os.path.isfile(path):
            return
        with open(path, "r", encoding="utf-8-sig", newline="") as f:
            reader = csv.reader(f)
            for idx, row in enumerate(reader):
                if len(row) < 4:
                    continue
                trait = (row[2] or "").strip()
                prompt_cell = (row[3] or "").strip()
                if not trait or not prompt_cell or not prompt_cell.lower().startswith("/imagine"):
                    continue
                self.add(
                    source_id="gdocs-midjourney-physiognomy",
                    source_key=f"phys:{idx}:{trait}",
                    prompt=prompt_cell,
                    kind="image",
                    occurred_at_utc="2022-12-27T00:00:00Z",
                    models=["Midjourney"],
                    details={"trait": trait},
                )

    def ingest_ideogram(self, extractor_dir: str) -> None:
        by_req_id: dict[str, dict[str, Any]] = {}
        all_json = os.path.join(extractor_dir, "ideogram_data_all.json")
        if os.path.isfile(all_json):
            with open(all_json, "r", encoding="utf-8") as f:
                data = json.load(f)
                if isinstance(data, list):
                    for item in data:
                        if isinstance(item, dict) and item.get("request_id"):
                            by_req_id[str(item["request_id"])] = item
        for page_path in sorted(glob.glob(os.path.join(extractor_dir, "ideogram_requests", "page_*.json"))):
            try:
                with open(page_path, "r", encoding="utf-8") as f:
                    d = json.load(f)
            except Exception:
                continue
            items = d if isinstance(d, list) else (d.get("requests") or d.get("data") or [])
            for item in items:
                if isinstance(item, dict) and item.get("request_id"):
                    rid = str(item["request_id"])
                    if rid not in by_req_id:
                        by_req_id[rid] = item
        for rid, item in by_req_id.items():
            prompt = (item.get("user_prompt") or "").strip()
            if not prompt:
                continue
            iso = unix_to_iso_utc(item.get("creation_time_float"), is_ms=False)
            model = format_ideogram_model(item.get("model_version"))
            details: dict[str, Any] = {}
            for src_k, dst_k in [
                ("request_type", "requestType"),
                ("aspect_ratio", "ar"),
                ("resolution", "resolution"),
                ("style_expert", "style"),
            ]:
                val = item.get(src_k)
                if val:
                    details[dst_k] = str(val)
            if item.get("private"):
                details["private"] = True
            self.add(
                source_id="ideogram-archive",
                source_key=f"ideo:{rid}",
                prompt=prompt,
                kind="image",
                occurred_at_utc=iso,
                models=[model],
                negative_prompt=str(item.get("user_negative_prompt") or ""),
                details=details,
            )

    def ingest_chatgpt_jsonl(self, path: str) -> None:
        if not os.path.isfile(path):
            return
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                rec = json.loads(line)
                details: dict[str, Any] = {}
                if rec.get("conversation_title"):
                    details["conversationTitle"] = rec["conversation_title"]
                if rec.get("size"):
                    details["size"] = rec["size"]
                self.add(
                    source_id="chatgpt-images",
                    source_key=str(rec["source_key"]),
                    prompt=str(rec.get("prompt") or ""),
                    kind="image",
                    occurred_at_utc=parse_iso_utc(rec.get("occurred_at_utc")),
                    models=[str(rec.get("model") or "ChatGPT Image")],
                    details=details,
                )

    def ingest_mic_local(self, saves_dir: str) -> None:
        seen_ui_jobs: set[str] = set()
        # 1) Local UiHistory (clean unsuffixed user prompts)
        hist_dir = os.path.join(saves_dir, "UiHistory")
        if os.path.isdir(hist_dir):
            for p in sorted(glob.glob(os.path.join(hist_dir, "*", "job.json"))):
                try:
                    d = json.load(open(p, encoding="utf-8"))
                except Exception:
                    continue
                jid = str(d.get("Id") or os.path.basename(os.path.dirname(p)))
                seen_ui_jobs.add(jid)
                gens = [str(g) for g in (d.get("GeneratorKeys") or []) if g]
                prompt = str(d.get("Prompt") or "")
                kind = classify_mic_kind(gens)
                self.add(
                    source_id="mic-local",
                    source_key=f"local:ui:{jid}",
                    prompt=prompt,
                    kind=kind,
                    occurred_at_utc=parse_iso_utc(d.get("CreatedAt")),
                    models=gens or ["MultiImageClient"],
                    origin_instance="local",
                    origin_job_id=jid,
                )
        # 2) Local generation-history.sqlite3 (only CLI / non-UiHistory runs so per-endpoint suffixes don't duplicate UiHistory)
        db_path = os.path.join(saves_dir, "generation-history.sqlite3")
        if os.path.isfile(db_path):
            c = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
            for row_id, src, ext_jid, gen_key, gen_api, started, prompt in c.execute(
                "SELECT id, source, external_job_id, generator_key, generator_api_type, started_at_utc, prompt "
                "FROM generation_attempts"
            ):
                if src == "ui" and str(ext_jid or "") in seen_ui_jobs:
                    continue
                model = (gen_key or gen_api or "").strip()
                kind = classify_mic_kind([model] if model else [])
                self.add(
                    source_id="mic-local",
                    source_key=f"local:attempt:{row_id}",
                    prompt=str(prompt or ""),
                    kind=kind,
                    occurred_at_utc=parse_iso_utc(started),
                    models=[model] if model else ["MultiImageClient"],
                    origin_instance="local" if src == "ui" and ext_jid else "",
                    origin_job_id=str(ext_jid or "") if src == "ui" else "",
                )
            c.close()
        # 3) Local grok_ledger.jsonl
        ledger_path = os.path.join(saves_dir, "grok_ledger.jsonl")
        if os.path.isfile(ledger_path):
            with open(ledger_path, "r", encoding="utf-8") as f:
                for idx, line in enumerate(f):
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        rec = json.loads(line)
                    except Exception:
                        continue
                    kind = "video" if str(rec.get("kind") or "").lower() == "video" else "image"
                    req_id = str(rec.get("requestId") or idx)
                    self.add(
                        source_id="mic-local",
                        source_key=f"local:grok:{req_id}:{idx}",
                        prompt=str(rec.get("prompt") or ""),
                        kind=kind,
                        occurred_at_utc=parse_iso_utc(rec.get("timestampUtc")),
                        models=[str(rec.get("model") or "grok-api")],
                    )
        # 4) Local UiGoalLoops
        loop_dir = os.path.join(saves_dir, "UiGoalLoops")
        if os.path.isdir(loop_dir):
            for p in sorted(glob.glob(os.path.join(loop_dir, "*", "loop.json"))):
                try:
                    d = json.load(open(p, encoding="utf-8"))
                except Exception:
                    continue
                lid = str(d.get("id") or os.path.basename(os.path.dirname(p)))
                goal = str(d.get("goal") or d.get("Goal") or "")
                model = str(d.get("managerLabel") or d.get("managerModel") or "Goal Loop")
                self.add(
                    source_id="mic-local",
                    source_key=f"local:goal:{lid}",
                    prompt=goal,
                    kind="goal",
                    occurred_at_utc=unix_to_iso_utc(d.get("createdAtUnixMs"), is_ms=True),
                    models=[model],
                    details={"goalLoopId": lid},
                )

    def ingest_prod_snapshot(self, path: str) -> None:
        if not os.path.isfile(path):
            return
        snap = json.load(open(path, "r", encoding="utf-8"))
        for key, source_id in [("ave", "mic-ave"), ("vibecoders", "mic-vibecoders")]:
            rows = snap.get(key) or []
            ui_job_ids = {
                str(r.get("job_id") or "")
                for r in rows
                if ":ui:" in str(r.get("source_key") or "") and r.get("job_id")
            }
            for rec in rows:
                skey = str(rec["source_key"])
                jid = str(rec.get("job_id") or "")
                if ":attempt:" in skey and jid and jid in ui_job_ids:
                    continue
                iso = parse_iso_utc(rec.get("occurred_at_utc"))
                if not iso and rec.get("occurred_at_unix_ms"):
                    iso = unix_to_iso_utc(rec.get("occurred_at_unix_ms"), is_ms=True)
                models = list(rec.get("models") or [])
                raw_kind = str(rec.get("kind") or "")
                kind = raw_kind if raw_kind == "goal" else classify_mic_kind(models)
                self.add(
                    source_id=source_id,
                    source_key=skey,
                    prompt=str(rec.get("prompt") or ""),
                    kind=kind,
                    occurred_at_utc=iso,
                    models=models,
                    origin_instance=str(rec.get("instance") or ""),
                    origin_job_id=jid,
                )

    def write_sqlite(self, output_path: str) -> dict[str, Any]:
        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        fd, tmp_path = tempfile.mkstemp(prefix=".prompt-archive-", suffix=".sqlite3", dir=os.path.dirname(os.path.abspath(output_path)))
        os.close(fd)
        try:
            conn = sqlite3.connect(tmp_path)
            conn.execute("PRAGMA journal_mode = DELETE;")
            conn.execute("PRAGMA synchronous = OFF;")
            conn.execute("PRAGMA temp_store = MEMORY;")
            conn.executescript(
                """
                CREATE TABLE meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE sources (
                    source_id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    category TEXT NOT NULL,
                    origin_hint TEXT NOT NULL,
                    total_occurrences INTEGER NOT NULL DEFAULT 0,
                    unique_prompts INTEGER NOT NULL DEFAULT 0,
                    earliest_utc TEXT,
                    latest_utc TEXT
                );
                CREATE TABLE prompts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    norm_hash TEXT NOT NULL UNIQUE,
                    prompt_text TEXT NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'image',
                    char_len INTEGER NOT NULL,
                    word_count INTEGER NOT NULL,
                    first_seen_utc TEXT,
                    last_seen_utc TEXT,
                    first_year INTEGER NOT NULL DEFAULT 0,
                    last_year INTEGER NOT NULL DEFAULT 0,
                    occurrence_count INTEGER NOT NULL DEFAULT 1,
                    primary_source TEXT NOT NULL,
                    sources_json TEXT NOT NULL,
                    models_json TEXT NOT NULL,
                    negative_prompt TEXT NOT NULL DEFAULT '',
                    meta_text TEXT NOT NULL DEFAULT '',
                    extra_meta_json TEXT NOT NULL DEFAULT '{}',
                    origin_instance TEXT NOT NULL DEFAULT '',
                    origin_job_id TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX idx_prompts_first_year ON prompts(first_year);
                CREATE INDEX idx_prompts_last_year ON prompts(last_year);
                CREATE INDEX idx_prompts_last_seen ON prompts(last_seen_utc DESC);
                CREATE INDEX idx_prompts_first_seen ON prompts(first_seen_utc ASC);
                CREATE INDEX idx_prompts_char_len ON prompts(char_len);
                CREATE INDEX idx_prompts_occ_count ON prompts(occurrence_count DESC);
                CREATE INDEX idx_prompts_kind ON prompts(kind);
                CREATE INDEX idx_prompts_primary_source ON prompts(primary_source);

                CREATE TABLE prompt_sources (
                    prompt_id INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    PRIMARY KEY (prompt_id, source_id)
                );
                CREATE INDEX idx_prompt_sources_source ON prompt_sources(source_id, prompt_id);

                CREATE TABLE prompt_occurrences (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    prompt_id INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    source_key TEXT NOT NULL,
                    occurred_at_utc TEXT,
                    model TEXT NOT NULL DEFAULT '',
                    origin_instance TEXT NOT NULL DEFAULT '',
                    origin_job_id TEXT NOT NULL DEFAULT '',
                    details_json TEXT NOT NULL DEFAULT '{}',
                    UNIQUE (source_id, source_key)
                );
                CREATE INDEX idx_prompt_occurrences_prompt ON prompt_occurrences(prompt_id, occurred_at_utc DESC);

                CREATE VIRTUAL TABLE prompts_fts USING fts5(
                    prompt_text,
                    models_json,
                    meta_text,
                    content='prompts',
                    content_rowid='id',
                    tokenize='unicode61'
                );
                CREATE TRIGGER prompts_ai AFTER INSERT ON prompts BEGIN
                    INSERT INTO prompts_fts(rowid, prompt_text, models_json, meta_text)
                    VALUES (new.id, new.prompt_text, new.models_json, new.meta_text);
                END;
                CREATE TRIGGER prompts_ad AFTER DELETE ON prompts BEGIN
                    INSERT INTO prompts_fts(prompts_fts, rowid, prompt_text, models_json, meta_text)
                    VALUES ('delete', old.id, old.prompt_text, old.models_json, old.meta_text);
                END;
                CREATE TRIGGER prompts_au AFTER UPDATE OF prompt_text, models_json, meta_text ON prompts BEGIN
                    INSERT INTO prompts_fts(prompts_fts, rowid, prompt_text, models_json, meta_text)
                    VALUES ('delete', old.id, old.prompt_text, old.models_json, old.meta_text);
                    INSERT INTO prompts_fts(rowid, prompt_text, models_json, meta_text)
                    VALUES (new.id, new.prompt_text, new.models_json, new.meta_text);
                END;
                """
            )

            # Group occurrences by (kind, norm_hash)
            groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
            for rec in self.records:
                h = compute_norm_hash(rec["kind"], rec["prompt"])
                groups[h].append(rec)

            # Sort canonical prompts deterministically (newest first, then hash)
            def group_sort_key(item: tuple[str, list[dict[str, Any]]]) -> tuple[str, str]:
                h, occs = item
                dates = [o["occurred_at_utc"] for o in occs if o["occurred_at_utc"]]
                return (max(dates) if dates else "", h)

            sorted_groups = sorted(groups.items(), key=group_sort_key)

            source_occ_counts: dict[str, int] = defaultdict(int)
            source_unique_counts: dict[str, int] = defaultdict(int)
            source_earliest: dict[str, str] = {}
            source_latest: dict[str, str] = {}

            cur = conn.cursor()
            cur.execute("BEGIN")
            for norm_hash, occs in sorted_groups:
                # Choose best display text: prefer multiline / original mixed case / longest
                best_prompt = max(
                    (o["prompt"] for o in occs),
                    key=lambda s: (
                        "\n" in s,
                        any(c.isupper() for c in s),
                        len(s),
                    ),
                )
                kind = occs[0]["kind"]
                dates = sorted(o["occurred_at_utc"] for o in occs if o["occurred_at_utc"])
                first_seen = dates[0] if dates else None
                last_seen = dates[-1] if dates else None

                # Primary source = source of earliest dated occurrence, else first source
                dated_occs = [o for o in occs if o["occurred_at_utc"]]
                if dated_occs:
                    dated_occs.sort(key=lambda o: (o["occurred_at_utc"], o["source_id"], o["source_key"]))
                    primary_source = dated_occs[0]["source_id"]
                else:
                    primary_source = occs[0]["source_id"]

                sources_ordered: list[str] = []
                models_ordered: list[str] = []
                neg_prompt = ""
                extra_meta: dict[str, Any] = {}
                meta_tokens: list[str] = []
                origin_instance = ""
                origin_job_id = ""
                origin_ts = ""

                for o in occs:
                    sid = o["source_id"]
                    if sid not in sources_ordered:
                        sources_ordered.append(sid)
                    source_occ_counts[sid] += 1
                    ts = o["occurred_at_utc"]
                    if ts:
                        if sid not in source_earliest or ts < source_earliest[sid]:
                            source_earliest[sid] = ts
                        if sid not in source_latest or ts > source_latest[sid]:
                            source_latest[sid] = ts
                    for m in o["models"]:
                        if m not in models_ordered:
                            models_ordered.append(m)
                    if o["negative_prompt"] and len(o["negative_prompt"]) > len(neg_prompt):
                        neg_prompt = o["negative_prompt"]
                    for k, v in (o["details"] or {}).items():
                        if k not in extra_meta and v not in ("", None, False):
                            extra_meta[k] = v
                        if isinstance(v, str) and v and k in ("conversationTitle", "category", "channel", "trait", "style"):
                            if v not in meta_tokens:
                                meta_tokens.append(v)
                    if o["origin_instance"] and o["origin_job_id"]:
                        cand_ts = ts or ""
                        if not origin_job_id or cand_ts >= origin_ts:
                            origin_instance = o["origin_instance"]
                            origin_job_id = o["origin_job_id"]
                            origin_ts = cand_ts

                for sid in sources_ordered:
                    source_unique_counts[sid] += 1

                if neg_prompt:
                    meta_tokens.append(neg_prompt)
                meta_text = " | ".join(meta_tokens)
                word_count = len(best_prompt.split())

                cur.execute(
                    """
                    INSERT INTO prompts (
                        norm_hash, prompt_text, kind, char_len, word_count,
                        first_seen_utc, last_seen_utc, first_year, last_year,
                        occurrence_count, primary_source, sources_json, models_json,
                        negative_prompt, meta_text, extra_meta_json, origin_instance, origin_job_id
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        norm_hash,
                        best_prompt,
                        kind,
                        len(best_prompt),
                        word_count,
                        first_seen,
                        last_seen,
                        year_of(first_seen),
                        year_of(last_seen),
                        len(occs),
                        primary_source,
                        json.dumps(sources_ordered, ensure_ascii=False),
                        json.dumps(models_ordered, ensure_ascii=False),
                        neg_prompt,
                        meta_text,
                        json.dumps(extra_meta, ensure_ascii=False),
                        origin_instance,
                        origin_job_id,
                    ),
                )
                prompt_id = cur.lastrowid

                for sid in sources_ordered:
                    cur.execute(
                        "INSERT INTO prompt_sources (prompt_id, source_id) VALUES (?, ?)",
                        (prompt_id, sid),
                    )
                for o in occs:
                    cur.execute(
                        """
                        INSERT INTO prompt_occurrences (
                            prompt_id, source_id, source_key, occurred_at_utc,
                            model, origin_instance, origin_job_id, details_json
                        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                        """,
                        (
                            prompt_id,
                            o["source_id"],
                            o["source_key"],
                            o["occurred_at_utc"],
                            ", ".join(o["models"]),
                            o["origin_instance"],
                            o["origin_job_id"],
                            json.dumps(o["details"], ensure_ascii=False),
                        ),
                    )

            for sid, (label, category, origin_hint) in SOURCE_DEFS.items():
                if source_occ_counts[sid] == 0:
                    continue
                cur.execute(
                    """
                    INSERT INTO sources (
                        source_id, label, category, origin_hint,
                        total_occurrences, unique_prompts, earliest_utc, latest_utc
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        sid,
                        label,
                        category,
                        origin_hint,
                        source_occ_counts[sid],
                        source_unique_counts[sid],
                        source_earliest.get(sid),
                        source_latest.get(sid),
                    ),
                )

            now_iso = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            cur.executemany(
                "INSERT INTO meta (key, value) VALUES (?, ?)",
                [
                    ("schema_version", SCHEMA_VERSION),
                    ("importer_version", IMPORTER_VERSION),
                    ("built_at_utc", now_iso),
                ],
            )
            conn.commit()
            conn.execute("INSERT INTO prompts_fts(prompts_fts) VALUES('optimize');")
            conn.execute("ANALYZE;")
            conn.commit()
            conn.execute("VACUUM;")

            integrity = conn.execute("PRAGMA integrity_check;").fetchone()[0]
            if integrity != "ok":
                raise RuntimeError(f"SQLite integrity check failed: {integrity}")
            summary = {
                "output_path": output_path,
                "unique_prompts": conn.execute("SELECT COUNT(*) FROM prompts").fetchone()[0],
                "total_occurrences": conn.execute("SELECT COUNT(*) FROM prompt_occurrences").fetchone()[0],
                "by_kind": dict(conn.execute("SELECT kind, COUNT(*) FROM prompts GROUP BY kind").fetchall()),
                "by_year": dict(conn.execute("SELECT first_year, COUNT(*) FROM prompts GROUP BY first_year ORDER BY first_year").fetchall()),
                "sources": [
                    {
                        "id": r[0],
                        "label": r[1],
                        "unique": r[2],
                        "occurrences": r[3],
                        "earliest": r[4],
                        "latest": r[5],
                    }
                    for r in conn.execute(
                        "SELECT source_id, label, unique_prompts, total_occurrences, earliest_utc, latest_utc FROM sources ORDER BY earliest_utc ASC NULLS FIRST"
                    ).fetchall()
                ],
                "skipped": dict(self.skipped_counts),
            }
            conn.close()
            os.chmod(tmp_path, 0o600)
            os.replace(tmp_path, output_path)
            summary["size_bytes"] = os.path.getsize(output_path)
            return summary
        except Exception:
            if os.path.exists(tmp_path):
                os.unlink(tmp_path)
            raise


def main() -> int:
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    parser = argparse.ArgumentParser(description="Build owner prompt-archive.sqlite3 from local + staged sources.")
    parser.add_argument(
        "--sources-dir",
        default=os.path.join(repo_root, "saves", "prompt-archive-sources"),
        help="Directory holding staged external prompt exports and production snapshots.",
    )
    parser.add_argument("--midjourney-csv", default=None, help="Optional Midjourney CSV export path.")
    parser.add_argument("--midjourney-tsv", default=None, help="Optional Midjourney TSV export path.")
    parser.add_argument("--early-doc", default=None, help="Optional early plain-text prompt doc path.")
    parser.add_argument("--hard-prompts-doc", default=None, help="Optional hard prompts doc path.")
    parser.add_argument("--physiognomy-csv", default=None, help="Optional Midjourney study CSV path.")
    parser.add_argument("--chatgpt-jsonl", default=None, help="Optional ChatGPT image prompts JSONL path.")
    parser.add_argument("--prod-snapshot", default=None, help="Optional owner-only production snapshot JSON path.")
    parser.add_argument(
        "--saves-dir",
        default=os.path.join(repo_root, "saves"),
        help="Local MultiImageClient saves directory.",
    )
    parser.add_argument(
        "--ideogram-dir",
        default=os.path.join(repo_root, "IdeogramHistoryExtractor"),
        help="IdeogramHistoryExtractor directory.",
    )
    parser.add_argument(
        "--output",
        default=os.path.join(repo_root, "saves", "prompt-archive.sqlite3"),
        help="Destination SQLite path.",
    )
    args = parser.parse_args()

    builder = ArchiveBuilder()
    builder.ingest_midjourney_csv(args.midjourney_csv or os.path.join(args.sources_dir, "midjourney_prompts.csv"), delimiter=",", key_prefix="mj-sheet")
    builder.ingest_midjourney_csv(args.midjourney_tsv or os.path.join(args.sources_dir, "midjourney_prompts.tsv"), delimiter="\t", key_prefix="mj-tsv")
    builder.ingest_early_2022_doc(args.early_doc or os.path.join(args.sources_dir, "early_ai_image_prompts.txt"))
    builder.ingest_hard_prompts_doc(args.hard_prompts_doc or os.path.join(args.sources_dir, "hard_ai_prompts.txt"))
    builder.ingest_midjourney_physiognomy(args.physiognomy_csv or os.path.join(args.sources_dir, "midjourney_physiognomy.csv"))
    builder.ingest_ideogram(args.ideogram_dir)
    builder.ingest_chatgpt_jsonl(args.chatgpt_jsonl or os.path.join(args.sources_dir, "chatgpt_image_prompts.jsonl"))
    builder.ingest_mic_local(args.saves_dir)
    builder.ingest_prod_snapshot(args.prod_snapshot or os.path.join(args.sources_dir, "prod_owner_snapshot.json"))

    summary = builder.write_sqlite(args.output)
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
