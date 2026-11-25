#!/usr/bin/env bash
# Fails when a direct or transitive NuGet package has a known vulnerability. Argument: solution or project (default: the solution).
set -euo pipefail
cd "$(dirname "$0")/.."
target=${1:-HotelUnderwriting.slnx}
# the audit warnings of restore are reported below instead
dotnet restore "$target" -p:NuGetAudit=false
report=$(dotnet list "$target" package --vulnerable --include-transitive --format json) || { echo "$report"; exit 1; }
echo "$report"
grep -q '"projects"' <<<"$report" || { echo "The report has no projects: the output shape changed, or nothing was checked." >&2; exit 1; }
if grep -qE '"vulnerabilities"|"level": *"error"' <<<"$report"; then
  echo "Vulnerable packages (or the check failed): see the report above." >&2
  exit 1
fi
echo "No known vulnerable packages."
