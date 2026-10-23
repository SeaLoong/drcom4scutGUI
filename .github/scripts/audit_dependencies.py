"""Make NuGet's machine-readable advisory report a failing CI check."""
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def verify(report):
    if not isinstance(report, dict) or report.get("version") != 1:
        raise ValueError("Unsupported NuGet vulnerability report")
    if report.get("problems") or report.get("errors"):
        raise ValueError("NuGet vulnerability lookup did not finish successfully")
    projects = report.get("projects")
    if not isinstance(projects, list) or not projects:
        raise ValueError("NuGet vulnerability report contains no projects")
    vulnerabilities = []
    for project in projects:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str):
            raise ValueError("Invalid NuGet project report")
        if project.get("problems") or project.get("errors"):
            raise ValueError("A NuGet project could not be audited")
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for advisory in package.get("vulnerabilities", []):
                        vulnerabilities.append((package.get("id"), advisory.get("severity"), advisory.get("advisoryurl")))
    if vulnerabilities:
        details = "\n".join(f"{name}: {severity} {url}" for name, severity, url in vulnerabilities)
        raise ValueError("NuGet dependencies have known vulnerabilities:\n" + details)
    return len(projects)


if __name__ == "__main__":
    result = subprocess.run([
        "dotnet", "list", "drcom4scutGUI.sln", "package", "--vulnerable", "--include-transitive",
        "--format", "json", "--output-version", "1", "--no-restore",
    ], cwd=ROOT, check=True, capture_output=True, text=True, timeout=180)
    try:
        count = verify(json.loads(result.stdout))
    except (ValueError, TypeError, KeyError) as error:
        raise SystemExit(str(error)) from error
    print(f"Checked NuGet advisories for {count} projects, including transitive dependencies.")
