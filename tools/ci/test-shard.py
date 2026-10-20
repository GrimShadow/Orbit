#!/usr/bin/env python3
"""Splits the integration test classes into balanced shards for CI.

All integration test classes share one xUnit collection (one Postgres), so they run serially; to cut wall-clock time CI runs
several jobs, each with a slice of the classes. The slice is computed here from the real list of tests, so a new test class is
never left out: classes without a recorded weight get DEFAULT_WEIGHT.

usage: dotnet test <project> --no-build --list-tests | test-shard.py <shard (1-based)> <count>  ->  prints a `dotnet test --filter` expression
"""
import json
import pathlib
import sys

DEFAULT_WEIGHT = 25  # seconds, for a class that is not in class-weights.json yet


def classes(listing: str) -> list[str]:
    found: set[str] = set()
    for line in listing.splitlines():
        if not line.startswith("    "):
            continue
        name = line.strip().split("(")[0]  # theory arguments may contain dots
        if "." in name:
            found.add(name.rsplit(".", 1)[0])
    return sorted(found)


def shard(classes_: list[str], weights: dict[str, int], index: int, count: int) -> list[str]:
    bins: list[tuple[int, list[str]]] = [(0, []) for _ in range(count)]
    for c in sorted(classes_, key=lambda c: (-weights.get(c.rsplit(".", 1)[-1], DEFAULT_WEIGHT), c)):
        i = min(range(count), key=lambda b: (bins[b][0], b))
        bins[i] = (bins[i][0] + weights.get(c.rsplit(".", 1)[-1], DEFAULT_WEIGHT), bins[i][1] + [c])
    return sorted(bins[index - 1][1])


def main() -> int:
    index, count = int(sys.argv[1]), int(sys.argv[2])
    if not 1 <= index <= count:
        print("shard index out of range", file=sys.stderr)
        return 2
    weights = json.loads((pathlib.Path(__file__).with_name("class-weights.json")).read_text())
    mine = shard(classes(sys.stdin.read()), weights, index, count)
    if not mine:
        print("no test classes for this shard", file=sys.stderr)
        return 3
    print("|".join(f"FullyQualifiedName~{c}." for c in mine))
    return 0


if __name__ == "__main__":
    sys.exit(main())
