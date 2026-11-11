#!/usr/bin/env python3
"""Byte-level audit of a native CAD report and its bounded projection.

Only the standard library is used. Profile offsets locate top-level values;
array elements are compared as their original UTF-8 JSON bytes. The source
text, rather than the profile's anchor list, determines required and permitted
geometry. A successful audit is not a CAD accuracy or image-fidelity claim.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
from typing import Iterator


MAX_RECORD_BYTES = 16 * 1024 * 1024
MAX_PROFILE_BYTES = 64 * 1024 * 1024
MAX_GAP_BYTES = 1024 * 1024
MAX_ANCHOR_UTF8_BYTES = 64 * 1024 * 1024
MAX_ANCHORS = 1_000_000
INITIAL_READ_CHARS = 64 * 1024
MAX_READ_CHARS = 4 * 1024 * 1024
JSON_SPACE = re.compile(r"[ \t\r\n]*")
HEX_SHA256 = re.compile(r"[0-9a-fA-F]{64}\Z")

# ECMAScript trim; the conservative set also includes .NET's U+0085.
JS_TRIM = (" \t\n\r\v\f\u00a0\u1680" +
           "".join(chr(n) for n in range(0x2000, 0x200B)) +
           "\u2028\u2029\u202f\u205f\u3000\ufeff")
CONSERVATIVE_TRIM = JS_TRIM + "\u0085"


class AuditError(ValueError):
    pass


def reject_constant(value: str):
    raise AuditError("invalid_json_constant: " + value)


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise AuditError("duplicate_json_key_in_metadata_or_profile: " + key)
        result[key] = value
    return result


def strict_json(value: bytes):
    return json.loads(value, object_pairs_hook=unique_pairs, parse_constant=reject_constant)


def sha256_file(path: Path) -> str:
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest().upper()


def canonical_path(value: str | Path) -> str:
    return os.path.normcase(os.path.abspath(os.path.normpath(os.fspath(value))))


def require_int(value, name: str, minimum: int = 0) -> int:
    if type(value) is not int or value < minimum:
        raise AuditError(name + "_must_be_nonnegative_integer")
    return value


def require_string(value, name: str) -> str:
    if not isinstance(value, str):
        raise AuditError(name + "_must_be_string")
    # JsonDocument.GetString() rejects unpaired UTF-16 surrogates. This also
    # avoids silently changing the semantics of strings used as anchor keys.
    try:
        value.encode("utf-8", "strict")
    except UnicodeEncodeError as exc:
        raise AuditError(name + "_contains_unpaired_surrogate") from exc
    return value


def require_sha(value, name: str) -> str:
    value = require_string(value, name)
    if not HEX_SHA256.fullmatch(value):
        raise AuditError(name + "_must_be_sha256")
    return value.upper()


def read_profile(path: Path) -> tuple[dict, str]:
    with path.open("rb") as f:
        raw = f.read(MAX_PROFILE_BYTES + 1)
    if len(raw) > MAX_PROFILE_BYTES:
        raise AuditError("profile_exceeds_64MiB")
    value = strict_json(raw)
    if type(value) is not dict:
        raise AuditError("profile_must_be_object")
    return value, hashlib.sha256(raw).hexdigest().upper()


def validate_profile(profile: dict, path: Path, digest: str) -> dict:
    if profile.get("status") != "passed":
        raise AuditError("profile_status_not_passed")
    reported = require_string(profile.get("source"), "profile_source")
    if not os.path.isabs(reported) or canonical_path(reported) != canonical_path(path):
        raise AuditError("profile_source_path_mismatch")
    if require_sha(profile.get("sourceHash"), "profile_sourceHash") != digest:
        raise AuditError("profile_source_hash_mismatch")
    size = path.stat().st_size
    if require_int(profile.get("sourceBytes"), "profile_sourceBytes") != size:
        raise AuditError("profile_source_size_mismatch")
    fields = profile.get("fields")
    if type(fields) is not dict or not fields:
        raise AuditError("profile_fields_missing")

    # Check that offsets describe the named root properties in order. This
    # avoids trusting a profile that simply points 'texts' at another array.
    ordered = []
    for name, stat in fields.items():
        if type(name) is not str or type(stat) is not dict:
            raise AuditError("invalid_profile_field")
        start = require_int(stat.get("start"), name + "_start")
        end = require_int(stat.get("end"), name + "_end")
        length = require_int(stat.get("bytes"), name + "_bytes")
        if start >= end or end > size or end - start != length:
            raise AuditError("invalid_field_offsets: " + name)
        ordered.append((start, end, name, stat))
    ordered.sort(key=lambda entry: entry[0])
    decoder = json.JSONDecoder(parse_constant=reject_constant)
    with path.open("rb") as f:
        previous = 0
        for index, (start, end, name, _) in enumerate(ordered):
            gap = start - previous
            if gap < 0 or gap > MAX_GAP_BYTES:
                raise AuditError("invalid_root_property_gap: " + name)
            f.seek(previous)
            between = f.read(gap).decode("utf-8", "strict")
            between = between.lstrip(" \t\r\n")
            marker = "{" if index == 0 else ","
            if not between.startswith(marker):
                raise AuditError("root_property_separator_mismatch: " + name)
            between = between[1:].lstrip(" \t\r\n")
            try:
                actual_name, pos = decoder.raw_decode(between)
            except json.JSONDecodeError as exc:
                raise AuditError("root_property_name_invalid: " + name) from exc
            if type(actual_name) is not str or actual_name != name:
                raise AuditError("root_property_name_mismatch: " + name)
            tail = between[pos:].lstrip(" \t\r\n")
            if not tail.startswith(":") or tail[1:].strip(" \t\r\n"):
                raise AuditError("root_property_colon_mismatch: " + name)
            previous = end
        if size - previous > MAX_GAP_BYTES:
            raise AuditError("root_tail_too_large")
        f.seek(previous)
        tail = f.read(size - previous).decode("utf-8", "strict").strip(" \t\r\n")
        if tail != "}":
            raise AuditError("root_tail_invalid_or_unprofiled_property")
    return fields


def required_fields(fields: dict, names: tuple[str, ...]):
    for name in names:
        if name not in fields:
            raise AuditError("missing_required_field: " + name)


def value_bytes(path: Path, stat: dict, name: str) -> bytes:
    size = require_int(stat.get("bytes"), name + "_bytes")
    if size > MAX_RECORD_BYTES:
        raise AuditError("metadata_value_exceeds_16MiB: " + name)
    with path.open("rb") as f:
        f.seek(stat["start"])
        value = f.read(size)
    if len(value) != size:
        raise AuditError("metadata_value_truncated: " + name)
    return value


class ArrayElements:
    """Read one profiled array; materialize at most one bounded JSON object.

    raw_decode performs the structural scan in C. Incomplete objects grow the
    buffer geometrically, with a capped read chunk, avoiding quadratic retry
    on long strings. The returned bytes exclude array separators/whitespace.
    """

    def __init__(self, path: Path, stat: dict, name: str):
        if stat.get("type") != "StartArray":
            raise AuditError("field_not_profiled_as_array: " + name)
        self.path, self.stat, self.name = path, stat, name
        self.count = 0

    def __iter__(self) -> Iterator[tuple[dict, bytes]]:
        decoder = json.JSONDecoder(parse_constant=reject_constant)
        with self.path.open("rb") as raw:
            raw.seek(self.stat["start"])
            with io.TextIOWrapper(raw, encoding="utf-8", errors="strict", newline="") as reader:
                data, pos, base, eof = "", 0, self.stat["start"], False

                def compact():
                    nonlocal data, pos, base
                    if pos:
                        base += len(data[:pos].encode("utf-8"))
                        data, pos = data[pos:], 0

                def fill():
                    nonlocal data, eof
                    compact()
                    # Geometric growth is linear in the length of an unfinished
                    # record; no per-byte Python scan and no 64 KiB reparse loop.
                    amount = min(MAX_READ_CHARS, max(INITIAL_READ_CHARS, len(data)))
                    chunk = reader.read(amount)
                    if chunk:
                        data += chunk
                    else:
                        eof = True

                def space():
                    nonlocal pos
                    while True:
                        pos = JSON_SPACE.match(data, pos).end()
                        if pos < len(data) or eof:
                            return
                        fill()

                fill()
                space()
                if pos >= len(data) or data[pos] != "[":
                    raise AuditError("invalid_array_start: " + self.name)
                pos += 1
                first = True
                while True:
                    space()
                    if pos >= len(data):
                        raise AuditError("incomplete_array: " + self.name)
                    if data[pos] == "]":
                        pos += 1
                        end_offset = base + len(data[:pos].encode("utf-8"))
                        if end_offset != self.stat["end"]:
                            raise AuditError("array_end_offset_mismatch: " + self.name)
                        expected = require_int(self.stat.get("items"), self.name + "_items")
                        if self.count != expected:
                            raise AuditError("array_profile_count_mismatch: " + self.name)
                        return
                    if not first:
                        if data[pos] != ",":
                            raise AuditError("array_comma_missing: " + self.name)
                        pos += 1
                        space()
                        if pos >= len(data) or data[pos] == "]":
                            raise AuditError("array_trailing_comma_or_incomplete: " + self.name)
                    start = pos
                    while True:
                        try:
                            obj, end = decoder.raw_decode(data, start)
                            break
                        except json.JSONDecodeError as exc:
                            # A successful decode is attempted before checking
                            # the bound, so prefetch of the next record cannot
                            # reject a valid near-limit object.
                            pos = start
                            compact()
                            start = 0
                            if len(data.encode("utf-8")) > MAX_RECORD_BYTES:
                                raise AuditError("record_exceeds_16MiB: " + self.name) from exc
                            if eof:
                                raise AuditError("invalid_or_incomplete_record: " + self.name) from exc
                            fill()
                    if type(obj) is not dict:
                        raise AuditError("array_element_not_object: " + self.name)
                    record = data[start:end].encode("utf-8")
                    if len(record) > MAX_RECORD_BYTES:
                        raise AuditError("record_exceeds_16MiB: " + self.name)
                    pos = end
                    self.count += 1
                    first = False
                    yield obj, record


def anchor_kind(text: str) -> tuple[bool, bool]:
    text = require_string(text, "text")
    js = text.strip(JS_TRIM)
    conservative = text.strip(CONSERVATIVE_TRIM)

    def matches(value: str) -> bool:
        # The length guard avoids allocating an uppercase copy of a large text.
        return value == "PART No." or (len(value) == 6 and value.upper() == "NUMBER")

    return matches(js), matches(conservative)


def check_profile_anchors(profile: dict, actual: set[tuple[str, str]], label: str):
    entries = profile.get("anchors")
    if type(entries) is not list or len(entries) > MAX_ANCHORS:
        raise AuditError(label + "_profile_anchors_invalid")
    declared = set()
    for entry in entries:
        if type(entry) is not dict:
            raise AuditError(label + "_profile_anchor_not_object")
        item = (require_string(entry.get("layout"), "anchor_layout"),
                require_string(entry.get("blockPath"), "anchor_blockPath"))
        if item in declared:
            raise AuditError(label + "_profile_anchor_duplicate")
        declared.add(item)
    if declared != actual:
        raise AuditError(label + "_profile_anchors_mismatch")


def lookup(anchors: set[tuple[str, str]]) -> dict[str, set[str]]:
    result: dict[str, set[str]] = {}
    for layout, path in anchors:
        result.setdefault(layout, set()).add(path)
    return result


CROSS_MARK_RULE = "open_2pt_line_or_polyline_len_ge_100_diagonal_quarter_v1"


def finite_number(value):
    # bool is an int subclass in Python but never a JSON number.
    if type(value) is int:
        try:
            value = float(value)
        except OverflowError:
            return None
    if type(value) is not float or value != value or value in (float("inf"), float("-inf")):
        return None
    return value


def is_cross_mark_candidate(obj: dict) -> bool:
    """Independent restatement of the projector's long diagonal segment rule."""
    if "dimensionGraphics" in obj and obj["dimensionGraphics"] is not False:
        return False
    if "closed" in obj and obj["closed"] is not False:
        return False
    kind = obj.get("kind")
    if type(kind) is not str or (kind != "Line" and "Polyline" not in kind):
        return False
    points = obj.get("points")
    if type(points) is not list or len(points) != 2:
        return False
    xy = []
    for point in points:
        if type(point) is not list or len(point) < 2:
            return False
        values = [finite_number(v) for v in point]
        if any(v is None for v in values):
            return False
        xy.extend(values[:2])
    dx, dy = abs(xy[2] - xy[0]), abs(xy[3] - xy[1])
    return dx * dx + dy * dy >= 10000 and dy >= 0.25 * dx and dx >= 0.25 * dy


def matches_path(index: dict[str, set[str]], layout: str, path: str) -> bool:
    candidates = index.get(layout)
    if not candidates:
        return False
    if path in candidates:
        return True
    offset = path.find("/")
    while offset >= 0:
        if path[:offset] in candidates:
            return True
        offset = path.find("/", offset + 1)
    return False


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "profile", "projected", "projected-profile", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--wait-for-go", action="store_true")
    args = parser.parse_args(argv)
    if not args.wait_for_go or sys.stdin.readline() != "GO\n":
        raise AuditError("invalid_gate")

    source, source_profile_path = Path(args.source), Path(args.profile)
    projected, projected_profile_path = Path(args.projected), Path(args.projected_profile)
    output = Path(args.output)
    if output.exists():
        raise AuditError("output_already_exists")
    if not output.parent.is_dir():
        raise AuditError("output_parent_missing")
    inputs = (source, source_profile_path, projected, projected_profile_path)
    if canonical_path(output) in {canonical_path(p) for p in inputs}:
        raise AuditError("output_cannot_be_input")
    if canonical_path(source) == canonical_path(projected):
        raise AuditError("source_and_projection_must_differ")

    source_profile, source_profile_hash = read_profile(source_profile_path)
    projected_profile, projected_profile_hash = read_profile(projected_profile_path)
    source_hash, projected_hash = sha256_file(source), sha256_file(projected)
    sf = validate_profile(source_profile, source, source_hash)
    pf = validate_profile(projected_profile, projected, projected_hash)
    common = ("sourceFile", "sourceSha256", "success", "summary", "numericSanitization",
              "notifications", "issues", "texts", "geometry")
    required_fields(sf, common)
    required_fields(pf, common + ("projectionMetadata",))

    for name in ("sourceFile", "sourceSha256", "success"):
        left = strict_json(value_bytes(source, sf[name], name))
        right = strict_json(value_bytes(projected, pf[name], name))
        if type(left) is not type(right) or left != right:
            raise AuditError("metadata_mismatch: " + name)
        if name == "success" and left is not True:
            raise AuditError("source_success_not_true")
        if name == "sourceFile":
            require_string(left, name)
        if name == "sourceSha256":
            require_sha(left, name)
        if name in ("sourceFile", "sourceSha256"):
            if source_profile.get(name) != left or projected_profile.get(name) != right:
                raise AuditError("profile_metadata_mismatch: " + name)
    for name in ("summary", "numericSanitization"):
        if value_bytes(source, sf[name], name) != value_bytes(projected, pf[name], name):
            raise AuditError("metadata_bytes_mismatch: " + name)

    metadata = strict_json(value_bytes(projected, pf["projectionMetadata"], "projectionMetadata"))
    if type(metadata) is not dict or metadata.get("kind") != "title-anchor-geometry-subset":
        raise AuditError("projection_metadata_kind_invalid")
    if require_sha(metadata.get("originalReportSha256"), "originalReportSha256") != source_hash:
        raise AuditError("projection_metadata_source_hash_mismatch")
    origin = require_string(metadata.get("originalReportPath"), "originalReportPath")
    if not os.path.isabs(origin) or canonical_path(origin) != canonical_path(source):
        raise AuditError("projection_metadata_source_path_mismatch")
    if require_int(metadata.get("originalReportBytes"), "originalReportBytes") != source.stat().st_size:
        raise AuditError("projection_metadata_source_size_mismatch")
    if metadata.get("allTextAndIssuesRetained") is not True or metadata.get("fullNativeReport") is not False:
        raise AuditError("projection_metadata_flags_invalid")
    if metadata.get("allNotificationsRetained") is not True:
        raise AuditError("projection_metadata_notifications_flag_invalid")

    exact_anchors: set[tuple[str, str]] = set()
    conservative_anchors: set[tuple[str, str]] = set()
    anchor_bytes = 0
    counts = {}
    for name in ("notifications", "issues", "texts"):
        left, right = ArrayElements(source, sf[name], name), ArrayElements(projected, pf[name], name)
        li, ri = iter(left), iter(right)
        while True:
            lvalue = next(li, None)
            rvalue = next(ri, None)
            if lvalue is None and rvalue is None:
                break
            if lvalue is None or rvalue is None or lvalue[1] != rvalue[1]:
                raise AuditError(f"{name}_bytes_mismatch_at_{left.count}")
            if name == "texts":
                obj = lvalue[0]
                exact, conservative = anchor_kind(obj.get("text"))
                if conservative:
                    anchor = (require_string(obj.get("layout"), "text_layout"),
                              require_string(obj.get("blockPath"), "text_blockPath"))
                    if anchor not in conservative_anchors:
                        anchor_bytes += len(anchor[0].encode("utf-8")) + len(anchor[1].encode("utf-8"))
                        if anchor_bytes > MAX_ANCHOR_UTF8_BYTES or len(conservative_anchors) >= MAX_ANCHORS:
                            raise AuditError("anchor_budget_exceeded")
                        conservative_anchors.add(anchor)
                    if exact:
                        exact_anchors.add(anchor)
        counts[name] = left.count

    check_profile_anchors(source_profile, conservative_anchors, "source")
    check_profile_anchors(projected_profile, conservative_anchors, "projected")
    strict_index, allowed_index = lookup(exact_anchors), lookup(conservative_anchors)
    source_geometry = ArrayElements(source, sf["geometry"], "geometry")
    projected_geometry = ArrayElements(projected, pf["geometry"], "geometry")
    projected_iter = iter(projected_geometry)
    pending = next(projected_iter, None)
    required_count = allowed_extra_count = retained_count = cross_mark_count = 0
    for obj, record in source_geometry:
        layout = require_string(obj.get("layout"), "geometry_layout")
        path = require_string(obj.get("blockPath"), "geometry_blockPath")
        cross_mark = is_cross_mark_candidate(obj)
        cross_mark_count += cross_mark
        required = cross_mark or matches_path(strict_index, layout, path)
        allowed = required or matches_path(allowed_index, layout, path)
        if allowed and pending is not None and pending[1] == record:
            retained_count += 1
            if required:
                required_count += 1
            else:
                allowed_extra_count += 1
            pending = next(projected_iter, None)
        elif required:
            raise AuditError(f"required_geometry_bytes_mismatch_at_source_{source_geometry.count}")
    if pending is not None:
        raise AuditError("unexpected_or_changed_projected_geometry")
    if require_int(metadata.get("originalGeometryCount"), "originalGeometryCount") != source_geometry.count:
        raise AuditError("projection_metadata_original_geometry_count_mismatch")
    if require_int(metadata.get("retainedGeometryCount"), "retainedGeometryCount") != retained_count:
        raise AuditError("projection_metadata_retained_geometry_count_mismatch")
    if metadata.get("crossMarkRule") != CROSS_MARK_RULE:
        raise AuditError("projection_metadata_cross_mark_rule_mismatch")
    if require_int(metadata.get("crossMarkCandidateCount"), "crossMarkCandidateCount") != cross_mark_count:
        raise AuditError("projection_metadata_cross_mark_count_mismatch")

    if sha256_file(source) != source_hash or sha256_file(projected) != projected_hash:
        raise AuditError("reports_changed_during_audit")
    if sha256_file(source_profile_path) != source_profile_hash or sha256_file(projected_profile_path) != projected_profile_hash:
        raise AuditError("profiles_changed_during_audit")

    result = {
        "status": "passed",
        "method": "independent profile-offset UTF-8 JSON element byte comparison",
        "source_sha256": source_hash,
        "projection_sha256": projected_hash,
        "source_profile_sha256": source_profile_hash,
        "projection_profile_sha256": projected_profile_hash,
        "source_and_profiles_unchanged_during_audit": True,
        "array_element_bytes_equal": counts,
        "strict_anchor_paths": len(exact_anchors),
        "conservative_anchor_paths": len(conservative_anchors),
        "original_geometry_objects": source_geometry.count,
        "required_geometry_objects_equal": required_count,
        "conservative_extra_geometry_objects_equal": allowed_extra_count,
        "retained_geometry_objects_equal": retained_count,
        "cross_mark_rule": CROSS_MARK_RULE,
        "cross_mark_candidate_objects_equal": cross_mark_count,
        "projection_metadata_verified": True,
        "limits": {"record_utf8_bytes": MAX_RECORD_BYTES, "profile_bytes": MAX_PROFILE_BYTES,
                   "anchor_utf8_bytes": MAX_ANCHOR_UTF8_BYTES, "anchor_paths": MAX_ANCHORS},
        "limitations": ["Report and profile hashes are checked before and after the audit, not continuously.",
                        "No DWG-to-report correctness, frame ownership, or visual fidelity is certified."],
    }
    # Create only after all checks and both sets of hashes succeed.
    with output.open("x", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, ensure_ascii=False, indent=2, allow_nan=False)
        f.write("\n")
    print(json.dumps(result, ensure_ascii=False, allow_nan=False))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError, UnicodeError, RecursionError, OverflowError) as exc:
        print(json.dumps({"status": "failed", "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)
