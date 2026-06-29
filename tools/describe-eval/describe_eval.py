#!/usr/bin/env python3
"""
Build portrait fixtures and test whether describe endpoints mention the visible
facts in a plain prompt.

Default mode is cost-safe: it writes only the manifest. Add --generate and/or
--describe to call paid APIs.
"""

from __future__ import annotations

import argparse
import base64
import csv
import itertools
import json
import mimetypes
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable


AGES = ["18", "28", "42"]
GENDERS = [("man", "men"), ("woman", "women")]
ETHNICITIES = [
    ("south_korean", "South Korean", ["south korean", "korean"]),
    ("japanese", "Japanese", ["japanese"]),
    ("chinese", "Chinese", ["chinese"]),
    ("english", "English", ["english", "british"]),
    ("egyptian", "Egyptian", ["egyptian"]),
    ("african", "African", ["african", "black"]),
    ("russian", "Russian", ["russian"]),
    ("polish", "Polish", ["polish"]),
]
DEFAULT_DESCRIBE_PROMPT = (
    "Describe only the people in this image, not the location or background. "
    "Tell the truth and be specific. Include: the number of people, your best "
    "guess at their sex, approximate age or age range, ethnicity or ancestry if "
    "visually apparent, detailed clothing, and the style or body look they seem "
    "to be going for (for example sporty, natural, tailored, bodycon, casual, "
    "relaxed, polished). If something is unclear, say it is unclear."
)
ScoreCategories = ["people_count", "gender", "age", "ethnicity", "clothing", "style_body"]


@dataclass(frozen=True)
class Case:
    case_id: str
    ethnicity_key: str
    ethnicity_label: str
    gender: str
    gender_plural: str
    age: str
    prompt: str


def build_cases() -> list[Case]:
    cases: list[Case] = []
    for (ethnicity_key, ethnicity_label, _), (gender, gender_plural), age in itertools.product(
        ETHNICITIES,
        GENDERS,
        AGES,
    ):
        prompt = (
            f"A natural full-body street photo of two {age}-year-old {ethnicity_label} {gender_plural} "
            "walking together along a palm-lined street in Florida. "
            "They seem relaxed, cheerful, and comfortable with each other, like a casual photo they might post on Instagram. "
            "Realistic candid photography, clear full normal daytime lighting, bright exposure, casual outfits, clear head-to-toe view. "
            "Do not make the image dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark."
        )
        case_id = f"{ethnicity_key}__{gender_plural}__age_{age}__friends__florida_walk"
        cases.append(Case(case_id, ethnicity_key, ethnicity_label, gender, gender_plural, age, prompt))
    return cases


def load_settings(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def setting_or_env(settings: dict[str, Any], setting_name: str, env_name: str) -> str:
    value = os.environ.get(env_name) or settings.get(setting_name) or ""
    if isinstance(value, str) and (value.startswith("Optional:") or value.startswith("REQUIRED:")):
        return ""
    return str(value).strip()


def post_json(url: str, headers: dict[str, str], payload: dict[str, Any], timeout: int = 180) -> dict[str, Any]:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=headers | {"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            return json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        body = ex.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {ex.code} from {url}: {body}") from ex


def download_url(url: str, timeout: int = 180) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "multiImageClient-describe-eval/1.0"})
    with urllib.request.urlopen(req, timeout=timeout) as res:
        return res.read()


def image_data_uri(path: Path) -> tuple[str, str, str]:
    data = path.read_bytes()
    mime = mimetypes.guess_type(path.name)[0] or "image/png"
    b64 = base64.b64encode(data).decode("ascii")
    return f"data:{mime};base64,{b64}", b64, mime


def write_manifest(cases: list[Case], run_dir: Path) -> Path:
    run_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = run_dir / "manifest.jsonl"
    with manifest_path.open("w", encoding="utf-8") as f:
        for case in cases:
            f.write(json.dumps(asdict(case), ensure_ascii=False) + "\n")
    return manifest_path


def generate_openai(case: Case, settings: dict[str, Any], args: argparse.Namespace) -> bytes:
    api_key = setting_or_env(settings, "OpenAIApiKey", "OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing OpenAIApiKey or OPENAI_API_KEY.")
    payload = {
        "model": args.openai_image_model,
        "prompt": case.prompt,
        "size": args.size,
        "quality": args.quality,
        "n": 1,
    }
    if args.openai_moderation:
        payload["moderation"] = args.openai_moderation
    response = post_json("https://api.openai.com/v1/images/generations", {"Authorization": f"Bearer {api_key}"}, payload, args.timeout)
    item = (response.get("data") or [{}])[0]
    if item.get("b64_json"):
        return base64.b64decode(item["b64_json"])
    if item.get("url"):
        return download_url(item["url"], args.timeout)
    raise RuntimeError(f"OpenAI image response had no b64_json or url: {response}")


def generate_grok(case: Case, settings: dict[str, Any], args: argparse.Namespace) -> bytes:
    api_key = setting_or_env(settings, "XAIGrokApiKey", "XAI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing XAIGrokApiKey or XAI_API_KEY.")
    payload = {
        "model": args.grok_image_model,
        "prompt": case.prompt,
        "aspect_ratio": "1:1",
        "quality": "high",
        "resolution": args.grok_resolution,
        "n": 1,
        "response_format": "b64_json",
    }
    response = post_json("https://api.x.ai/v1/images/generations", {"Authorization": f"Bearer {api_key}"}, payload, args.timeout)
    item = (response.get("data") or [{}])[0]
    if item.get("b64_json"):
        return base64.b64decode(item["b64_json"])
    if item.get("url"):
        return download_url(item["url"], args.timeout)
    raise RuntimeError(f"xAI image response had no b64_json or url: {response}")


GENERATORS: dict[str, Callable[[Case, dict[str, Any], argparse.Namespace], bytes]] = {
    "openai": generate_openai,
    "grok": generate_grok,
}


def generate_images(cases: list[Case], settings: dict[str, Any], args: argparse.Namespace, images_dir: Path) -> None:
    images_dir.mkdir(parents=True, exist_ok=True)
    selected = cases[: args.limit] if args.limit else cases
    generator = GENERATORS[args.generator]
    for idx, case in enumerate(selected, 1):
        path = images_dir / f"{case.case_id}.png"
        if path.exists() and not args.overwrite:
            print(f"[generate {idx}/{len(selected)}] skip existing {path.name}")
            continue
        print(f"[generate {idx}/{len(selected)}] {case.case_id}: {case.prompt}", flush=True)
        path.write_bytes(generator(case, settings, args))
        time.sleep(args.sleep)


def extract_text_from_response(response: dict[str, Any]) -> str:
    if isinstance(response.get("output_text"), str):
        return response["output_text"]
    chunks: list[str] = []
    for output in response.get("output", []) or []:
        for content in output.get("content", []) or []:
            text = content.get("text") or content.get("output_text")
            if isinstance(text, str):
                chunks.append(text)
    if chunks:
        return "\n".join(chunks)
    try:
        return response["choices"][0]["message"]["content"]
    except Exception:
        return json.dumps(response, ensure_ascii=False)


def extract_json_object(text: str) -> dict[str, Any]:
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    match = re.search(r"\{.*\}", text, re.DOTALL)
    if not match:
        raise ValueError(f"Response did not contain a JSON object: {text[:500]}")
    return json.loads(match.group(0))


def clamp_score(value: Any) -> float:
    try:
        score = float(value)
    except (TypeError, ValueError):
        return 0.0
    if score >= 0.75:
        return 1.0
    if score >= 0.25:
        return 0.5
    return 0.0


def normalize_llm_score(raw: dict[str, Any]) -> dict[str, Any]:
    categories = raw.get("categories") if isinstance(raw.get("categories"), dict) else {}
    normalized_categories: dict[str, Any] = {}
    checks: dict[str, float] = {}

    for category in ScoreCategories:
        item = categories.get(category, {})
        if not isinstance(item, dict):
            item = {"score": item}
        score = clamp_score(item.get("score"))
        checks[category] = score
        normalized_categories[category] = {
            "score": score,
            "extracted": str(item.get("extracted", "") or ""),
            "reason": str(item.get("reason", "") or ""),
        }

    return {
        **checks,
        "score": sum(checks.values()),
        "possible": float(len(ScoreCategories)),
        "judge": raw.get("judge", "llm_text_judge"),
        "categories": normalized_categories,
    }


def build_score_prompt(case: Case, response_text: str) -> str:
    expected = {
        "people_count": "two people",
        "gender": case.gender_plural,
        "age": f"about {case.age} years old",
        "ethnicity": case.ethnicity_label,
        "clothing": "any concrete clothing description",
        "style_body": "any concrete style, fashion, body look, build, or aesthetic description",
    }
    return (
        "You are scoring an image description response. You are NOT looking at the image. "
        "Your only job is to decide whether the response text contains claims matching the expected fixture metadata. "
        "Use semantic understanding, not keyword matching.\n\n"
        "Return only a valid JSON object in this exact shape:\n"
        "{\n"
        '  "judge": "llm_text_judge",\n'
        '  "categories": {\n'
        '    "people_count": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"},\n'
        '    "gender": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"},\n'
        '    "age": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"},\n'
        '    "ethnicity": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"},\n'
        '    "clothing": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"},\n'
        '    "style_body": {"score": 0|0.5|1, "extracted": "text found or empty", "reason": "short reason"}\n'
        "  }\n"
        "}\n\n"
        "Scoring rules:\n"
        "- Score 1 when the response clearly contains the expected information or a reasonable approximation.\n"
        "- Score 0.5 for broad but compatible information, such as 'Asian' for Chinese/Japanese/South Korean, "
        "'European' for English/Russian/Polish, or an age range that overlaps but is loose.\n"
        "- Score 0 when the category is omitted, contradicted, or only says it is unclear.\n"
        "- For clothing and style_body, score whether the response contains a real description; do not compare to hidden ground truth.\n"
        "- For age, accept ranges like '30s or 40s' for a 42-year-old and '20s' for a 28-year-old.\n\n"
        f"Expected fixture metadata:\n{json.dumps(expected, ensure_ascii=False)}\n\n"
        f"Response text to score:\n{response_text}"
    )


def score_description_with_llm(case: Case, text: str, settings: dict[str, Any], args: argparse.Namespace) -> dict[str, Any]:
    api_key = setting_or_env(settings, "OpenAIApiKey", "OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing OpenAIApiKey or OPENAI_API_KEY for LLM scoring.")
    payload = {
        "model": args.score_judge_model,
        "messages": [{"role": "user", "content": build_score_prompt(case, text)}],
        "temperature": 0,
        "max_tokens": args.score_max_tokens,
        "response_format": {"type": "json_object"},
    }
    response = post_json("https://api.openai.com/v1/chat/completions", {"Authorization": f"Bearer {api_key}"}, payload, args.timeout)
    return normalize_llm_score(extract_json_object(extract_text_from_response(response)))


def describe_openai(image_path: Path, settings: dict[str, Any], args: argparse.Namespace) -> str:
    api_key = setting_or_env(settings, "OpenAIApiKey", "OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing OpenAIApiKey or OPENAI_API_KEY.")
    data_uri, _, _ = image_data_uri(image_path)
    payload = {
        "model": args.openai_vision_model,
        "input": [{
            "role": "user",
            "content": [
                {"type": "input_text", "text": args.describe_prompt},
                {"type": "input_image", "image_url": data_uri, "detail": args.detail},
            ],
        }],
        "max_output_tokens": args.max_tokens,
    }
    return extract_text_from_response(post_json("https://api.openai.com/v1/responses", {"Authorization": f"Bearer {api_key}"}, payload, args.timeout))


def describe_grok(image_path: Path, settings: dict[str, Any], args: argparse.Namespace) -> str:
    api_key = setting_or_env(settings, "XAIGrokApiKey", "XAI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing XAIGrokApiKey or XAI_API_KEY.")
    data_uri, _, _ = image_data_uri(image_path)
    payload = {
        "model": args.grok_vision_model,
        "input": [{
            "role": "user",
            "content": [
                {"type": "input_image", "image_url": data_uri, "detail": args.detail},
                {"type": "input_text", "text": args.describe_prompt},
            ],
        }],
        "max_output_tokens": args.max_tokens,
    }
    return extract_text_from_response(post_json("https://api.x.ai/v1/responses", {"Authorization": f"Bearer {api_key}"}, payload, args.timeout))


def describe_gemini(image_path: Path, settings: dict[str, Any], args: argparse.Namespace) -> str:
    api_key = setting_or_env(settings, "GoogleGeminiApiKey", "GOOGLE_GEMINI_API_KEY")
    if not api_key:
        raise RuntimeError("Missing GoogleGeminiApiKey or GOOGLE_GEMINI_API_KEY.")
    _, b64, mime = image_data_uri(image_path)
    model = urllib.parse.quote(args.gemini_vision_model, safe="")
    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={urllib.parse.quote(api_key)}"
    payload = {
        "contents": [{"role": "user", "parts": [{"text": args.describe_prompt}, {"inline_data": {"mime_type": mime, "data": b64}}]}],
        "generationConfig": {"maxOutputTokens": args.max_tokens, "temperature": 0.0, "thinkingConfig": {"thinkingBudget": 0}},
    }
    response = post_json(url, {}, payload, args.timeout)
    parts = response.get("candidates", [{}])[0].get("content", {}).get("parts", [])
    return "\n".join(part.get("text", "") for part in parts).strip() or json.dumps(response, ensure_ascii=False)


def describe_claude(image_path: Path, settings: dict[str, Any], args: argparse.Namespace) -> str:
    api_key = setting_or_env(settings, "AnthropicApiKey", "ANTHROPIC_API_KEY")
    if not api_key:
        raise RuntimeError("Missing AnthropicApiKey or ANTHROPIC_API_KEY.")
    _, b64, mime = image_data_uri(image_path)
    payload = {
        "model": args.claude_vision_model,
        "max_tokens": args.max_tokens,
        "temperature": 0.0,
        "messages": [{
            "role": "user",
            "content": [
                {"type": "image", "source": {"type": "base64", "media_type": mime, "data": b64}},
                {"type": "text", "text": args.describe_prompt},
            ],
        }],
    }
    response = post_json("https://api.anthropic.com/v1/messages", {"x-api-key": api_key, "anthropic-version": "2023-06-01"}, payload, args.timeout)
    return "\n".join(item.get("text", "") for item in response.get("content", []) if item.get("type") == "text").strip() or json.dumps(response, ensure_ascii=False)


def encode_multipart(fields: dict[str, str], files: dict[str, tuple[str, str, bytes]]) -> tuple[bytes, str]:
    boundary = f"----multiImageClientDescribeEval{int(time.time() * 1000)}"
    lines: list[bytes] = []
    for name, value in fields.items():
        lines.extend([f"--{boundary}".encode(), f'Content-Disposition: form-data; name="{name}"'.encode(), b"", value.encode("utf-8")])
    for name, (filename, mime, data) in files.items():
        lines.extend([f"--{boundary}".encode(), f'Content-Disposition: form-data; name="{name}"; filename="{filename}"'.encode(), f"Content-Type: {mime}".encode(), b"", data])
    lines.extend([f"--{boundary}--".encode(), b""])
    return b"\r\n".join(lines), boundary


def describe_ideogram(image_path: Path, settings: dict[str, Any], args: argparse.Namespace) -> str:
    api_key = setting_or_env(settings, "IdeogramApiKey", "IDEOGRAM_API_KEY")
    if not api_key:
        raise RuntimeError("Missing IdeogramApiKey or IDEOGRAM_API_KEY.")
    mime = mimetypes.guess_type(image_path.name)[0] or "image/png"
    fields = {"describe_model_version": args.ideogram_describe_model} if args.ideogram_describe_model else {}
    body, boundary = encode_multipart(fields, {"image_file": (image_path.name, mime, image_path.read_bytes())})
    req = urllib.request.Request(
        "https://api.ideogram.ai/describe",
        data=body,
        headers={"Api-Key": api_key, "Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=args.timeout) as res:
            response = json.loads(res.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        raise RuntimeError(f"HTTP {ex.code} from Ideogram describe: {ex.read().decode('utf-8', errors='replace')}") from ex
    descriptions = response.get("descriptions") or []
    return "\n".join(d.get("text", "") for d in descriptions).strip() or json.dumps(response, ensure_ascii=False)


DESCRIBERS: dict[str, Callable[[Path, dict[str, Any], argparse.Namespace], str]] = {
    "openai": describe_openai,
    "grok": describe_grok,
    "gemini": describe_gemini,
    "claude": describe_claude,
    "ideogram": describe_ideogram,
}


def has_any(text: str, terms: list[str]) -> bool:
    lower = text.lower()
    return any(re.search(rf"\b{re.escape(term.lower())}\b", lower) for term in terms)


def age_score(expected_age: str, text: str) -> float:
    if has_any(text, [expected_age]):
        return 1.0
    if expected_age == "18":
        return 1.0 if has_any(text, ["teen", "teenager", "late teens", "young adult", "college-aged"]) else 0.0
    if expected_age == "28":
        return 1.0 if has_any(text, ["20s", "twenties", "late 20s", "young adult", "young men", "young women"]) else 0.0
    if expected_age == "42":
        return 1.0 if has_any(text, ["40s", "forties", "30s or 40s", "thirties or forties", "middle-aged", "middle aged"]) else 0.0
    return 0.0


def ethnicity_score(case: Case, text: str) -> float:
    ethnicity_terms = next(terms for key, _, terms in ETHNICITIES if key == case.ethnicity_key)
    if has_any(text, ethnicity_terms):
        return 1.0

    broad_terms = {
        "south_korean": ["asian", "east asian"],
        "japanese": ["asian", "east asian"],
        "chinese": ["asian", "east asian"],
        "english": ["white", "european", "western"],
        "egyptian": ["middle eastern", "north african", "arab"],
        "african": ["black", "african descent"],
        "russian": ["white", "european", "slavic"],
        "polish": ["white", "european", "slavic"],
    }
    return 0.5 if has_any(text, broad_terms.get(case.ethnicity_key, [])) else 0.0


def clothing_score(text: str) -> float:
    return 1.0 if has_any(text, [
        "wearing", "shirt", "t-shirt", "tee", "top", "tank", "blouse", "jacket", "hoodie",
        "dress", "skirt", "shorts", "pants", "trousers", "jeans", "leggings", "shoes",
        "sneakers", "sandals", "outfit", "clothing", "clothes",
    ]) else 0.0


def style_body_score(text: str) -> float:
    return 1.0 if has_any(text, [
        "style", "look", "aesthetic", "sporty", "athletic", "casual", "relaxed", "natural",
        "polished", "tailored", "bodycon", "slim", "fit", "toned", "muscular", "build",
        "physique", "fashion", "streetwear",
    ]) else 0.0


def score_description_rules(case: Case, text: str) -> dict[str, Any]:
    checks = {
        "people_count": 1.0 if has_any(text, ["two", "2", "pair", "both"]) else 0.0,
        "gender": 1.0 if has_any(text, [case.gender, case.gender_plural, "male" if case.gender == "man" else "female"]) else 0.0,
        "age": age_score(case.age, text),
        "ethnicity": ethnicity_score(case, text),
        "clothing": clothing_score(text),
        "style_body": style_body_score(text),
    }
    return {**checks, "score": sum(checks.values()), "possible": float(len(checks)), "judge": "rules"}


def score_description(case: Case, text: str, settings: dict[str, Any] | None = None, args: argparse.Namespace | None = None) -> dict[str, Any]:
    if args is not None and getattr(args, "score_mode", "llm") == "llm":
        if settings is None:
            raise RuntimeError("LLM scoring requires settings.")
        return score_description_with_llm(case, text, settings, args)
    return score_description_rules(case, text)


def describe_images(cases: list[Case], settings: dict[str, Any], args: argparse.Namespace, images_dir: Path, run_dir: Path) -> None:
    endpoints = [name.strip() for name in args.endpoints.split(",") if name.strip()]
    unknown = sorted(set(endpoints) - set(DESCRIBERS))
    if unknown:
        raise ValueError(f"Unknown endpoints: {', '.join(unknown)}")
    selected = cases[: args.limit] if args.limit else cases
    results_path = run_dir / "describe_results.jsonl"
    summary_path = run_dir / "describe_summary.csv"
    rows: list[dict[str, Any]] = []
    with results_path.open("a", encoding="utf-8") as results:
        for case in selected:
            image_path = images_dir / f"{case.case_id}.png"
            if not image_path.exists():
                print(f"[describe] missing image for {case.case_id}; expected {image_path}")
                continue
            for endpoint in endpoints:
                print(f"[describe] {endpoint}: {case.case_id}", flush=True)
                try:
                    text = DESCRIBERS[endpoint](image_path, settings, args)
                    score = score_description(case, text, settings, args)
                    record = {"case": asdict(case), "endpoint": endpoint, "text": text, "score": score, "error": ""}
                except Exception as ex:
                    record = {"case": asdict(case), "endpoint": endpoint, "text": "", "score": {}, "error": str(ex)}
                results.write(json.dumps(record, ensure_ascii=False) + "\n")
                results.flush()
                rows.append(flatten_result(record))
                time.sleep(args.sleep)
    write_summary(summary_path, rows)


def flatten_result(record: dict[str, Any]) -> dict[str, Any]:
    case = record["case"]
    score = record.get("score") or {}
    categories = score.get("categories") if isinstance(score.get("categories"), dict) else {}
    extracted = {
        f"{category}_extracted": str((categories.get(category) or {}).get("extracted", "") or "")
        for category in ScoreCategories
    }
    reasons = {
        f"{category}_reason": str((categories.get(category) or {}).get("reason", "") or "")
        for category in ScoreCategories
    }
    return {
        "endpoint": record["endpoint"],
        "case_id": case["case_id"],
        "ethnicity": case["ethnicity_key"],
        "gender": case["gender"],
        "age": case["age"],
        "judge": score.get("judge", ""),
        "score": score.get("score", 0),
        "possible": score.get("possible", 0),
        "people_count_score": score.get("people_count", 0),
        "gender_score": score.get("gender", 0),
        "age_score": score.get("age", 0),
        "ethnicity_score": score.get("ethnicity", 0),
        "clothing_score": score.get("clothing", 0),
        "style_body_score": score.get("style_body", 0),
        **extracted,
        **reasons,
        "error": record.get("error", ""),
    }


def write_summary(path: Path, rows: list[dict[str, Any]]) -> None:
    fieldnames = [
        "endpoint",
        "case_id",
        "ethnicity",
        "gender",
        "age",
        "judge",
        "score",
        "possible",
        "people_count_score",
        "gender_score",
        "age_score",
        "ethnicity_score",
        "clothing_score",
        "style_body_score",
        "people_count_extracted",
        "gender_extracted",
        "age_extracted",
        "ethnicity_extracted",
        "clothing_extracted",
        "style_body_extracted",
        "people_count_reason",
        "gender_reason",
        "age_reason",
        "ethnicity_reason",
        "clothing_reason",
        "style_body_reason",
        "error",
    ]
    with path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate portrait fixtures and evaluate describe endpoints.")
    parser.add_argument("--settings", default="MultiImageClient/settings.json", help="Path to repo settings.json.")
    parser.add_argument("--out", default="saves/describe-eval/portrait-fixtures", help="Output folder for manifest, images, and results.")
    parser.add_argument("--limit", type=int, default=0, help="Only process the first N cases. Default: all 96.")
    parser.add_argument("--generate", action="store_true", help="Generate portrait images.")
    parser.add_argument("--describe", action="store_true", help="Describe existing/generated images and score omissions.")
    parser.add_argument("--generator", choices=sorted(GENERATORS), default="openai", help="Image generator to use with --generate.")
    parser.add_argument("--endpoints", default="openai,grok,gemini,claude,ideogram", help="Comma-separated describers.")
    parser.add_argument("--describe-prompt", default=DEFAULT_DESCRIBE_PROMPT, help="Generic prompt sent to describers.")
    parser.add_argument("--overwrite", action="store_true", help="Overwrite existing generated images.")
    parser.add_argument("--sleep", type=float, default=0.5, help="Delay between paid API calls.")
    parser.add_argument("--timeout", type=int, default=240, help="HTTP timeout in seconds.")
    parser.add_argument("--max-tokens", type=int, default=1200, help="Max output tokens for describers.")
    parser.add_argument("--score-mode", choices=["llm", "rules"], default="llm", help="Use an LLM text judge or the old keyword rules scorer.")
    parser.add_argument("--score-judge-model", default="gpt-4.1-mini", help="Cheap text model used to score describer responses.")
    parser.add_argument("--score-max-tokens", type=int, default=900, help="Max output tokens for the LLM scoring JSON.")
    parser.add_argument("--detail", default="high", help="Vision detail level for OpenAI/xAI.")
    parser.add_argument("--size", default="1024x1024", help="OpenAI image size.")
    parser.add_argument("--quality", default="low", help="OpenAI image quality.")
    parser.add_argument("--openai-moderation", default="low", help="OpenAI image moderation setting; blank to omit.")
    parser.add_argument("--openai-image-model", default="gpt-image-2", help="OpenAI image generation model.")
    parser.add_argument("--openai-vision-model", default="gpt-4.1", help="OpenAI vision describe model.")
    parser.add_argument("--grok-image-model", default="grok-imagine-image", help="xAI image generation model.")
    parser.add_argument("--grok-resolution", default="1k", help="xAI image resolution.")
    parser.add_argument("--grok-vision-model", default="grok-4.3", help="xAI vision describe model.")
    parser.add_argument("--gemini-vision-model", default="gemini-2.5-pro", help="Gemini vision describe model.")
    parser.add_argument("--claude-vision-model", default="claude-sonnet-4-5", help="Claude vision describe model.")
    parser.add_argument("--ideogram-describe-model", default="", help="Optional Ideogram describe_model_version.")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    settings = load_settings(Path(args.settings))
    run_dir = Path(args.out)
    images_dir = run_dir / "images"
    cases = build_cases()
    manifest = write_manifest(cases, run_dir)

    print(f"cases: {len(cases)}")
    print(f"manifest: {manifest}")
    print(f"images: {images_dir}")

    if not args.generate and not args.describe:
        print("No paid API calls made. Add --generate and/or --describe to run the harness.")
        return 0
    if args.generate:
        generate_images(cases, settings, args, images_dir)
    if args.describe:
        describe_images(cases, settings, args, images_dir, run_dir)
    print("done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
