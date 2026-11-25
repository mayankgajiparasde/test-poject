#!/usr/bin/env bash
# Numbered walkthrough of every role and feature against a RUNNING stack. Prints PASS/FAIL per step (expected vs actual) and exits 0 only when all pass.
# It creates nothing but assumption sets, runs and sweep jobs (all idempotent: a repeat run finds them again) and removes nothing.
#
#   bash scripts/demo.sh            # run the walkthrough
#   bash scripts/demo.sh tokens     # only print `export TOKEN_ALICE=...` lines (use: eval "$(bash scripts/demo.sh tokens)")
#
# Settings (environment):
#   HUE_URL               base URL of the API                 (default http://127.0.0.1:5080)
#   HUE_COMPOSE_PROJECT   Compose project of the stack, e.g. hue-demo: tokens are minted inside its api container.
#                         Empty (default) = a local `dotnet run`: tokens are minted with `dotnet run --no-build`, which needs the SAME
#                         ASPNETCORE_ENVIRONMENT=Development, Auth__Issuer, Auth__Audience and Auth__DevSigningKey as the server in this shell.
#   HUE_JWT_KEY / Auth__DevSigningKey   only for the optional "user with no role" step (a token signed by hand with openssl); skipped without a key.
# Needs bash, curl, and jq or python. Tokens are valid for 1 hour and are the only secrets printed (only in `tokens` mode).
set -uo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1 # Git Bash: leave container paths alone
export PYTHONUTF8=1 # python reads the (UTF-8) answers the same way on Windows

url=${HUE_URL:-http://127.0.0.1:5080}
project=${HUE_COMPOSE_PROJECT:-}
container=${HUE_API_CONTAINER:-${project}-api-1}
workbook=data/Hotel_Acquisition_Underwriting_Model.xlsx
golden=src/HotelUnderwriting.Api/golden-base.json
xlsx=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
set_id=bddceda5e0021a3a43bdda20bbfc61f1fcb9cea6a737f13442280ad3993c2f99
base_run_key=974d28bf9084e5375243e7607dd1be3156eb5bf8d1892757e62b63d7c7be3929

die() { echo "$*" >&2; exit 2; }
command -v curl >/dev/null || die "curl is needed"
[ -f "$workbook" ] || die "run this from the repository (missing $workbook)"

# ---- tokens
mint() { # sub roles [deals]
  if [ -n "$project" ]; then
    docker exec "$container" dotnet HotelUnderwriting.Api.dll dev-token --sub "$1" --roles "$2" ${3:+--deals "$3"} --hours 1 | tr -d '\r'
  else
    dotnet run --no-build --project src/HotelUnderwriting.Api -- dev-token --sub "$1" --roles "$2" ${3:+--deals "$3"} --hours 1 | tr -d '\r'
  fi
}
b64u() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
mint_norole() { # a valid token without a roles claim, signed by hand (dev-token refuses to create one)
  local key=${HUE_JWT_KEY:-${Auth__DevSigningKey:-}}
  [ -n "$key" ] && command -v openssl >/dev/null || return 1
  local iss=${Auth__Issuer:-$([ -n "$project" ] && echo https://hue.local || echo https://dev.local)} aud=${Auth__Audience:-hue-api} now h p
  now=$(date +%s)
  h=$(printf '{"alg":"HS256","typ":"JWT"}' | b64u)
  p=$(printf '{"iss":"%s","aud":"%s","sub":"norole","nbf":%s,"exp":%s,"deals":["d1"]}' "$iss" "$aud" $((now - 60)) $((now + 3600)) | b64u)
  printf '%s.%s.%s' "$h" "$p" "$(printf '%s' "$h.$p" | openssl dgst -sha256 -hmac "$key" -binary | b64u)"
}

mode=${1:-run}
if [ "$mode" = tokens ]; then
  echo "export TOKEN_ALICE=$(mint alice analyst d1)"
  echo "export TOKEN_BOB=$(mint bob analyst d2)"
  echo "export TOKEN_RITA=$(mint rita reviewer d1)"
  echo "export TOKEN_ADA=$(mint ada admin)"
  if t=$(mint_norole); then echo "export TOKEN_NOROLE=$t"; fi
  exit 0
fi

# ---- http helpers (sets $code and $body)
request() {
  local out
  out=$(curl -sS --max-time 90 -w '\n%{http_code}' "$@" 2>&1) || { code=000; body=$out; return; }
  code=${out##*$'\n'}
  body=${out%$'\n'*}
}
if command -v jq >/dev/null; then
  field() { jq -r --arg p "$1" 'getpath($p | split(".")) // empty' <<<"$body" 2>/dev/null; }
else
  py=""
  for c in python3 python; do if command -v "$c" >/dev/null && "$c" -c "import json" 2>/dev/null; then py=$c; break; fi; done
  [ -n "$py" ] || die "jq or a working python is needed"
  field() { "$py" -c 'import json,sys,functools;r=functools.reduce(lambda a,k:a.get(k) if isinstance(a,dict) else None,sys.argv[1].split("."),json.load(sys.stdin));print("" if r is None else r)' "$1" <<<"$body" 2>/dev/null | tr -d '\r'; }
fi

n=0; passed=0; failed=0
verdict() { # title expected actual ok
  n=$((n + 1))
  if [ "$4" = 1 ]; then passed=$((passed + 1)); printf 'PASS %2d  %s\n         expected %s, actual %s\n' "$n" "$1" "$2" "$3"
  else failed=$((failed + 1)); printf 'FAIL %2d  %s\n         expected %s, actual %s\n         body: %.300s\n' "$n" "$1" "$2" "$3" "$body"; fi
}
# status TITLE "200 201" curl-args...   : one request, the status must be one of the expected ones
status() {
  local title=$1 want=$2; shift 2
  request "$@"
  case " $want " in *" $code "*) verdict "$title" "HTTP ${want// / or }" "HTTP $code" 1 ;; *) verdict "$title" "HTTP ${want// / or }" "HTTP $code" 0 ;; esac
}
# equals TITLE expected actual            : a value read from the last body
equals() { if [ "$2" = "$3" ]; then verdict "$1" "$2" "$3" 1; else verdict "$1" "$2" "$3" 0; fi; }
starts() { if [[ $3 == $2* ]]; then verdict "$1" "$2..." "$3" 1; else verdict "$1" "$2..." "$3" 0; fi; }

echo "== demo against $url ($([ -n "$project" ] && echo "compose project $project" || echo "local dotnet"))"
request "$url/health/live"
[ "$code" = 200 ] || die "nothing answers at $url/health/live (HTTP $code). Start the stack first, see the README, section 'Try it in 5 minutes'."

alice=$(mint alice analyst d1) || die "could not mint a token (compose: is the container $container running? local: same Auth__ environment as the server, built with dotnet build)"
bob=$(mint bob analyst d2); rita=$(mint rita reviewer d1); ada=$(mint ada admin)
[ -n "$alice" ] && [ -n "$bob" ] && [ -n "$rita" ] && [ -n "$ada" ] || die "a token is empty"
A=(-H "Authorization: Bearer $alice"); B=(-H "Authorization: Bearer $bob"); R=(-H "Authorization: Bearer $rita"); D=(-H "Authorization: Bearer $ada")
D1=(-H "X-Deal-Id: d1"); JSON=(-H "Content-Type: application/json"); X=(-H "Content-Type: $xlsx")

echo; echo "-- health, contract, login"
status "health: live, startup, ready need no token (live)" 200 "$url/health/live"
status "health: startup" 200 "$url/health/startup"
status "health: ready (database and golden self-test)" 200 "$url/health/ready"
status "OpenAPI document needs no token" 200 "$url/openapi/v1.json"
starts "OpenAPI is version 3.1" "3.1" "$(printf '%s' "$body" | grep -m1 -o '"openapi": *"[^"]*"' | sed 's/.*"\([^"]*\)"$/\1/')"
status "no token: 401" 401 "$url/v1/jobs/none"
status "garbage token: 401" 401 -H "Authorization: Bearer not.a.token" "$url/v1/jobs/none"

echo; echo "-- roles on publish"
status "reviewer rita cannot publish (Excel route): 403" 403 "${R[@]}" "${D1[@]}" "${X[@]}" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
status "analyst alice without X-Deal-Id: 400" 400 "${A[@]}" "${X[@]}" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
status "analyst alice into a deal that is not hers (d2): 403" 403 "${A[@]}" -H "X-Deal-Id: d2" "${X[@]}" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
status "analyst alice publishes the reference workbook, Excel route (201 first time, 200 on a repeat)" "200 201" "${A[@]}" "${D1[@]}" "${X[@]}" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
equals "assumption set id is the known content hash" "$set_id" "$(field id)"
status "alice publishes the embedded Base scenario on the JSON route" "200 201" "${A[@]}" "${D1[@]}" "${JSON[@]}" --data-binary "@$golden" "$url/v1/assumption-sets"

echo; echo "-- run and read"
status "alice runs Base (200)" 200 "${A[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"
equals "run key of Base" "$base_run_key" "$(field runKey)"
status "alice reads the run" 200 "${A[@]}" "$url/v1/runs/$base_run_key?include=monthly,proforma"
starts "levered IRR of Base is 18.3860% (0.1838596...)" "0.1838596" "$(field summary.leveredIrr)"
status "alice runs a scenario that does not exist: 404" 404 "${A[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Nope\"}" "$url/v1/runs"
status "alice runs with the wrong deal header (d2): 403" 403 "${A[@]}" -H "X-Deal-Id: d2" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"

echo; echo "-- sweep"
status "alice submits a 2 x 2 sweep (202)" 202 "${A[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\",\"grid\":[{\"path\":\"interestRate\",\"values\":[0.06,0.07]},{\"path\":\"exitCapRate\",\"values\":[0.07,0.075]}]}" "$url/v1/sweeps"
job=$(field jobId)
st=""
for _ in $(seq 1 90); do
  request "${A[@]}" "$url/v1/jobs/$job"; st=$(field status)
  case $st in succeeded | failed) break ;; esac
  sleep 1
done
equals "the job finishes (a worker runs it)" "succeeded" "$st"
equals "four cases completed, none failed" "4/0" "$(field completedCases)/$(field failedCases)"
status "alice reads the results" 200 "${A[@]}" "$url/v1/jobs/$job/results"
if command -v jq >/dev/null; then keys=$(jq -r '[.items[] | select((.runKey // "") | test("^[0-9a-f]{64}$"))] | length' <<<"$body")
else keys=$("$py" -c 'import json,re,sys;print(sum(1 for i in json.load(sys.stdin)["items"] if re.fullmatch(r"[0-9a-f]{64}",i.get("runKey") or "")))' <<<"$body" | tr -d '\r'); fi
equals "four results with a run key" 4 "$keys"

echo; echo "-- another deal, reviewer, admin, no role"
status "bob (analyst of d2) cannot see d1's set: 404" 404 "${B[@]}" "$url/v1/assumption-sets/$set_id"
status "bob cannot see d1's run: 404" 404 "${B[@]}" "$url/v1/runs/$base_run_key"
status "bob cannot see d1's job: 404" 404 "${B[@]}" "$url/v1/jobs/$job"
status "bob cannot run d1's set: 404" 404 "${B[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"
status "reviewer rita reads d1's set: 200" 200 "${R[@]}" "$url/v1/assumption-sets/$set_id"
status "rita reads d1's run: 200" 200 "${R[@]}" "$url/v1/runs/$base_run_key"
status "rita reads d1's job results: 200" 200 "${R[@]}" "$url/v1/jobs/$job/results"
status "rita cannot run: 403" 403 "${R[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"
status "rita cannot sweep: 403" 403 "${R[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\",\"grid\":[{\"path\":\"promote\",\"values\":[0.2]}]}" "$url/v1/sweeps"
status "admin ada sees d1's set: 200" 200 "${D[@]}" "$url/v1/assumption-sets/$set_id"
status "ada sees d1's run: 200" 200 "${D[@]}" "$url/v1/runs/$base_run_key"
status "ada sees d1's job: 200" 200 "${D[@]}" "$url/v1/jobs/$job"
status "ada runs Base in deal d1: 200" 200 "${D[@]}" "${D1[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"
if norole=$(mint_norole); then
  status "a valid token without any role: 403 on a read" 403 -H "Authorization: Bearer $norole" "$url/v1/assumption-sets/$set_id"
  status "a valid token without any role: 403 on publish" 403 -H "Authorization: Bearer $norole" "${D1[@]}" "${X[@]}" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
else
  echo "SKIP     no-role user: set HUE_JWT_KEY (Compose) or Auth__DevSigningKey (local) to the server's key to run these two steps"
fi

echo; echo "-- bad and hostile input"
status "Excel route: bytes that are not a workbook: 422" 422 "${A[@]}" "${D1[@]}" "${X[@]}" --data-binary "this is not a zip file" "$url/v1/assumption-sets/excel"
status "Excel route: wrong content type: 415" 415 "${A[@]}" "${D1[@]}" -H "Content-Type: text/plain" --data-binary "x" "$url/v1/assumption-sets/excel"
status "JSON route: malformed JSON: 400" 400 "${A[@]}" "${D1[@]}" "${JSON[@]}" -d '{not json' "$url/v1/assumption-sets"
status "JSON route: charset the body cannot be read with: 415" 415 "${A[@]}" "${D1[@]}" -H "Content-Type: application/json; charset=nonsense" -d '{}' "$url/v1/assumption-sets"
bad=$(sed 's/"purchasePrice":[0-9.eE+-]*/"purchasePrice":0/' "$golden")
status "JSON route: purchase price 0 breaks a rule: 422" 422 "${A[@]}" "${D1[@]}" "${JSON[@]}" -d "$bad" "$url/v1/assumption-sets"
status "sweep with an unknown override path: 422" 422 "${A[@]}" "${JSON[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\",\"cases\":[{\"overrides\":[{\"path\":\"nope\",\"value\":1}]}]}" "$url/v1/sweeps"
status "unknown set id: 404" 404 "${A[@]}" "$url/v1/assumption-sets/does-not-exist"

echo
echo "== $passed passed, $failed failed (of $n)"
[ "$failed" -eq 0 ]
