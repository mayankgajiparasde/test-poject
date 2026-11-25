#!/usr/bin/env bash
# Fails when the CLI no longer reports the known levered IRRs and reconciliation count. Writes to a temporary folder, not output/.
set -euo pipefail
cd "$(dirname "$0")/.."
out=$(mktemp -d)
report=$(dotnet run --project src/HotelUnderwriting.Cli -c Release -f net10.0 -p:NuGetAudit=false -- data/Hotel_Acquisition_Underwriting_Model.xlsx "$out")
echo "$report"
for want in '^Base +levered IRR 18\.3860% ' '^Upside +levered IRR 32\.3000% ' '^Downside +levered IRR -3\.8510% ' '^Reconciliation: 37/66 pass'; do
  grep -qE "$want" <<<"$report" || { echo "CLI figures changed: no line matches '$want'" >&2; exit 1; }
done
echo "CLI figures unchanged."
