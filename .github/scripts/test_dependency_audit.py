"""A zero CLI exit status must not hide NuGet advisories or an empty report."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("dependency_audit", Path(__file__).with_name("audit_dependencies.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class DependencyAuditTests(unittest.TestCase):
    def test_clean_projects_are_counted(self):
        self.assertEqual(audit.verify({"version": 1, "projects": [{"path": "app.csproj"}]}), 1)

    def test_direct_and_transitive_advisories_fail_the_check(self):
        for kind in ("topLevelPackages", "transitivePackages"):
            report = {"version": 1, "projects": [{"path": "app.csproj", "frameworks": [{
                kind: [{"id": "Example.Dependency", "vulnerabilities": [{
                    "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-example"
                }]}]
            }]}]}
            with self.subTest(kind=kind), self.assertRaisesRegex(ValueError, "Example.Dependency: High"):
                audit.verify(report)

    def test_missing_projects_unknown_schema_and_query_failures_cannot_pass(self):
        for report in ({}, {"version": 1, "projects": []},
                       {"version": 1, "projects": [{"path": "app.csproj"}], "problems": ["lookup failed"]},
                       {"version": 1, "projects": [{"path": "app.csproj", "errors": ["lookup failed"]}]}):
            with self.subTest(report=report), self.assertRaises(ValueError):
                audit.verify(report)


if __name__ == "__main__":
    unittest.main()
