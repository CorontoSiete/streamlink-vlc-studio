"""Shared validation and reporting for resource measurement tools."""

import json
import math


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    # Windows PowerShell writes a BOM with -Encoding UTF8; PowerShell 7 does not.
    # Accept both while retaining the file's Unicode names and paths.
    return json.loads(path.read_text(encoding="utf-8-sig"),
                      parse_constant=reject_nonfinite, parse_float=parse_finite_float)


def reject_nonfinite(value):
    raise ValueError(f"Non-finite JSON number: {value}")


def parse_finite_float(value):
    number = float(value)
    if not math.isfinite(number):
        reject_nonfinite(value)
    return number


def require_stream_values(row, fields, streams, location):
    for field in fields:
        values = row.get(field)
        require(isinstance(values, list) and len(values) == streams,
                f"Expected {streams} {field} results in {location}.")


def reduction(before, after):
    require(math.isfinite(before) and math.isfinite(after) and before >= 0 and after >= 0,
            "Resource measurements must be finite and non-negative.")
    return 100 * (1 - after / before) if before else None


def format_percent(value):
    return "unavailable" if value is None else f"{value:+.1f}%"
