#!/usr/bin/env python3
"""Generates the VIN fixture set used by the Step 3.1 acceptance test (ADR-0039).

Everything here is invented: the maker "Acme", its VIN prefix (ACM), model, variant and theme codes. No customer's names, models or VIN prefixes appear (ADR-0003).
`ev` and `ice` are just two example theme codes a tenant might define.

Writes three files next to the integration tests (deterministic: a fixed seed, no clock, no environment):
  vin-fixtures.csv   200 VINs with the model, variant, model year, theme and document each must resolve to
  vin-rules.csv      the VIN rules, in the import format of POST /knowledge/vin-rules/import
  vin-documents.csv  the knowledge documents to create (one per expected document key)

The VINs are 17 characters without I, O or Q. Position 10 is the model-year code, so the expected year is what the rules read from the VIN; the codes used
(N P R S T = 2022..2026) decode to those years whenever the clock reads a year between 2025 and 2050, which is the life of the fixture.

Usage: python3 tools/fixtures/make-vin-fixtures.py
"""
import csv
import random
from pathlib import Path

SEED = 20261020
COUNT = 200
OUT = Path(__file__).resolve().parents[2] / "tests" / "Dam.IntegrationTests" / "Fixtures"

YEAR_CODES = {2022: "N", 2023: "P", 2024: "R", 2025: "S", 2026: "T"}
ALPHA = "ABCDEFGHJKLMNPRSTUVWXYZ"   # no I, O, Q
DIGITS = "0123456789"

# model code, VIN model letters, theme, [(variant code, [variant digits], rule pattern suffix after "ACM<MM>")]
MODELS = [
    ("roadster", "RD", "ice", [("base", "1", "1*"), ("gt", "2", "2*")]),
    ("voyager", "VY", "ice", [("lx", "1", "1*"), ("xl", "2", "2*")]),
    ("spark", "SP", "ev", [("std", "1", "1??*"), ("max", "2", "2??*")]),
    ("terra", "TR", "ev", [("s", "13", "[13]*"), ("x", "24", "[24]*")]),
]
YEARS = [2022, 2023, 2024, 2025, 2026]
# roadster GT has a manual per range of model years; every other variant has one manual for all years.
YEAR_SPLIT = {("roadster", "gt"): [(2022, 2024), (2025, 2026)]}
FLEET = ("spark", "SP", "9", "fleet", 2025)   # a fixed-year rule: variant digit 9 on a spark is the fleet edition, always model year 2025


def vin_for(rng, wmi, mm, variant_digit, year_code):
    filler = "".join(rng.choice(ALPHA) for _ in range(2))
    check = rng.choice(DIGITS)
    plant = rng.choice("ABCDEFGH")
    serial = "".join(rng.choice(DIGITS) for _ in range(6))
    vin = f"{wmi}{mm}{variant_digit}{filler}{check}{year_code}{plant}{serial}"
    assert len(vin) == 17 and not set("IOQ") & set(vin), vin
    return vin


def document_key(model, variant, year):
    if variant is None:
        return f"{model}-all"
    for lo, hi in YEAR_SPLIT.get((model, variant), []):
        if lo <= year <= hi:
            return f"{model}-{variant}-{lo}-{hi}"
    return f"{model}-{variant}"


def main():
    rng = random.Random(SEED)
    rows = []
    seen = set()

    # (model, theme, variant code or None for the model-wide fallback, variant digit, fixed year or None)
    profiles = []
    for model, mm, theme, variants in MODELS:
        for variant, digits, _ in variants:
            for d in digits:
                profiles.append((model, mm, theme, variant, d, None))
        profiles.append((model, mm, theme, None, "8", None))        # no variant rule matches: the models fallback rule
    profiles.append((FLEET[0], FLEET[1], "ev", FLEET[3], FLEET[2], FLEET[4]))

    i = 0
    while len(rows) < COUNT:
        model, mm, theme, variant, digit, fixed_year = profiles[i % len(profiles)]
        year = fixed_year or YEARS[(i // len(profiles) + i) % len(YEARS)]
        code = YEAR_CODES[year] if not fixed_year else rng.choice(list(YEAR_CODES.values()))   # a fixed-year rule ignores the VIN's code
        vin = vin_for(rng, "ACM", mm, digit, code)
        i += 1
        if vin in seen:
            continue
        seen.add(vin)
        doc = f"{model}-{variant}" if variant == "fleet" else document_key(model, variant, year)
        rows.append((vin, model, variant or "", year, theme, doc))

    OUT.mkdir(parents=True, exist_ok=True)
    with open(OUT / "vin-fixtures.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["vin", "expected_model", "expected_variant", "expected_year", "expected_theme", "expected_document"])
        w.writerows(rows)

    rules = []
    for model, mm, theme, variants in MODELS:
        for variant, _, suffix in variants:
            rules.append((f"{model} {variant}", f"ACM{mm}{suffix}", model, variant, "vin", theme, 10, "true"))
        rules.append((f"{model} (any variant)", f"ACM{mm}*", model, "", "vin", theme, 0, "true"))
    rules.append(("spark fleet", f"ACM{FLEET[1]}{FLEET[2]}*", FLEET[0], FLEET[3], str(FLEET[4]), "ev", 20, "true"))
    rules.append(("retired roadster rule", "ACMRD*", "roadster", "base", "vin", "ice", 99, "false"))   # switched off: must never win
    with open(OUT / "vin-rules.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["name", "pattern", "model", "variant", "year", "theme", "priority", "enabled"])
        w.writerows(rules)

    docs = {}
    for _, model, variant, year, _, doc in rows:
        if doc in docs:
            continue
        if variant == "":
            docs[doc] = (model, "", "", "")
        elif variant == "fleet":
            docs[doc] = (model, variant, "", "")
        else:
            split = [r for r in YEAR_SPLIT.get((model, variant), []) if doc.endswith(f"-{r[0]}-{r[1]}")]
            docs[doc] = (model, variant, split[0][0] if split else "", split[0][1] if split else "")
    with open(OUT / "vin-documents.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["key", "model", "variant", "year_from", "year_to"])
        for key in sorted(docs):
            w.writerow([key, *docs[key]])

    print(f"{len(rows)} VINs, {len(rules)} rules, {len(docs)} documents -> {OUT}")


if __name__ == "__main__":
    main()
