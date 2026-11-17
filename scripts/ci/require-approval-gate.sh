#!/usr/bin/env bash
# CI only: fails unless the given GitHub environment exists and has a required reviewer.
# GitHub silently creates a missing environment, without protection, the first time a job uses it,
# which would let a release or upload through with no approval. Run this before such jobs.
#   scripts/ci/require-approval-gate.sh production
set -euo pipefail
env_name="${1:?usage: require-approval-gate.sh ENVIRONMENT}"
rules=$(gh api "repos/$GITHUB_REPOSITORY/environments/$env_name" --jq '[.protection_rules[].type] | join(",")' 2>/dev/null || true)
if [[ "$rules" != *required_reviewers* ]]; then
  echo "::error::The '$env_name' environment must exist with a required reviewer. See CICD.md › One-time setup."
  exit 1
fi
echo "Approval gate '$env_name' is in place."
