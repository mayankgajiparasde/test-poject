# Hotel Acquisition Underwriting Engine (Excel to C#/.NET)

C# engine that reproduces the monthly hotel acquisition model in `Hotel_Acquisition_Underwriting_Model.xlsx`
(Base / Upside / Downside): operating forecast, debt with PIP holdback, sale, unlevered and levered returns, GP/LP waterfall.
The READ ME sheet of the workbook is treated as the specification. Where a model sheet disagrees with it, the engine follows READ ME and the
difference is documented below.

## Run it

Requires the .NET 8 SDK or newer. The projects multi-target `net8.0;net10.0` (.NET 8 left support on 10 Nov 2026; .NET 10 is the LTS); `dotnet test` runs everything: 173 unit tests and 12 seeded property tests on each framework, plus 322 API tests and 44 PostgreSQL tests on net10.0 (the latter need a database, see PostgreSQL mode).

```
dotnet test                                                                             # 173 + 12 property tests x2 frameworks, 322 API, 44 PostgreSQL
dotnet run --project src/HotelUnderwriting.Cli -- data/Hotel_Acquisition_Underwriting_Model.xlsx output
```

The CLI reads the reference workbook (read-only, never saved) and writes to `output/`:
- `HotelUnderwriting_Output.xlsx`: Scenario Summary; per scenario `Proforma - X`, `Monthly - X` (unlevered, debt, levered, pref/tier, LP and GP cash flows), `Returns - X`; plus `Reconciliation` and `Workbook Issues` sheets.
- `reconciliation.md`: the reconciliation and issue-impact tables used in this README.

## Try it in 5 minutes

Everything below runs on your own machine. The only secrets are throwaway values you generate in your own shell; nothing is written to a file.

You need: Git, the repository, and for

- **Docker path (A)**: Docker with Compose v2, `curl`, `openssl` (Git for Windows ships both in Git Bash) and, for the demo script, `bash` plus `jq` or Python.
- **Local path (B)**: the .NET 10 SDK (`dotnet --version` says 10.x), `curl` and `openssl` (or any 32+ character string for the key).

Use Git Bash, WSL or any bash on Windows (the `curl` and demo-script lines are bash). Where PowerShell differs, both are shown. All commands run from the repository root.

### A. Docker Compose (database, API with login, worker)

bash:

```
export HUE_DB_PASSWORD=$(openssl rand -hex 24) HUE_JWT_KEY=$(openssl rand -hex 32)   # throwaway, only in this shell
export HUE_API_PORT=5080                                                             # change it when 5080 is taken
docker compose -p hue-demo up -d --build
until curl -sf http://127.0.0.1:$HUE_API_PORT/health/ready >/dev/null; do sleep 2; done; echo ready
```

PowerShell (Windows PowerShell 5.1 or 7):

```
function New-Hex($n) { $b = New-Object byte[] $n; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') }) }
$env:HUE_DB_PASSWORD = New-Hex 24; $env:HUE_JWT_KEY = New-Hex 32; $env:HUE_API_PORT = '5080'
docker compose -p hue-demo up -d --build
do { Start-Sleep 2; try { $r = (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$env:HUE_API_PORT/health/ready").StatusCode } catch { $r = 0 } } until ($r -eq 200); 'ready'
```

The first build takes a few minutes. Stop it later with `docker compose -p hue-demo stop` (data is kept), start it again with `docker compose -p hue-demo start`. Keep the same two environment variables in the shell you use for `docker compose` (Compose needs them for every command). The database password is fixed when the volume is first created: after a new `HUE_DB_PASSWORD` on an old volume the database refuses the API, so use the same value or `docker compose -p hue-demo down -v` (this deletes the demo data).

Token command for this path (bash; the PowerShell form is at the end of the next section):

```
mint() { docker compose -p hue-demo exec -T api dotnet HotelUnderwriting.Api.dll dev-token "$@" | tr -d '\r'; }
```

### B. Local `dotnet run`

Quickest start, no login (anyone on this machine may call it; it only listens on 127.0.0.1; data is in memory and lost on restart):

```
dotnet run --project src/HotelUnderwriting.Api
curl -s -X POST http://127.0.0.1:5080/v1/assumption-sets/excel -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" --data-binary @data/Hotel_Acquisition_Underwriting_Model.xlsx
```

With login (this is what you need to test the roles). bash:

```
export ASPNETCORE_ENVIRONMENT=Development Auth__Mode=jwt Auth__Issuer=https://dev.local Auth__Audience=hue-api
export Auth__DevSigningKey=$(openssl rand -hex 32)          # at least 32 bytes; only allowed in the Development environment
dotnet build src/HotelUnderwriting.Api
dotnet run --no-build --project src/HotelUnderwriting.Api > /tmp/hue-api.log 2>&1 &     # stop it later with: kill %1
until curl -sf http://127.0.0.1:5080/health/ready >/dev/null; do sleep 1; done; echo ready
mint() { dotnet run --no-build --project src/HotelUnderwriting.Api -- dev-token "$@" | tr -d '\r'; }
```

PowerShell:

```
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Auth__Mode = 'jwt'; $env:Auth__Issuer = 'https://dev.local'; $env:Auth__Audience = 'hue-api'
function New-Hex($n) { $b = New-Object byte[] $n; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') }) }
$env:Auth__DevSigningKey = New-Hex 32
dotnet build src/HotelUnderwriting.Api
$api = Start-Process dotnet -ArgumentList 'run','--no-build','--project','src/HotelUnderwriting.Api' -PassThru -WindowStyle Hidden    # stop later: Stop-Process $api (and its child: Get-Process HotelUnderwriting.Api | Stop-Process)
do { Start-Sleep 1; try { $r = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5080/health/ready).StatusCode } catch { $r = 0 } } until ($r -eq 200); 'ready'
function mint { dotnet run --no-build --project src/HotelUnderwriting.Api -- dev-token @args }
```

For the Docker stack in PowerShell use `function mint { docker compose -p hue-demo exec -T api dotnet HotelUnderwriting.Api.dll dev-token @args }` instead.

Run the whole walkthrough in one go (bash only; in PowerShell use Git Bash or WSL for it): `bash scripts/demo.sh` for a local server, or `HUE_COMPOSE_PROJECT=hue-demo HUE_URL=http://127.0.0.1:$HUE_API_PORT bash scripts/demo.sh` for the Compose stack (add `HUE_JWT_KEY` already exported, as above; for a local server the same `Auth__` variables must be exported in that shell). It prints PASS or FAIL for every step with the expected and the actual status and exits 0 only when all pass. `bash scripts/demo.sh tokens` prints `export TOKEN_ALICE=...` lines for the examples below: `eval "$(HUE_COMPOSE_PROJECT=hue-demo bash scripts/demo.sh tokens)"` (leave out the variable for a local server).

### Test users and roles

The login is a signed token. In this development setup you make tokens yourself with `mint` (above). `--hours N` sets the lifetime (1 to 24, default 8): after that every call answers 401 and you mint a new token. The scripts use 1 hour.

| User | Command (after `mint` is defined) | Can | Cannot |
|---|---|---|---|
| alice, analyst of deal d1 | `TOKEN_ALICE=$(mint --sub alice --roles analyst --deals d1)` | publish (JSON or Excel) with `X-Deal-Id: d1`: 201, or 200 for content that exists; run (200) and sweep (202) d1's sets; read d1's sets, runs, jobs and results (200) | publish without `X-Deal-Id` (400) or into d2 (403); use `X-Deal-Id: d2` on a run (403); see anything of d2 (404, exactly like a missing id) |
| bob, analyst of deal d2 | `TOKEN_BOB=$(mint --sub bob --roles analyst --deals d2)` | the same for d2 | see d1's set, run or job (404); run d1's set (404) |
| rita, reviewer of deal d1 | `TOKEN_RITA=$(mint --sub rita --roles reviewer --deals d1)` | read d1's sets, runs, jobs and results (200) | publish, upload, run or sweep (403 on every POST); see d2 (404) |
| ada, admin | `TOKEN_ADA=$(mint --sub ada --roles admin)` | everything: read all deals, publish into any deal (with `X-Deal-Id`), run and sweep any set | nothing within the API |
| a user with no role | `TOKEN_NOROLE` (see below) | health probes and the OpenAPI document (they need no token) | everything else: 403 |
| nobody | no `Authorization` header, or a bad token | `/health/*`, `/openapi/v1.json` | everything else: 401 |

`mint` refuses a token without a role, so the roleless user is signed by hand: `scripts/demo.sh tokens` prints `export TOKEN_NOROLE=...` when `HUE_JWT_KEY` (Docker path) or `Auth__DevSigningKey` (local path) is exported in the shell. Without role the API answers 403 to every `/v1` call, even for d1's own resources.

Several users can share one deal (`--deals d1,d2` gives one analyst two deals; then a run or sweep needs `X-Deal-Id`, 400 without it when the set belongs to more than one of those deals). A reviewer or analyst never sees an id from another deal: the answer is the same 404 as for an id that does not exist.

### Curl examples (bash)

Set `U` to the API address and export the tokens (`eval "$(HUE_COMPOSE_PROJECT=hue-demo bash scripts/demo.sh tokens)"`, or the `TOKEN_...=$(mint ...)` lines above):

```
U=http://127.0.0.1:5080
XLSX=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet

# health (no token) and the OpenAPI document
curl -s $U/health/live; curl -s $U/health/ready; curl -s $U/health/startup
curl -s $U/openapi/v1.json -o openapi.json

# publish: Excel route (the reference workbook) and JSON route (the embedded Base scenario)
curl -s -H "Authorization: Bearer $TOKEN_ALICE" -H "X-Deal-Id: d1" -H "Content-Type: $XLSX" --data-binary @data/Hotel_Acquisition_Underwriting_Model.xlsx $U/v1/assumption-sets/excel
curl -s -H "Authorization: Bearer $TOKEN_ALICE" -H "X-Deal-Id: d1" -H "Content-Type: application/json" --data-binary @src/HotelUnderwriting.Api/golden-base.json $U/v1/assumption-sets
SET=bddceda5e0021a3a43bdda20bbfc61f1fcb9cea6a737f13442280ad3993c2f99      # id of the reference workbook, also in the answer above
curl -s -H "Authorization: Bearer $TOKEN_ALICE" $U/v1/assumption-sets/$SET

# run one scenario, then read it (levered IRR of Base: 18.3860%)
curl -s -H "Authorization: Bearer $TOKEN_ALICE" -H "Content-Type: application/json" -d "{\"assumptionSetId\":\"$SET\",\"scenario\":\"Base\"}" $U/v1/runs
curl -s -H "Authorization: Bearer $TOKEN_ALICE" "$U/v1/runs/974d28bf9084e5375243e7607dd1be3156eb5bf8d1892757e62b63d7c7be3929?include=monthly,proforma"

# sweep: 2 x 2 grid, then poll the job and read the results
JOB=$(curl -s -H "Authorization: Bearer $TOKEN_ALICE" -H "Content-Type: application/json" -H "Idempotency-Key: demo-1" -d "{\"assumptionSetId\":\"$SET\",\"scenario\":\"Base\",\"grid\":[{\"path\":\"interestRate\",\"values\":[0.06,0.07]},{\"path\":\"exitCapRate\",\"values\":[0.07,0.075]}]}" $U/v1/sweeps | sed 's/.*"jobId":"\([^"]*\)".*/\1/')
curl -s -H "Authorization: Bearer $TOKEN_ALICE" $U/v1/jobs/$JOB                      # status queued, running, succeeded
curl -s -H "Authorization: Bearer $TOKEN_ALICE" "$U/v1/jobs/$JOB/results?limit=100"

# roles: bob sees nothing of d1 (404), rita can read (200) but not run (403), ada sees all
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOKEN_BOB"  $U/v1/assumption-sets/$SET
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOKEN_RITA" $U/v1/assumption-sets/$SET
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOKEN_RITA" -H "Content-Type: application/json" -d "{\"assumptionSetId\":\"$SET\",\"scenario\":\"Base\"}" $U/v1/runs
curl -s -o /dev/null -w "%{http_code}\n" -H "Authorization: Bearer $TOKEN_ADA"  $U/v1/assumption-sets/$SET
```

In mode `none` (the no-login quick start) leave out the `Authorization` and `X-Deal-Id` headers. The OpenAPI 3.1 document is `GET /openapi/v1.json` (the file is `src/HotelUnderwriting.Api/openapi.v1.json`): in Postman choose Import, then Link, and paste `http://127.0.0.1:5080/openapi/v1.json`; in Swagger Editor (editor.swagger.io) use File, Import file after saving it with the first curl line above (the editor cannot call a login-protected local API from the browser, use it to read the contract).

### Things to try to break it (and the expected answers)

Each is an ordinary call with alice's token and `X-Deal-Id: d1`; every answer is `application/problem+json` and never a 500 or a stack trace.

| Try | Expected |
|---|---|
| Excel route with a text file, an empty body, a half-cut workbook | 422 (the detail names the reason, not the content) |
| Excel route with `Content-Type: text/plain` | 415 |
| An `.xlsx` bigger than 10 MB | 413 |
| A workbook with a macro, an external link, an embedded object, a zip bomb (many entries, huge uncompressed size) | 422 |
| JSON route with `{not json`, or an unknown `"$type"` in a debt event | 400 |
| `Content-Type: application/json; charset=nonsense` (or `utf-7`, or an empty charset) | 415 |
| Purchase price 0, loan-to-cost 1.5, occupancy 5, exit cap rate 0, a negative closing cost | 422 with a fixed text naming the field and never the value |
| Acquisition date `2027-04-15` (not the 1st) or `9999-12-01` | 422 |
| A scenario or line name that is empty, has a leading space, a newline, a zero-width or right-to-left character, or repeats (also as `Base` and `base`) | 422 |
| A sweep with an unknown override path, a non-finite value, or more than 100,000 cases | 422 |
| A sweep case whose value is finite but breaks a rule or overflows (`interestRate` 1e308, `purchasePrice` 0) | the sweep is accepted (202); that case is stored with an `error` and no run is stored for it |
| `GET /v1/runs/zz%0AFAKE` (a newline in the path) | 404, and no forged line in the log |
| Another deal's id, a NUL or control character in an id | 404 |
| A token for another deal, a reviewer's POST, a missing or malformed `X-Deal-Id` | 403 or 400 as in the table above |
| Stop the database (`docker compose -p hue-demo stop db`) and call the API | 503 with `Retry-After`, then normal again after `start db`, without restarting the API |

Hostile workbook files are easiest to make with Excel or any zip tool; the repository's own tests build them (`tests/HotelUnderwriting.Api.Tests/HostileWorkbooks.cs`).

## Architecture

```
src/HotelUnderwriting.Core    pure calculation, no Excel dependency
  Models.cs                   input records (property, scenario, operating lines, % items, seasonality)
  ProformaCalculator.cs       annual forecast: rooms build, fixed/variable lines by driver code, fees, GOP, NOI
  Underwriter.cs              one calculation path for every scenario: timeline, unlevered CF, debt, levered CF, waterfall, returns summary
  Returns.cs                  XIRR (actual/365) and multiples
  EngineInfo.cs               engine version (an input to run keys)
  Reconciler.cs               workbook checkpoints vs engine, difference attribution, IRR impact of workbook errors
src/HotelUnderwriting.Excel   the only project that touches Excel (ClosedXML)
  WorkbookReader.cs           reads assumptions + checkpoints by label, not by cell address
  OutputWriter.cs             writes the new output workbook
src/HotelUnderwriting.Application  ports (assumption source, result sink, run store, clock), content-addressed run keys, SubmitRun and RunAllScenarios use cases, in-memory adapters; references Core only
src/HotelUnderwriting.Cli     wires reader -> engine -> writer -> reconciliation
tests/HotelUnderwriting.Tests xUnit; uses a copy of the reference workbook in data/
tests/HotelUnderwriting.PropertyTests  600 seeded random valid cases asserting the model invariants
```

Inputs, calculation and output are separate: Core never sees a file; the Excel project only converts between files and Core records.
Nothing is hard-coded: scalars, annual vectors, the operating line table (amount, fixed %, driver code), % of revenue items and seasonality are all
read from `Inputs` / `Operating Inputs`. The only computed workbook cells read are the `Reconciliation` checkpoints, and only to compare against.

## Calculation approach

- Months 0..96, month 0 = acquisition date, month m dated `EOMONTH(acq, m-1)`. Hold flag `m <= hold`; sale at the end of the hold month. Hold must be 12-84 (validated).
- Proforma: `Amount(y) = Base x [Fixed% x (1+infl)^n x Days/365 + (1-Fixed%) x Driver(y)/Driver(base)]`. Drivers `OCC_ROOMS, ROOMS_REV, FB_REV, OTH_REV, TOTAL_REV` come from one lookup table, so a new driver is one entry. Leap years use 366 days.
- Monthly revenue and NOI = annual amount x seasonality weight of the calendar month. Forward 12-month NOI = months H+1..H+12, before the asset management fee.
- Debt: commitment = LTC x (price + closing + acq fee + PIP); holdback = PIP total; initial funded = commitment - holdback; PIP spend (inside the hold only) is drawn each month and added to the balance at month end. Interest = opening balance x rate x days/360. Interest-only through the IO period, then payment = debt constant x opening x days/360 (`1/years` when rate = 0). **Payoff = balance at the end of the exit month (after that month's draw and principal).**
- Waterfall: pref balance accrues at `(1+pref)^(1/12)` per month (none in month 0); Tier 1 = `min(distributable, balance + contributions)`; Tier 2 splits `(1 - promote)` pro rata and the promote goes to the GP.
- Refinance (optional, `ScenarioInputs.DebtEvents`): at the end of month R the balance is refinanced into a same-size loan with its own rate, interest-only window and amortization; a fee on the balance is charged in month R; new terms apply from R+1; remaining PIP holdback keeps drawing. With no events every number is unchanged.
- Hurdle tiers (optional, `ScenarioInputs.HurdleTiers`): once the LP has earned a given IRR (an LP balance accrued at `(1+h)^(1/12)`), the GP promote on residual cash steps to that tier's promote. Stackable. With no tiers every number is unchanged.
- IRR: XIRR convention (actual days / 365), own solver (scan for the first sign change from -99.99% upward, then bisection). Returns `null` (shown as n/a) when there is no sign change. Multiple = positives / |negatives|.
- Precision: `double` throughout, full precision internally, currency displayed to 2 decimals. The tolerances ($1 on ~$60M, 0.01 pp on IRR) are far above double rounding error, and the IRR solver needs floating point anyway. `decimal` would add conversions around the solver for no accuracy that the tolerances can see.

## Reconciliation (engine vs workbook checkpoints)

Tolerances from READ ME: currency +/- $1.00; IRRs +/- 0.01 pp; multiples +/- 0.001x. 37 of 66 values pass. All 29 differences are explained by
three workbook errors (below). "Explanation" is evidence, not a guess: the engine has switches that reproduce each workbook error, and a row is
attributed to the smallest set of errors that makes the engine hit the workbook number to within tolerance. With the errors switched on, the engine reproduces the workbook's levered IRR in
all three scenarios (test `Replicating_workbook_errors_reproduces_workbook_irr`).

| Scenario | Value | Workbook | Engine | Difference | Result | Explanation |
|---|---|---:|---:|---:|---|---|
| Base | Loan commitment | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Initial funded loan (after PIP holdback) | 39,751,500.00 | 39,751,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Equity required at close | 24,966,415.00 | 24,966,415.00 | 0.00 | PASS | Within tolerance. |
| Base | 2028 rooms revenue | 16,298,777.88 | 16,298,777.88 | 0.00 | PASS | Within tolerance. |
| Base | 2028 total revenue | 22,620,062.11 | 22,620,062.11 | 0.00 | PASS | Within tolerance. |
| Base | 2029 total departmental expenses | 8,134,491.18 | 8,134,491.18 | 0.00 | PASS | Within tolerance. |
| Base | 2029 total undistributed expenses | 6,657,219.58 | 6,657,219.58 | 0.00 | PASS | Within tolerance. |
| Base | 2029 NOI | 6,147,003.94 | 6,147,003.94 | 0.00 | PASS | Within tolerance. |
| Base | Month 1 interest | 236,852.69 | 236,852.69 | -0.00 | PASS | Within tolerance. |
| Base | First amortizing payment | 305,344.00 | 305,344.00 | 0.00 | PASS | Within tolerance. |
| Base | Peak loan balance | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Base | Forward 12-month NOI at exit | 6,990,168.13 | 6,990,168.13 | -0.00 | PASS | Within tolerance. |
| Base | Gross sale price | 87,377,101.64 | 87,377,101.64 | 0.00 | PASS | Within tolerance. |
| Base | Loan payoff at exit | 43,446,146.34 | 43,407,596.71 | -38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Net sale proceeds to equity | 41,309,642.26 | 41,348,191.89 | 38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Unlevered IRR | 12.3078% | 12.3078% | -0.0000 pp | PASS | Within tolerance. |
| Base | Levered IRR | 18.3677% | 18.3860% | 0.0183 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | Levered equity multiple | 2.1092x | 2.1108x | 0.0015 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Base | LP IRR | 16.6365% | 16.6089% | -0.0276 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | GP IRR | 30.5001% | 30.7736% | 0.2735 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | GP multiple | 3.3820x | 3.4171x | 0.0351 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Base | Check: loan balance after exit (must be 0) | -38,549.63 | 0.00 | 38,549.63 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Loan commitment | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Initial funded loan (after PIP holdback) | 39,751,500.00 | 39,751,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Equity required at close | 24,966,415.00 | 24,966,415.00 | 0.00 | PASS | Within tolerance. |
| Upside | 2028 rooms revenue | 17,288,623.23 | 17,288,623.23 | 0.00 | PASS | Within tolerance. |
| Upside | 2028 total revenue | 23,820,689.88 | 23,820,689.88 | 0.00 | PASS | Within tolerance. |
| Upside | 2029 total departmental expenses | 8,382,774.43 | 8,382,774.43 | -0.00 | PASS | Within tolerance. |
| Upside | 2029 total undistributed expenses | 6,784,566.66 | 6,784,566.66 | 0.00 | PASS | Within tolerance. |
| Upside | 2029 NOI | 7,032,258.46 | 7,032,258.46 | -0.00 | PASS | Within tolerance. |
| Upside | Month 1 interest | 223,602.19 | 223,602.19 | 0.00 | PASS | Within tolerance. |
| Upside | First amortizing payment | 293,280.39 | 293,280.39 | -0.00 | PASS | Within tolerance. |
| Upside | Peak loan balance | 44,791,500.00 | 44,791,500.00 | 0.00 | PASS | Within tolerance. |
| Upside | Forward 12-month NOI at exit | 7,922,785.86 | 7,922,785.86 | 0.00 | PASS | Within tolerance. |
| Upside | Gross sale price | 102,229,494.93 | 102,229,494.93 | 0.00 | PASS | Within tolerance. |
| Upside | Loan payoff at exit | 44,333,521.61 | 44,291,252.45 | -42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Net sale proceeds to equity | 54,829,088.47 | 54,871,357.63 | 42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Unlevered IRR | 18.3020% | 18.3020% | -0.0000 pp | PASS | Within tolerance. |
| Upside | Levered IRR | 32.2786% | 32.3000% | 0.0214 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | Levered equity multiple | 2.7055x | 2.7072x | 0.0017 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Upside | LP IRR | 28.5527% | 28.5376% | -0.0151 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | GP IRR | 56.3262% | 56.5242% | 0.1980 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | GP multiple | 5.2386x | 5.2653x | 0.0267 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) |
| Upside | Check: loan balance after exit (must be 0) | -42,269.16 | 0.00 | 42,269.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Downside | Loan commitment | 41,850,000.00 | 41,850,000.00 | 0.00 | PASS | Within tolerance. |
| Downside | Initial funded loan (after PIP holdback) | 35,970,000.00 | 35,970,000.00 | 0.00 | PASS | Within tolerance. |
| Downside | Equity required at close | 28,718,500.00 | 28,718,500.00 | 0.00 | PASS | Within tolerance. |
| Downside | 2028 rooms revenue | 14,724,838.80 | 14,724,838.80 | -0.00 | PASS | Within tolerance. |
| Downside | 2028 total revenue | 20,760,663.36 | 20,760,663.36 | -0.00 | PASS | Within tolerance. |
| Downside | 2029 total departmental expenses | 7,837,624.95 | 7,737,580.29 | -100,044.67 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | 2029 total undistributed expenses | 6,426,672.69 | 6,426,672.69 | -0.00 | PASS | Within tolerance. |
| Downside | 2029 NOI | 4,704,349.11 | 4,804,393.77 | 100,044.67 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Month 1 interest | 232,306.25 | 232,306.25 | 0.00 | PASS | Within tolerance. |
| Downside | First amortizing payment | 291,882.43 | 291,882.43 | -0.00 | PASS | Within tolerance. |
| Downside | Peak loan balance | 41,754,607.86 | 41,754,607.86 | 0.00 | PASS | Within tolerance. |
| Downside | Forward 12-month NOI at exit | 4,626,422.97 | 5,004,823.48 | 378,400.51 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Gross sale price | 52,873,405.39 | 57,197,982.67 | 4,324,577.28 | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Loan payoff at exit | 39,594,124.41 | 39,562,618.33 | -31,506.09 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |
| Downside | Net sale proceeds to equity | 11,428,711.79 | 15,633,434.95 | 4,204,723.16 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Unlevered IRR | 2.7380% | 3.8762% | 1.1382 pp | FAIL | Workbook error: reproduced only by switching on #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Levered IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Levered equity multiple | 0.5993x | 0.7850x | 0.1857 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | LP IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | GP IRR | -8.0564% | -3.8510% | 4.2054 pp | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | GP multiple | 0.5993x | 0.7850x | 0.1857 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73); #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) |
| Downside | Check: loan balance after exit (must be 0) | -31,506.09 | 0.00 | 31,506.09 | FAIL | Workbook error: reproduced only by switching on #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) |

## Workbook issues

Each was confirmed by reading the formula against READ ME, by a formula diff across columns and across the three scenario sheets, and by the
reconciliation attribution above. IRR impact = (workbook behaviour) - (spec behaviour), switching one error on at a time.

| # | Where | What is wrong | READ ME rule | Engine or workbook error? |
|---|---|---|---|---|
| 1 | `Monthly CF - Base/Upside/Downside` row 73, `D73:CU73` (`=D47*D67`) | Loan payoff uses the **opening** balance of the exit month, so that month's draw and scheduled principal are ignored. The workbook's own control `B112` is not 0: -38,549.63 / -42,269.16 / -31,506.09. | B29: payoff = balance at the END of the exit month, after draw and principal. | Workbook. |
| 2 | `Monthly CF - ...` `B37` (`=1+B23/12`), used by row 87 | Pref accrues at simple monthly `1 + pref/12` (8%/12). | B36: `(1+pref)^(1/12)` (compounded annually, 8% effective). | Workbook. Affects LP and GP IRR only; levered IRR unchanged. |
| 3 | `Proforma - Downside` `E26:L26` (F&B expense 2029-2036) | Variable part divides by **rooms revenue** (`E$19/$B$19`) instead of F&B revenue (`E$20/$B$20`). Base and Upside sheets are correct, so the three scenario sheets are inconsistent. | B17 and Operating Inputs `F8`: driver for FB_EXP is FB_REV. | Workbook. Understates Downside NOI from 2029. |
| 4 | `Monthly CF - ...` `CJ53:CT53`, `CU53` blank | Forward 12-month NOI window is cut off at month 96. | n/a | Not a defect in range: hold is limited to 84 months, whose forward window ends at month 96. Recorded as a limitation. |

Effect on levered IRR (percentage points; LP / GP shown because error #2 only moves those):

| Scenario | Workbook error | Levered IRR (spec) | Levered IRR (with error) | Levered impact (pp) | LP IRR impact (pp) | GP IRR impact (pp) |
|---|---|---:|---:|---:|---:|---:|
| Base | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | 18.3860% | 18.3677% | -0.0183 | -0.0156 | -0.0336 |
| Base | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | 18.3860% | 18.3860% | 0.0000 | +0.0432 | -0.2397 |
| Base | All errors together (replicates the workbook) | 18.3860% | 18.3677% | -0.0183 | +0.0276 | -0.2735 |
| Upside | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | 32.3000% | 32.2786% | -0.0214 | -0.0188 | -0.0350 |
| Upside | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | 32.3000% | 32.3000% | 0.0000 | +0.0339 | -0.1628 |
| Upside | All errors together (replicates the workbook) | 32.3000% | 32.2786% | -0.0214 | +0.0151 | -0.1980 |
| Downside | #1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73) | -3.8510% | -3.8733% | -0.0224 | -0.0224 | -0.0224 |
| Downside | #2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87) | -3.8510% | -3.8510% | 0.0000 | 0.0000 | 0.0000 |
| Downside | #3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26) | -3.8510% | -8.0272% | -4.1762 | -4.1762 | -4.1762 |
| Downside | All errors together (replicates the workbook) | -3.8510% | -8.0564% | -4.2054 | -4.2054 | -4.2054 |


Notes on the numbers:
- Error #3 is the big one: it takes Downside levered IRR from -3.85% (spec) to -8.06% (workbook), because it understates 2029+ NOI and so the forward NOI and the gross sale price (gross sale price 52.9M in the workbook vs 57.2M in the engine).
- Error #1 moves levered IRR by about 0.02 pp (just above the 0.01 pp tolerance). Error #2 alone moves GP IRR by 0.16 pp (Upside) and 0.24 pp (Base), well above tolerance.
- Because the reference workbook was not edited or re-saved, the engine's values are the corrected ones; the workbook's are left as supplied.

## Tests

`dotnet test`: **173 unit tests and 12 property tests on both net8.0 and net10.0, and 322 API and 44 PostgreSQL tests on net10.0, 0 failed.**

| Requirement | Test |
|---|---|
| Known result | `Base_known_results_match_workbook` (commitment, funded loan, equity, leap-year 2028 rooms revenue, 2029 NOI, month 1 interest, first amortizing payment, sale price, unlevered IRR) |
| Changed assumption | `Hold_36_months_exits_in_month_36_and_loan_is_repaid`; `Changed_input_in_a_copy_changes_the_engine_with_no_code_change` (workbook copy edited, no code change) |
| Edge cases | `Interest_only_longer_than_hold_never_amortizes`, `Zero_interest_rate_uses_straight_line_debt_constant`, `Pip_running_past_exit_is_cut_off_at_the_hold_end`, `Hold_outside_12_to_84_is_rejected` |
| IRR solver | `Xirr_finds_a_negative_irr`, `Xirr_matches_actual_over_365_convention`, `Xirr_returns_null_without_a_sign_change` |
| Waterfall invariant | `Waterfall_lp_plus_gp_equals_levered_cash_flow` (5 cases incl. 36 and 12 month holds); the same invariant on every random case |
| Reconciliation | `Reconciliation_every_difference_is_explained`, `Replicating_workbook_errors_reproduces_workbook_irr` (3), `Spec_engine_loan_is_fully_repaid` (3), `Workbook_payoff_error_leaves_a_residual_balance_like_the_workbook_check` |
| Import and output | `WorkbookTests`: assumption import, all 12 output sheets, written cell values equal the engine, reference workbook byte-identical and timestamp unchanged |
| Refinance | `DebtEventTests` (30 tests): terms, fee, IO windows, two and overlapping refinances, validation, roll-forward conservation, pins today's numbers with no events |
| Hurdle tiers | `PromoteTierTests` (36 tests): an independent reference waterfall, LP IRR lands at the hurdle with a 99% promote, never-reached tier changes nothing, validation, pins today's LP and GP cash flows |
| Application layer | `ApplicationTests`: stable and sensitive run keys, submit twice computes once, concurrent submits, quirks rejected, layering |
| Business rules | `AssumptionRulesTests` (21 tests, one row per rule with a value just inside and one just outside, finite check, sweep overflow) and the API and PostgreSQL tests of the business rules and error handling (`HardeningApiTests`, `HardeningPersistenceTests`: rules on both routes, charset, NUL ids, database outage and recovery) |
| Property tests | `tests/HotelUnderwriting.PropertyTests`: 600 seeded cases (seed = 20261124 + case), loan repaid, LP + GP = levered CF, sources = uses recomputed from inputs, finiteness; failures print the seed. The suite was mutation-tested: breaking the loan payoff or the financing costs makes it fail |

Reproducing a property failure: the failure message prints the case index and seed; `ScenarioGenerator.Generate(assumptions, caseIndex)` regenerates it.

## HTTP API

`src/HotelUnderwriting.Api` is an ASP.NET Core minimal API (net10.0, in-memory stores by default, optional PostgreSQL below). Run it with `dotnet run --project src/HotelUnderwriting.Api`; it listens on `http://127.0.0.1:5080` (override with `ASPNETCORE_URLS`). By default there is no authentication and the API refuses to listen on anything but loopback; see [Security](#security) for bearer tokens.

| Method and path | Purpose |
|---|---|
| `POST /v1/assumption-sets` | Publish an immutable `Assumptions` JSON body. 201 with `Location` the first time, 200 for identical content |
| `POST /v1/assumption-sets/excel` | The same from a raw `.xlsx` body (see [Excel upload](#excel-upload)) |
| `GET /v1/assumption-sets/{id}` | Fetch `{id, createdUtc, scenarios}` |
| `POST /v1/runs` | `{assumptionSetId, scenario}` runs one scenario synchronously; idempotent through the run key (`wasCached`) |
| `GET /v1/runs/{runKey}` | Fetch a run; `?include=monthly,proforma` adds those objects |
| `POST /v1/sweeps`, `GET /v1/jobs/{id}`, `GET /v1/jobs/{id}/results` | Sweeps (see [Sweeps](#sweeps)) |
| `GET /health/live`, `/health/startup`, `/health/ready` | Health |
| `GET /openapi/v1.json` | The OpenAPI 3.1 contract (`src/HotelUnderwriting.Api/openapi.v1.json`, tested against the real routes) |

Errors are `application/problem+json`: 400 malformed JSON or unknown `$type`, 404 unknown id or scenario, 405, 408 upload body too slow, 413 body over 1 MB (10 MB for the Excel upload), 415 wrong content type, 503 too many uploads or the database unavailable (a lost connection, a command or connection timeout, or a server answer of class 08, 53 or 57; sent with `Retry-After`; retrying is safe, because a set, run or job may already be stored when the failure came after the write, and every write is idempotent; logged as an error without detail; a database answer about the data, such as a constraint or trigger failure, stays a 500), 422 engine validation or a rule below (for example hold outside 12-84 months), 500 generic for a fault of the server itself; in jwt mode also 401 and 403. A `DebtEvent` is written as an object with `"$type": "Refinance"`.

A published set (either route) may hold at most 20 scenarios, 500 operating lines, 50 percent-of-revenue items and 50 years per scenario vector, and each name, code, section, base and driver at most 100 characters and without control, format or invisible characters (see the character rule below). A set over a limit, a percent item with an unknown base, and inputs that overflow or give non-finite cash flows (any number of the summary, the monthly schedule including the pref series, or the proforma) are 422. The limits are checked when a set is published: sets already stored by an earlier version are served as stored (a later publish of the same content is checked again, before the store is asked, so a rule violation is a 422 also while the database is down).

Business rules, one place in code (`AssumptionRules`), the same on the JSON and the Excel route and applied again to every run, so also to every sweep case. A violation is a 422 with a fixed text that names the field and never quotes the rejected value; the text may name the scenario (its name passed the character rules) and, for the engine's own messages such as the hold period, a number (a sweep case is stored as a case `error` instead and no run is stored): purchase price above 0; closing costs and PIP per key at least 0; acquisition, origination and disposition fee, cost of sale, asset management fee, percent-of-revenue rates, interest rate, loan-to-cost, GP share, promote and preferred return from 0 to 1; exit cap rate above 0 and at most 1; placement costs at least 0; occupancy (every year and the base year) from 0 to 1; ADR growth and expense inflation above -1; base ADR, keys and amortization years above 0; interest-only months at least 0; hold 12-84 months. Also bounded: an operating line's fixed share from 0 to 1 and its base amount at least 0; the percent-of-revenue rates add up to at most 1; cost of sale plus disposition fee at most 1; a refinance rate and fee at most 1 and a hurdle at most 1 (their lower bounds, and a hurdle above the pref, are the engine's own rules). With a PIP budget (PIP per key above 0) the duration and the start month are at least 1 and the PIP ends within the hold. Scenario names (ignoring case), line codes and percent-item codes are not empty, have no leading or trailing whitespace (also no-break and ideographic spaces) and are unique; a sweep case name, when given, is not empty or padded either (new submissions only); there is at least one operating line; the year list is strictly ascending. The acquisition date is the 1st of a month at midnight (a far-future date whose 96-month horizon overflows is a 422 too). Character rule: no name, code, section, driver or sweep case name may contain a character of Unicode category Cc (control), Cf (format: bidi controls, zero-width characters, LRM/RLM/ALM, soft hyphen, U+180E, invisible operators, tag characters), Cs (a lone surrogate), Co (private use), Cn (unassigned), Zl or Zp (line and paragraph separators), nor U+034F (combining grapheme joiner). Letters of any script, hyphens and inner spaces are fine. A base-year occupancy of exactly 0 passes the range rule, but every line driven by the rooms series (`OCC_ROOMS`, `ROOMS_REV`) scales with driver(year) / driver(base year), which is 0/0: such a set is refused with a 422 naming the line ("Line X: its driver OCC_ROOMS has a zero base year because the base occupancy is 0."). Likewise a revenue line used as another line's driver with a base amount of 0 gives "Line X: its driver line Y has a zero base amount." Both are explanations of a refusal that already happened (the finite check); a result of valid input is never changed. A stored run is served as it is and is not checked again. The sweep-case-name character rule applies to a new submission only, so a stored job stays readable. A JSON date with an offset (for example `2027-04-01T00:00:00+05:30`) is converted to the server's local time and may therefore stop being midnight on the 1st and be refused: send `Z` or a plain date. The unknown override path message does not repeat the path. A 404 or 400 text quotes an id, run key or scenario only when it matches `[A-Za-z0-9._-]{1,64}`; anything else is answered with a generic "was not found". An unknown line driver is refused without quoting it.

A JSON request whose `Content-Type` has a charset the body cannot be read with (unknown, empty, UTF-7 or given twice) is 415; a quoted or differently cased charset (`charset="UTF-8"`) is read. The body is read by the API itself, so no Content-Type value can make the reader fail with a 500. An id (set id, run key, job id) with a NUL or other control character is an unknown id (404), never a 500.

## Excel upload

`POST /v1/assumption-sets/excel` takes the workbook itself as the request body (no multipart) and publishes it exactly like the JSON route: the same roles, `X-Deal-Id` header, ledger actor, 201/200 and response body. Headers and authorization are checked before the body is read. Nothing is written to disk.

```
curl -s -X POST http://127.0.0.1:5080/v1/assumption-sets/excel -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" --data-binary @data/Hotel_Acquisition_Underwriting_Model.xlsx
```

In jwt mode add `-H "Authorization: Bearer $TOKEN" -H "X-Deal-Id: d1"`.

Limits:

- Body: 10 MB (413), received within `Upload:BodyTimeoutSeconds` (408, the connection is closed). Content type: only the `.xlsx` one above (415).
- Package (422), checked before the workbook library opens it, on the bytes actually decompressed: at most 500 entries, 50 MB per entry, 100 MB in total, 50 sheets, 20,000 rows per sheet and 200,000 cells. Refused: anything that is not an `.xlsx` workbook, duplicate or unsafe entry names, macros (`.xlsm`, `.xltm`, `.xlsb`), external links and any external relationship other than a hyperlink, data connections and query tables, embedded OLE objects or packages, ActiveX, and any DTD. A sheet referred to twice is refused. Hyperlinks are allowed and never followed. Sizes are what the zip reader delivers: it stops at the size an entry declares, so a cut-off XML part is refused, while a non-XML part that declares less than it holds is truncated and accepted (nothing that reads the package sees the rest).
- The reader also refuses an error value (such as `#REF!`) in a cell it needs, an operating line section other than `REV`, `DEPT`, `UNDIST` or `FIXED`, a count, month or year that is not a whole number, and a repeated scenario or seasonality month.
- Formulas are never evaluated. A cell the reader needs that holds a formula uses the value cached in the file; without one the answer is 422 naming the sheet and cell. A file saved by a tool that stores no cached values has to be recalculated and saved in a spreadsheet application first.
- Parsing runs in a child process (`HotelUnderwriting.Api parse-workbook`) whose environment holds no connection strings, `Auth*` settings or `OTEL*` headers. It is killed with its process tree at the time limit, when the client disconnects and when the service stops, and ends itself 5 seconds after the time limit if nothing else did. At most 1 MB of its output is read.
- A bad file is 422. Running out of time or memory, or crashing while parsing, is a 422 "could not be processed within limits" and a warning in the log naming the cause. A server fault is a 500 with an error in the log: the child cannot be started, does not start up, or cannot load or initialise a dependency.
- At most `Upload:MaxConcurrent` uploads are processed at once; further ones get 503 with `Retry-After`.

| Setting | Default | Allowed |
|---|---:|---|
| `Upload:TimeoutSeconds` (wall clock for the child) | 30 | 1 to 300 |
| `Upload:MaxHeapMb` (the child's .NET GC heap hard limit) | 512 | 384 to 4096 |
| `Upload:BodyTimeoutSeconds` (wall clock for receiving the body) | 30 | 1 to 300 |
| `Upload:MaxConcurrent` | 2 | 1 to 16 |

A value outside its range stops the service at startup.

Not covered:

- No malware scan.
- The child runs as the same service account and can use the same file system and network: it is a resource boundary, not a sandbox.
- The heap limit bounds the .NET heap only, not all native memory of the child.
- No per-user rate limit.
- Parsing is synchronous (the request waits) rather than queued to a worker.
- A slow client can hold a concurrency slot for up to the body time limit, and can do so again.
- If the API process itself is killed, a child that is parsing runs on until it finishes or ends itself.

## Sweeps

A sweep runs one base scenario of a published assumption set many times with different overrides, asynchronously. `POST /v1/sweeps` stores a job (the request, not the expanded cases) and answers 202 with `{jobId, status, caseCount, chunkCount}` and `Location: /v1/jobs/{jobId}`. The cases are split into chunks of 500; workers lease a chunk, run each case through the same idempotent `SubmitRun` (so run keys, caching and ledger rows are the normal ones, actor `worker`), and store one result per case.

| Method and path | Purpose |
|---|---|
| `POST /v1/sweeps` | `{assumptionSetId, scenario, cases?: [{name?, overrides:[{path, value}]}], grid?: [{path, values:[...]}]}`, exactly one of `cases` and `grid`. Optional `Idempotency-Key` header (at most 128 chars of `A-Za-z0-9._-`): the same key and request returns the same job, the same key with a different request is 409. 404 unknown set or scenario, 422 invalid overrides or limits |
| `GET /v1/jobs/{id}` | `{jobId, status, createdUtc, caseCount, completedCases, failedCases, chunks:{total, done, queued, leased, dead}}`. `completedCases` counts every case with a stored result (failed ones included) |
| `GET /v1/jobs/{id}/results?after=&limit=` | Results ordered by `caseIndex`, `limit` 1 to 1000 (default 100); `next` is the last index when more remain, else null. Each item has `runKey`, `error` and a `summary` taken from the stored run |

Overrides set one of these camelCase numeric fields: `purchasePrice, closingCosts, acquisitionFeePct, pipPerKey, pipStartMonth, pipDurationMonths, expenseInflation, loanToCost, interestRate, interestOnlyMonths, amortizationYears, originationFeePct, placementCosts, assetMgmtFeePct, holdMonths, exitCapRate, costOfSalePct, dispositionFeePct, gpShare, prefReturn, promote`. The integer fields (`pipStartMonth, pipDurationMonths, interestOnlyMonths, holdMonths`) need whole numbers. Overrides apply in list order, last write wins; vectors, debt events and tiers are never touched. A `grid` is the cartesian product of its axes, first axis varying slowest. Limits: 100,000 cases, 20 grid axes, 1,000 values per axis, 20 overrides per case, case names of 100 characters.

Job status: `queued` (no chunk claimed yet), `running`, `succeeded` (all chunks done), `failed` (a chunk is dead; the other chunks still finish, so `failed` can be reported while others still run). Chunks finish in any order, so page `/results` only once the job is `succeeded` or `failed` with nothing queued or leased; a page read earlier can skip cases whose chunk finishes later. A case the engine rejects (for example `holdMonths` 5) is stored as that case's `error` and does not fail the job.

The queue is a table: workers claim the oldest queued chunk, or one whose 60 second lease expired, with `FOR UPDATE SKIP LOCKED` (a lock in memory mode). Each claim counts an attempt; a chunk that already had 5 attempts is marked `dead` (dead letter) instead of being leased again. A crashed worker only means its lease expires and the chunk is claimed again; reprocessing is harmless because runs come back from the run store and results are upserted. A completion from a worker that lost its lease is ignored. A worker renews the lease while it works (see Observability, worker hardening). Known limit: expiry is judged by each worker's own clock, so run workers on clock-synchronised hosts. In jwt mode a job still waiting for its deal grant is deferred without using up an attempt (see Security). Job rows, chunks and the `sweep.submitted` ledger row are written in one transaction. Migration `002_sweeps.sql` adds `jobs`, `job_chunks` and `job_results` and changes no existing table.

Configuration: `Role` is `api`, `worker` or `all` (default `all`; `api` starts no worker loops, so a sweep stays queued until a process with workers runs against the same database) and `Worker:Count` is the number of worker loops per process (default 2, 1 to 16). Example:

```
curl -s -X POST http://127.0.0.1:5080/v1/sweeps -H "Content-Type: application/json" -H "Idempotency-Key: demo-1"   -d '{"assumptionSetId":"<id>","scenario":"Base","grid":[{"path":"interestRate","values":[0.06,0.07,0.08]},{"path":"exitCapRate","values":[0.07,0.075]}]}'
curl -s http://127.0.0.1:5080/v1/jobs/<jobId>
curl -s "http://127.0.0.1:5080/v1/jobs/<jobId>/results?limit=100"
```

## PostgreSQL mode

By default the API keeps everything in memory. Set `Persistence__ConnectionString` (or `Persistence:ConnectionString`) to a PostgreSQL connection string and the API applies the embedded migrations at startup (`HotelUnderwriting.Persistence`, advisory-locked and idempotent), then stores assumption sets, runs and an append-only audit ledger there. A `Search Path=<schema>` in the string selects the schema (default `public`). The `ledger` table rejects UPDATE, DELETE and TRUNCATE with a trigger. Every publish and run appends a ledger row (actor: the token subject in jwt mode, `anonymous` in mode none, `worker` for rows written by sweep workers) carrying the `X-Correlation-Id` request header (or a generated GUID, echoed in the response). `/health/ready` queries the database and returns 503 problem+json when it is unreachable.

The persistence tests run against a throwaway database and are skipped unless `HUE_TEST_PG` is set, for example `HUE_TEST_PG=Host=127.0.0.1;Port=55432;Username=postgres;Database=hue_test` (trust authentication, no password). Each test creates and drops its own `hue_<random>` schema.

Throwaway database for the tests: `docker run -d --name hue-test-pg -p 127.0.0.1:55432:5432 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=hue_test postgres:16-alpine`, then set `HUE_TEST_PG=Host=127.0.0.1;Port=55432;Username=postgres;Database=hue_test`. Without `HUE_TEST_PG` the database tests show as Skipped, so a green plain run proves nothing about them; set `HUE_REQUIRE_PG=1` (CI) to make them fail instead of skip when `HUE_TEST_PG` is missing.

Operator notes:

- The setting is `Persistence:ConnectionString` (environment `Persistence__ConnectionString`). Connection strings belong in secrets, never in files. Defaults: connect timeout 5 s, command timeout 15 s; `Search Path` may name one schema only.
- Migrations and the app currently use one role. In production run migrations as the owner role and give the app role only INSERT and SELECT on `ledger` (and no DDL): the append-only trigger does not stop a table owner from dropping the table.
- The state write and its ledger row commit in one database transaction, so either both exist or neither does. If the ledger insert fails because the database answered with an error (a constraint or trigger) the call returns the generic 500 and nothing is stored, and a retry then logs `run.computed`; if the connection is lost or times out the call is a 503, with the same result (nothing stored, retrying is safe). A repeat of an existing run (or a republish of a set) writes only a ledger row, in its own transaction.
- Republishing an existing assumption set writes another `assumption_set.published` ledger row.
- A repeat of an existing run, or a republish of an existing set, writes only a ledger row, so it also fails if the ledger insert fails (500 for a database answer, 503 for a lost connection or timeout): the ledger is required, not best effort.
- If a commit succeeds but the response is lost (connection drop), the client retry is logged as `run.served_from_store`, so a `run.computed` row can exist for a run the client believes failed. Both are correct and expected.
- If the database is down at startup the API still starts; `/health/ready` retries the migration on each call and returns 503 until it works. While the database is unreachable every route that needs it (publish, run, sweep, GET of a set, run or job, Excel upload after parsing) answers 503 with `Retry-After: 5`; the service recovers by itself when the database is back, without a restart.
- With no connection string the API logs a warning and uses in-memory stores that lose data on restart.

## Security

`Auth:Mode` (environment `Auth__Mode`) is `none` (default) or `jwt`.

- `none`: no token, no access control, loopback only. The process (including `Role=worker`) refuses to start when any listen address, however configured (`--urls`, `ASPNETCORE_URLS`, `Kestrel:Endpoints`, `HTTP_PORTS`/`HTTPS_PORTS`), is not `localhost`, `127.0.0.0/8` or `::1`, when an endpoint is not an IP address (Unix socket, named pipe), or when the server is not Kestrel (for example IIS in-process). Requests are served only with the `Host` header `localhost`, `127.0.0.1` or `[::1]` (400 otherwise, against DNS rebinding; `AllowedHosts` does not widen this). The official .NET container images set `ASPNETCORE_HTTP_PORTS`, which listens on all addresses, so mode `none` refuses to start in them: use `jwt`. A reverse proxy or port forward in front of a mode `none` process exposes it.
- `jwt`: standard bearer tokens (ASP.NET Core JwtBearer). Requires `Auth:Issuer`, `Auth:Audience` and exactly one of `Auth:Authority` (an OpenID Connect provider; https, http only for loopback; keys from discovery; RS256, ES256 or PS256) or `Auth:DevSigningKey` (at least 32 bytes; HS256 only; allowed only in the `Development` environment, and the default environment is `Production`). Signature, issuer, audience, expiry and not-before are checked with 60 s clock skew. A rejected token is logged by failure type only: Information for a bad token, Warning for an unknown signing key or a failure that is not about the token (for example an unreachable authority). A warning is logged at start when it listens on plain http beyond loopback.

Before switching a shared database to `jwt`, stop all old workers: a worker of an earlier version computes sweep jobs without granting their runs.

Claims: `sub` (required, at most 128 characters, no control characters, not `worker` or `anonymous`; recorded as the ledger actor), `exp` (required), `roles` (string or array), `deals` (string or array of deal ids, 1 to 64 characters of `A-Za-z0-9._-`; other entries are ignored).

| Role | GET | POST | Sees |
|---|---|---|---|
| `analyst` | yes | yes | resources of its deals; publishes only into its deals |
| `reviewer` | yes | no (403) | resources of its deals |
| `admin` | yes | yes | everything; publishes into any deal |
| none of these | 403 | 403 | nothing |

- Everything except `/health/*` and `/openapi/v1.json` needs a token: 401 with `WWW-Authenticate: Bearer` and no reason.
- `POST /v1/assumption-sets` and `POST /v1/assumption-sets/excel` need the header `X-Deal-Id`.
- A new run or sweep job is granted to exactly one deal. `POST /v1/runs` and `POST /v1/sweeps` take `X-Deal-Id`: it must be a deal of the assumption set and, for an analyst, one of the token's deals (403 otherwise; 400 when malformed). It may be left out when there is only one candidate (an analyst: the deals shared with the set; an admin: the shared ones, or all of the set's when none is shared); with several the answer is 400 and nothing is computed or stored. A set without any deal can only be used by an admin, without the header, and the result belongs to no deal.
- A resource of another deal answers 404 exactly like a missing one. Resources created in mode `none` belong to no deal and only `admin` sees them. Publishing identical content into a second deal shares the set; a deal sees a run only once a run or sweep request grants it to that deal.
- Sweep `Idempotency-Key` values are scoped per subject and target deal: the same key for another deal creates another job.
- Grants live in the table `deal_resources` (migration `003_deal_access.sql`) and are only ever added. If a grant fails the request is a 500 (a 503 when the connection is lost or times out) and repeating it repairs it, because the set, run or job is already stored: a publish or run at any time, a sweep with the same `Idempotency-Key` and deal at any time (a repeat never adds deals to a job that already has a grant). A worker does not compute a job that is waiting for its grant: it defers the chunk for half the job's age (between 1 and 60 s) without using up an attempt, so the job is never dead-lettered for this, and logs a warning once the job is 60 s old.

Local development token (HS256, default 8 hours, at most 24; `Development` environment only). From the repository root, in one bash shell, so the server and the token command have the same key:

```
export ASPNETCORE_ENVIRONMENT=Development Auth__Mode=jwt Auth__Issuer=https://dev.local Auth__Audience=hue-api
export Auth__DevSigningKey="$(openssl rand -base64 48)"
dotnet build src/HotelUnderwriting.Api
TOKEN=$(dotnet run --no-build --project src/HotelUnderwriting.Api -- dev-token --sub alice --roles analyst --deals d1,d2)
dotnet run --no-build --project src/HotelUnderwriting.Api &
until curl -sf http://127.0.0.1:5080/health/ready > /dev/null; do sleep 1; done
curl -s -H "Authorization: Bearer $TOKEN" -H "X-Deal-Id: d1" -H "Content-Type: application/json" -d @src/HotelUnderwriting.Api/golden-base.json http://127.0.0.1:5080/v1/assumption-sets
kill %1
```

Limitations:

- Publishing content that already exists shows that it existed: 200 instead of 201 with the first `createdUtc`, and a run of it can answer `wasCached` true.
- An ID token issued for the same audience is not distinguished from an access token: give the API its own audience.
- Grants are not written to the ledger, and the API cannot remove them.
- A sweep submitted without an `Idempotency-Key` whose grant failed cannot be repaired: its job stays waiting (submit it again).
- A reverse proxy in front of a mode `none` process exposes it.
- Not covered: TLS termination (put it in front of the service), rate limiting, token revocation (keep token lifetimes short) and management of deal membership (it comes from the identity provider's claims).

## Observability

Metrics and traces use the in-box `System.Diagnostics` types, so the engine emits them with no package. Both are named `HotelUnderwriting` and carry the engine version.

| Instrument | Kind | Tags |
|---|---|---|
| `hue.run.duration` | histogram, ms | `outcome` = computed, cached, rejected |
| `hue.runs` | counter | `outcome` = computed, cached, rejected |
| `hue.sweep.chunks` | counter | `outcome` = done, failed, dead, deferred, lease_lost |
| `hue.sweep.cases` | counter | `outcome` = ok, error |
| `hue.queue.depth` | observable gauge | queued plus expired-lease chunks |
| `hue.queue.oldest_age` | observable gauge, seconds | age of the oldest waiting chunk (its job's creation time) |
| `hue.queue.dead` | observable gauge | chunks that used all attempts |

The cache hit ratio is derived by dashboards from `hue.runs`. Spans: one per HTTP request (ASP.NET Core instrumentation), `run.submit` per run and `sweep.chunk` per processed chunk, tagged only with `run_key`, `job_id`, `chunk_id`, `engine_version`, `correlation_id` and `outcome`. Logs use message templates; request scopes carry `correlation_id`, worker scopes `correlation_id` and `job_id`. Logs are not exported over OTLP. Metric `hue.runs` also has outcome `error` (store or ledger failure). A chunk found dead while being claimed (its last lease expired) shows only in `hue.queue.dead`, not in `hue.sweep.chunks`.

The no-figures rule: only ids, hashes, counts, durations, versions and status codes appear in logs, metric tags and span tags. No price, rate, IRR or other assumption value is ever written there.

Log forging: the ASP.NET Core request-start and request-finish lines (category `Microsoft.AspNetCore.Hosting.Diagnostics`) quote the client's raw path and query, so a `%0A` in a URL would start a forged line in a console log. The API sets that category to Warning by default (`ApiHost`), so those two lines are not written; warnings and errors of the category and everything else are. A host configuration that sets the category back to Information re-opens the hole. The correlation id is only taken from the request header when it matches `[A-Za-z0-9._-]{1,64}`.

Export is off unless `OTEL_EXPORTER_OTLP_ENDPOINT` (environment variable or `--OTEL_EXPORTER_OTLP_ENDPOINT=` argument) is an absolute http or https URL; then tracing and metrics go to that OTLP endpoint. With no value nothing is registered and nothing connects out. An invalid value stops startup with a generic message that does not repeat it.

Probes: `GET /health/live` (process is up), `GET /health/startup` (200 once the database migration succeeded, or at once with in-memory stores; retries the migration, so a database that was down at boot recovers), `GET /health/ready` (stores respond, and a golden self-test recomputes an embedded Base scenario and compares levered IRR to 1e-9 and equity at close to 0.01; the result is cached for 60 seconds; a mismatch is a 503 without any figure).

Worker hardening: a worker renews its chunk lease every 50 cases (or every 20 seconds when cases are slow) and stops without completing when the lease was lost; a sweep plan is validated once per job per process (bounded cache of 64 jobs); stopping the service releases the chunk between cases without using up an attempt and waits at most 10 seconds.

## Containers and CI

`Dockerfile` builds one image (ASP.NET 10, non-root, the published API only) for both roles. `compose.yaml` runs a local stack: `db` (PostgreSQL 16 with a named volume), `api` (`Role=api`, jwt login, `Development` environment with a dev signing key, published on `127.0.0.1:5080`, change with `HUE_API_PORT`) and `worker` (`Role=worker`, listens on its own loopback only, publishes no port). `api` and `worker` run with a read-only root file system, a `/tmp` tmpfs, no capabilities, `no-new-privileges`, a restart policy and a 2 GB memory limit. The restart policy covers crashes and a Docker daemon restart only: Docker does not restart a container that was stopped by `docker stop` or `docker kill`.

Build and run (bash, from the repository root). Copy `.env.example` to `.env` (git-ignored) and fill in both values; there are no defaults and Compose refuses to start without them:

```
cp .env.example .env            # then set HUE_DB_PASSWORD (openssl rand -hex 24) and HUE_JWT_KEY (openssl rand -hex 32)
docker compose up -d --build
curl -sf http://127.0.0.1:5080/health/ready
TOKEN=$(docker compose exec -T api dotnet HotelUnderwriting.Api.dll dev-token --sub alice --roles analyst --deals d1)
curl -s -H "Authorization: Bearer $TOKEN" -H "X-Deal-Id: d1" -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" --data-binary @data/Hotel_Acquisition_Underwriting_Model.xlsx http://127.0.0.1:5080/v1/assumption-sets/excel
docker compose stop
```

The database password is set when the volume is first created: changing `HUE_DB_PASSWORD` later does not change it.

Smoke test: `bash scripts/smoke.sh` (needs Docker with Compose, curl and openssl). It builds and starts the stack as Compose project `hue-smoke` with generated throwaway secrets, checks the published ports and the hardening, expects 401 without a token, mints a token, publishes the reference workbook through the Excel route, runs one scenario and a four-case sweep that the `worker` service must finish, reads the results, stops and starts the stack, checks that the data is still there and that no secret is in the logs, and leaves the stack stopped. It also checks that neither generated secret appears in the image history or configuration. It removes no volume, network or image tag; on a repeat run the generated secrets differ, so Compose recreates the project's containers, the database volume stays and gets the new password. It needs `jq` or Python for reading JSON. `HUE_SMOKE_PROJECT` and `HUE_API_PORT` change the project name and port. Under GitHub Actions it masks the generated secrets in the log. Note that `docker compose config` and `docker inspect` show the secrets in clear text, so do not paste their output.

CI (`.github/workflows/ci.yml`, on push and pull request, `contents: read` only). **The workflow has never run on GitHub**: only its commands were run locally (Windows build and tests, a Linux container run for the Linux side, `scripts/smoke.sh`, `scripts/check-cli-figures.sh`):

| Job | What it checks |
|---|---|
| `test` (Ubuntu) | build with warnings as errors and all tests on .NET 8 and 10 with a PostgreSQL 16 service (`HUE_REQUIRE_PG=1`); test results uploaded on failure; `scripts/check-cli-figures.sh` (the three levered IRRs and 37/66); `scripts/check-vulnerable-packages.sh` (the one gate for known vulnerable packages: builds and tests pass `-p:NuGetAudit=false` so an advisory alone cannot fail them) |
| `test-windows` | build with warnings as errors and the tests without PostgreSQL (database tests are skipped) |
| `smoke` (Ubuntu) | image build and `scripts/smoke.sh` |

Not covered: registry push, image signing, SBOM, image scan, pinned base image digests, TLS, backups, rate limiting, malware scan of uploads, orchestration probes (Compose does not probe `api` or `worker`), and production identity (the stack uses the development signing key).

## Path to production

This repository is the calculation core, an Excel adapter, a CLI, the application layer, an HTTP API with PostgreSQL persistence, a sweep queue and workers, login with per-deal access, a hardened Excel upload, telemetry, a container image, a Compose stack and a CI workflow that has not run on GitHub yet. It is not production-ready. The target design (hexagonal core, content-addressed idempotent runs, immutable versioned assumptions, queue-based sweeps, append-only audit ledger, 24 architecture decisions, SLOs, threat model and a 13-item go-live gate) was written as planning documents that are not part of this submission; the "Not built" list below is what separates this repository from that target. One scenario run takes about 3.4 ms (about 1,336 runs/s on 8 cores), so scale is a fan-out and governance problem, not a compute problem.

Built so far: core, Excel adapter, CLI and reconciliation; application layer with content-addressed idempotent runs; refinance and hurdle-tier extensions; property tests; .NET 10 multi-targeting; the HTTP API (publish, runs, sweeps and jobs) with an OpenAPI file tested against the routes; PostgreSQL persistence with an append-only ledger; the sweep queue and workers; telemetry and health probes; bearer-token authentication with per-deal access; the hardened Excel upload; a container image, a Compose stack, a smoke script and a CI workflow; and three rounds of adversarial QA with the business rules and error handling that came out of them.

Not built: deployment and infrastructure as code, image publishing, signing, SBOM and image scan, dashboards, alerts and runbooks, a nightly golden-suite job, load tests (SLOs and recovery targets are proposed, not measured), backups and a restore drill, TLS, rate limiting, malware scanning of uploads, a worker queue for uploads (parsing is a child process of the API), consumer contract tests, and any run of the CI workflow on GitHub.

## Limitations

- Output workbook holds values, not live formulas. It was written by the engine and not opened in Excel here.
- Forward 12-month NOI is truncated at the 96-month horizon (see issue 4); hold periods over 84 months are rejected rather than extended.
- The engine needs every month of the horizon to fall inside the annual forecast years (2027-2036 in the supplied workbook); an acquisition date that pushes months past the last year is a 422.
- Attribution of reconciliation differences to workbook errors assumes error #3 exists only on the Downside sheet (found by the formula diff).
- Peak loan balance is taken as the maximum of opening and pre-payoff end-of-month balances; the workbook uses opening and closing balances. They agree for all three scenarios.
- XIRR returns the first root from the low end; for unusual cash flow patterns with several sign changes there can be more than one root.
- Refinance events and hurdle tiers can be set in code or in the JSON publish body, but not in the workbook: the workbook reader, CLI and output do not read or show them. The pref tier and base promote remain scalars rather than list items.
- Hurdle tiers accrue monthly while XIRR counts days, so an LP IRR lands up to about 0.3 pp above a hurdle; a hurdle balance is clipped at 0 (a later equity call restarts it).
- Engine-only invalid input (the rules that need an engine run, such as the hold period or an unknown driver) is answered by the engine after the store has been asked, so while the database is down such a set gets 503 instead of 422. The rules that need no engine run (everything in `AssumptionRules`) are answered first and stay 422.
- A 422 text may name the scenario (its name passed the character rules) and, for the engine's own messages, a number; it never quotes another client-supplied value.
- A name, code or section is limited to 100 UTF-16 units (a character outside the BMP counts as 2), and U+200C and U+200D (ZWNJ, ZWJ) are refused as format characters, so a few scripts that need them to render correctly cannot be used in names.
- The Excel upload is limited to 10 MB. There is no malware scan, no rate limit, no backup or restore drill, no TLS in the service, no image scan, no SBOM and no image signing.
- The GitHub Actions workflow has never run on GitHub.
- Docker does not restart a container stopped with `docker stop` or `docker kill` (the restart policy only covers crashes and daemon restarts).
- A NUL byte in a URL path is answered by Kestrel itself with its own empty 400 before the API runs (no problem+json body, no correlation id).
- The CLI does not apply the API's rules and may crash on inputs the API accepts, for example GP share 0 or 1, fees of 1, an exit cap rate of 1, an occupancy of 0, or scenario names containing `/` or `?` (they cannot be used as output file names).
- The repository has mixed LF and CRLF line endings in older files; no `.editorconfig` yet.
