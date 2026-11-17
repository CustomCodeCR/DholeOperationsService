import os
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ensure_operations_database.sh"

class BootstrapTests(unittest.TestCase):
    def execute(self, project, initial_exists, db="dhole_operations"):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder)
            (path / "env").write_text("POSTGRES_USER=postgres\nOPERATIONS_DB=" + db + "\n")
            (path / "exists").write_text("1" if initial_exists else "0")
            fake_docker = """#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$MOCK_LOG"
if [[ "$1" == "ps" ]]; then echo staging-pg-id; exit 0; fi
if [[ "$1" != "exec" ]]; then exit 1; fi
if [[ "$3" == "pg_isready" ]]; then exit 0; fi
if [[ "$3" == "createdb" ]]; then echo 1 > "$MOCK_EXISTS"; exit 0; fi
if [[ "$3" == "psql" ]]; then
  if [[ "$*" == *"FROM pg_database"* ]]; then
    if [[ "$(cat "$MOCK_EXISTS")" == "1" ]]; then echo 1; fi
  else
    echo 1
  fi
  exit 0
fi
exit 1
"""
            (path / "docker").write_text(fake_docker)
            (path / "docker").chmod(0o755)
            env = dict(os.environ, DHOLE_ENV_FILE=str(path / "env"), COMPOSE_PROJECT=project,
                       MOCK_LOG=str(path / "log"), MOCK_EXISTS=str(path / "exists"),
                       PATH=str(path) + os.pathsep + os.environ["PATH"])
            result = subprocess.run(["bash", str(SCRIPT)], env=env, capture_output=True, text=True)
            log = (path / "log").read_text() if (path / "log").exists() else ""
            return result, log

    def test_missing_database_is_created(self):
        result, log = self.execute("dhole-staging", False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("createdb -U postgres dhole_operations", log)
        self.assertIn("staging-pg-id", log)

    def test_existing_production_database_is_preserved(self):
        result, log = self.execute("dhole", True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("createdb", log)
        self.assertIn("exec postgres pg_isready", log)

    def test_rejects_unknown_environment(self):
        result, log = self.execute("not-production", True)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("createdb", log)

    def test_rejects_database_injection(self):
        result, log = self.execute("dhole", False, "dhole_operations;DROP")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(log, "")

if __name__ == "__main__":
    unittest.main()
