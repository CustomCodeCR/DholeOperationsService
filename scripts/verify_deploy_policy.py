#!/usr/bin/env python3
"""Assert that Operations deployments cannot silently skip due to flag conditions."""
from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent
for environment, branch in (("staging", "develop"), ("production", "master")):
    path = root / ".github" / "workflows" / f"deploy-{environment}.yml"
    content = path.read_text(encoding="utf-8")
    assert f"branches: [{branch}]" in content, f"{environment}: missing {branch} push trigger"
    assert re.search(r"^  deploy:\s*\n    runs-on:", content, re.MULTILINE), (
        f"{environment}: deployment job must run without a conditional"
    )
    assert not re.search(r"^    if:\s*", content, re.MULTILINE), (
        f"{environment}: deployment job cannot be gated by a flag"
    )
    assert not re.search(r"vars\.DHOLE_OPERATIONS_.*ENABLED", content), (
        f"{environment}: legacy flag gate found"
    )
print("PASS: Operations master/develop deploy jobs have no feature-flag gates")
