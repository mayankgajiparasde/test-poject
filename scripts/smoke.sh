#!/usr/bin/env bash
# Builds and starts the Compose stack with throwaway secrets, drives it end to end, restarts it and leaves it stopped.
# Needs docker (with compose), curl, openssl and jq (or python). Settings: HUE_SMOKE_PROJECT (default hue-smoke), HUE_API_PORT (default 5080).
# It removes no volume, network or image tag. On a repeat run the throwaway secrets differ, so Compose recreates the
# project's containers (the volume stays) and the database gets the new password.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1 # Git Bash: leave container paths alone

project=${HUE_SMOKE_PROJECT:-hue-smoke}
export HUE_API_PORT=${HUE_API_PORT:-5080}
HUE_DB_PASSWORD=$(openssl rand -hex 24)
HUE_JWT_KEY=$(openssl rand -hex 32)
export HUE_DB_PASSWORD HUE_JWT_KEY
if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::add-mask::$HUE_DB_PASSWORD"; echo "::add-mask::$HUE_JWT_KEY"; fi
url=http://127.0.0.1:$HUE_API_PORT
workbook=data/Hotel_Acquisition_Underwriting_Model.xlsx
set_id=bddceda5e0021a3a43bdda20bbfc61f1fcb9cea6a737f13442280ad3993c2f99
base_run_key=974d28bf9084e5375243e7607dd1be3156eb5bf8d1892757e62b63d7c7be3929

dc() { docker compose -p "$project" "$@"; }
current="setup"
step() { current=$1; echo "== $1"; }
fail() { echo "$*" >&2; exit 1; }
finish() {
  local status=$?
  if [ "$status" -ne 0 ]; then
    echo "FAILED at step: $current (exit $status)" >&2
    dc logs --no-color --tail 100 >&2 || true
  fi
  dc stop >/dev/null 2>&1 || true
  exit "$status"
}
trap finish EXIT

# Sets $code and $body.
request() {
  local out
  out=$(curl -sS --max-time 90 -w '\n%{http_code}' "$@")
  code=${out##*$'\n'}
  body=${out%$'\n'*}
}
expect() { case " $* " in *" $code "*) echo "   HTTP $code" ;; *) fail "expected HTTP $*, got $code: $body" ;; esac; }
# field a.b: the value at that path from the top of $body (empty when absent), so a nested or repeated key cannot match.
# result_keys: how many items of $body have a 64-hex runKey.
if command -v jq >/dev/null; then
  field() { jq -r --arg p "$1" 'getpath($p | split(".")) // empty' <<<"$body"; }
  result_keys() { jq -r '[.items[] | select((.runKey // "") | test("^[0-9a-f]{64}$"))] | length' <<<"$body"; }
else
  py=""
  for c in python3 python; do if command -v "$c" >/dev/null && "$c" -c "import json" 2>/dev/null; then py=$c; break; fi; done
  [ -n "$py" ] || fail "jq or a working python is needed"
  field() { "$py" -c 'import json,sys,functools;r=functools.reduce(lambda a,k:a.get(k) if isinstance(a,dict) else None,sys.argv[1].split("."),json.load(sys.stdin));print("" if r is None else r)' "$1" <<<"$body" | tr -d ''; }
  result_keys() { "$py" -c 'import json,re,sys;print(sum(1 for i in json.load(sys.stdin)["items"] if re.fullmatch(r"[0-9a-f]{64}",i.get("runKey") or "")))' <<<"$body" | tr -d ''; }
fi
inspect() { docker inspect -f "$2" "$(dc ps -aq "$1")"; }
wait_ready() {
  for _ in $(seq 1 90); do
    if curl -sf --max-time 5 "$url/health/ready" >/dev/null; then echo "   ready"; return; fi
    sleep 1
  done
  fail "the api did not become ready"
}

step "start the database"
dc up -d --wait db
# A volume from an earlier run keeps the earlier password (psql connects over the local socket, which needs none).
echo "alter role hue password '$HUE_DB_PASSWORD'" | dc exec -T db psql -q -v ON_ERROR_STOP=1 -U hue -d hue

step "build and start the stack"
dc up -d --build
wait_ready

step "no generated secret in the image"
image=hue-underwriting:local
image_text=$(docker history --no-trunc "$image"; docker image inspect "$image")
[ -n "$image_text" ] || fail "no history or inspect output for $image"
for secret in "$HUE_DB_PASSWORD" "$HUE_JWT_KEY"; do
  if grep -qF -- "$secret" <<<"$image_text"; then fail "a generated secret appears in the history or configuration of $image"; fi
done
echo "   history and configuration of $image are clean"

step "only the api is published, on 127.0.0.1"
for s in db worker; do
  ports=$(inspect "$s" '{{json .HostConfig.PortBindings}}')
  echo "   $s $ports"
  [ "$ports" = "{}" ] || fail "$s publishes a port"
done
ports=$(inspect api '{{json .HostConfig.PortBindings}}')
echo "   api $ports"
[ "$ports" = "{\"8080/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"$HUE_API_PORT\"}]}" ] || fail "the api is not published on 127.0.0.1 only"
worker_log=$(dc logs --no-color worker)
listening=$(grep -o 'Now listening on: .*' <<<"$worker_log" | sort -u || true)
[ -n "$listening" ] || fail "the worker log has no 'Now listening on' line"
echo "   worker $listening"
[ "$listening" = "Now listening on: http://127.0.0.1:8080" ] || fail "the worker does not listen on loopback only"

step "api and worker are non-root on a read-only file system"
for s in api worker; do
  uid=$(dc exec -T "$s" id -u | tr -d '\r')
  hardening=$(inspect "$s" '{{.HostConfig.ReadonlyRootfs}} {{json .HostConfig.CapDrop}} {{json .HostConfig.SecurityOpt}} {{.HostConfig.Memory}}')
  echo "   $s uid=$uid $hardening"
  [ "$uid" != 0 ] || fail "$s runs as root"
  [ "$hardening" = 'true ["ALL"] ["no-new-privileges:true"] 2147483648' ] || fail "$s is not hardened as expected"
done

step "a request without a token gets 401"
request "$url/v1/jobs/none"
expect 401

step "mint a token"
token=$(dc exec -T api dotnet HotelUnderwriting.Api.dll dev-token --sub smoke --roles analyst --deals smoke | tr -d '\r')
[ -n "$token" ] || fail "no token"
auth=(-H "Authorization: Bearer $token")
json=(-H "Content-Type: application/json")

step "publish the reference workbook through the Excel route"
request "${auth[@]}" -H "X-Deal-Id: smoke" -H "Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" --data-binary "@$workbook" "$url/v1/assumption-sets/excel"
expect 200 201
[ "$(field id)" = "$set_id" ] || fail "unexpected assumption set id: $body"
echo "   set $set_id"

step "submit a run and read it"
request "${auth[@]}" "${json[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\"}" "$url/v1/runs"
expect 200
[ "$(field runKey)" = "$base_run_key" ] || fail "unexpected run key: $body"
request "${auth[@]}" "$url/v1/runs/$base_run_key"
expect 200
irr=$(field summary.leveredIrr)
echo "   run $base_run_key levered IRR $irr"
[[ $irr == 0.1838596* ]] || fail "unexpected levered IRR: $irr"

step "submit a sweep and wait for the worker"
request "${auth[@]}" "${json[@]}" -d "{\"assumptionSetId\":\"$set_id\",\"scenario\":\"Base\",\"grid\":[{\"path\":\"interestRate\",\"values\":[0.06,0.07]},{\"path\":\"exitCapRate\",\"values\":[0.07,0.075]}]}" "$url/v1/sweeps"
expect 202
job=$(field jobId)
for _ in $(seq 1 60); do
  request "${auth[@]}" "$url/v1/jobs/$job"
  case $(field status) in succeeded | failed) break ;; esac
  sleep 1
done
echo "   $body"
[ "$(field status)" = succeeded ] && [ "$(field completedCases)" = 4 ] && [ "$(field failedCases)" = 0 ] || fail "the sweep did not succeed"
worker_log=$(dc logs --no-color worker)
grep -q -- "$job" <<<"$worker_log" || fail "the worker service logged nothing about job $job"

step "read the sweep results"
request "${auth[@]}" "$url/v1/jobs/$job/results"
expect 200
keys=$(result_keys)
echo "   $keys results with a run key"
[ "$keys" = 4 ] || fail "expected 4 results with a run key: $body"

step "stop: api and worker exit with code 0"
started=$SECONDS
dc stop
echo "   stopped in $((SECONDS - started))s"
for s in api worker; do
  exit_code=$(inspect "$s" '{{.State.ExitCode}}')
  echo "   $s exit code $exit_code"
  [ "$exit_code" = 0 ] || fail "$s exited with $exit_code"
done

step "start again: data is still there and migrations rerun safely"
dc start
wait_ready
request "${auth[@]}" "$url/v1/assumption-sets/$set_id"
expect 200
request "${auth[@]}" "$url/v1/runs/$base_run_key"
expect 200
request "${auth[@]}" "$url/v1/jobs/$job"
expect 200
[ "$(field status)" = succeeded ] || fail "the job is no longer succeeded: $body"
versions=$(dc exec -T db psql -At -U hue -d hue -c 'select string_agg(version::text, chr(44) order by version) from schema_version' | tr -d '\r')
migrations=$(ls src/HotelUnderwriting.Persistence/Migrations/*.sql | wc -l)
expected=$(seq -s, 1 "$migrations")
echo "   schema versions $versions (expected $expected: one per migration file, no gaps)"
[ "$versions" = "$expected" ] || fail "schema_version is '$versions' but the $migrations migration files give '$expected'"

step "no secret and no migration failure in the logs"
logs=$(dc logs --no-color)
for secret in "$HUE_DB_PASSWORD" "$HUE_JWT_KEY" "$token"; do
  if grep -qF -- "$secret" <<<"$logs"; then
    current="a secret appears in the logs"
    trap - EXIT; dc stop >/dev/null 2>&1; echo "FAILED: $current" >&2; exit 1 # without printing the logs
  fi
done
[ -n "$logs" ] || fail "no logs to check"
if grep -q 'Database migration failed' <<<"$logs"; then fail "a database migration failed"; fi

step "stop and leave the stack stopped"
dc stop
for s in api worker; do
  exit_code=$(inspect "$s" '{{.State.ExitCode}}')
  echo "   $s exit code $exit_code"
  [ "$exit_code" = 0 ] || fail "$s exited with $exit_code"
done
echo "Smoke test passed."
