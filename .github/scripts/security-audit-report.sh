#!/usr/bin/env bash
# Turns `dotnet list <sln> package --vulnerable --include-transitive --format json` output into
# the rolling-issue body for the scheduled security audit (issue #50).
#
#   bash security-audit-report.sh <json-file>
#
# Exit codes (the dotnet command itself exits 0 whether or not it finds anything, so the caller
# needs a real signal):
#   0   no vulnerable packages; nothing on stdout
#   10  vulnerable packages found; the issue body (Markdown) is on stdout
#   *   anything else (unreadable file, invalid JSON) — the workflow must fail, not read it as clean
#
# Env: AUDIT_TIMESTAMP and AUDIT_RUN_URL are stamped into the body so a stale issue is obviously
# stale. Tested by security-audit-report.test.sh.
set -euo pipefail

json_file="${1:?usage: security-audit-report.sh <json-file>}"

# One row per (package, relation, version, severity, advisory): the same transitive advisory
# shows up once per project that pulls it in, which would otherwise mean five identical rows.
rows="$(jq -r '
  [ .projects[]
    | .frameworks[]?
    | ((.topLevelPackages? // [] | map(. + {relation: "direct"})),
       (.transitivePackages? // [] | map(. + {relation: "transitive"})))
    | .[]
    | { id, relation, version: .resolvedVersion } + (.vulnerabilities[]? | { severity, url: .advisoryurl })
  ]
  | unique
  | .[]
  | "| \(.id) | \(.relation) | \(.version) | \(.severity) | \(.url) |"
' "$json_file")"

[ -n "$rows" ] || exit 0

cat <<EOF
_Last updated: ${AUDIT_TIMESTAMP:-unknown} — [workflow run](${AUDIT_RUN_URL:-})_

Known vulnerabilities in the locked dependency graph, per the scheduled advisory audit:

| Package | Dependency | Resolved | Severity | Advisory |
| --- | --- | --- | --- | --- |
$rows

Reproduce locally:

\`\`\`
dotnet restore Mimir.slnx --locked-mode
dotnet list Mimir.slnx package --vulnerable --include-transitive --format json
\`\`\`

This issue is updated in place by \`.github/workflows/security-audit.yml\` on every run that
finds vulnerabilities. It is never auto-closed: close it once the advisories above have been
acted on, and a fresh one will be opened if new advisories appear later.
EOF

exit 10
