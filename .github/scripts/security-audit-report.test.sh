#!/usr/bin/env bash
# Tests for security-audit-report.sh, runnable anywhere bash + jq exist:
#
#   bash .github/scripts/security-audit-report.test.sh
#
# The security-audit workflow runs this before every real audit: the scheduled run is the
# only production execution of the report script, so a broken parser would otherwise fail
# silent-negative (no rows found because nothing parsed) rather than loud.
#
# Fixtures mirror `dotnet list <sln> package --vulnerable --include-transitive --format json`
# output captured from the .NET 10 SDK: a clean run emits projects with only a "path" (no
# "frameworks" key), and vulnerability entries use the lowercase "advisoryurl" property.
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/security-audit-report.sh"
fixtures="$here/testdata/security-audit"

export AUDIT_TIMESTAMP="2026-01-05 06:17 UTC"
export AUDIT_RUN_URL="https://github.com/smadam813/mimir/actions/runs/1234567890"

failures=0

fail() {
  echo "FAIL: $1" >&2
  failures=$((failures + 1))
}

assert_contains() { # haystack, needle, label
  case "$1" in
    *"$2"*) ;;
    *) fail "$3: expected output to contain: $2" ;;
  esac
}

# --- clean run: exit 0, nothing on stdout ---------------------------------------------------
out="$(bash "$script" "$fixtures/clean.json")"
status=$?
[ "$status" -eq 0 ] || fail "clean: expected exit 0, got $status"
[ -z "$out" ] || fail "clean: expected empty stdout, got: $out"

# --- vulnerabilities found: exit 10, issue body on stdout -----------------------------------
out="$(bash "$script" "$fixtures/found.json")"
status=$?
[ "$status" -eq 10 ] || fail "found: expected exit 10, got $status"

assert_contains "$out" "$AUDIT_TIMESTAMP" "found: timestamp"
assert_contains "$out" "$AUDIT_RUN_URL" "found: run URL"
assert_contains "$out" "| Package | Dependency | Resolved | Severity | Advisory |" "found: table header"
assert_contains "$out" "| Npgsql.EntityFrameworkCore.PostgreSQL | direct | 10.0.3 | High | https://github.com/advisories/GHSA-aaaa-1111-bbbb |" "found: direct row"
assert_contains "$out" "| System.Text.Json | transitive | 8.0.0 | Critical | https://github.com/advisories/GHSA-cc1t-3333-dddd |" "found: transitive critical row"
assert_contains "$out" "| System.Text.Json | transitive | 8.0.0 | High | https://github.com/advisories/GHSA-hh1t-2222-cccc |" "found: transitive high row"
# The spec (issue #50) wants the *exact* audit command for one-paste reproduction, so the
# body's command must match what the workflow runs, --format json included.
assert_contains "$out" "dotnet list Mimir.slnx package --vulnerable --include-transitive --format json" "found: repro command"

# The same System.Text.Json advisories appear in two projects in the fixture; the table
# dedups to one row per (package, relation, version, severity, advisory).
rows="$(printf '%s\n' "$out" | grep -c '^| System.Text.Json ')"
[ "$rows" -eq 2 ] || fail "found: expected 2 deduplicated System.Text.Json rows, got $rows"

# --- malformed input: neither 0 (clean) nor 10 (found) --------------------------------------
out="$(bash "$script" "$fixtures/does-not-exist.json" 2>/dev/null)"
status=$?
{ [ "$status" -ne 0 ] && [ "$status" -ne 10 ]; } || fail "missing file: expected a failure exit code, got $status"

out="$(printf 'not json' | bash "$script" /dev/stdin 2>/dev/null)"
status=$?
{ [ "$status" -ne 0 ] && [ "$status" -ne 10 ]; } || fail "invalid JSON: expected a failure exit code, got $status"

# --------------------------------------------------------------------------------------------
if [ "$failures" -gt 0 ]; then
  echo "$failures test(s) failed" >&2
  exit 1
fi
echo "security-audit-report tests passed"
