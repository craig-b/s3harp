"""pytest plugin that gates S3Harp on the s3-tests cases it is known to pass.

must-pass.txt lists the cases S3Harp passes today, one node id per line.
Every collected case outside the list is marked xfail(strict=True), so a
listed case failing fails the run, and an unlisted case passing fails the
run until the list grows to include it. The list only moves forward.
"""

from pathlib import Path

import pytest

MUST_PASS = Path(__file__).with_name("must-pass.txt")


@pytest.hookimpl(tryfirst=True)
def pytest_collection_modifyitems(config, items):
    # Runs before -k and -m deselection so the list is checked against the
    # whole suite and a narrowed local run still works.
    expected = {line.strip() for line in MUST_PASS.read_text().splitlines() if line.strip()}
    collected = {item.nodeid for item in items}
    unknown = sorted(expected - collected)
    if unknown:
        raise pytest.UsageError(
            f"{MUST_PASS} lists cases that were not collected:\n  " + "\n  ".join(unknown)
        )

    for item in items:
        if item.nodeid not in expected:
            item.add_marker(
                pytest.mark.xfail(strict=True, reason=f"not listed in {MUST_PASS.name}")
            )
