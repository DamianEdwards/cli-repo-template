#!/usr/bin/env bash
# Creates or updates a public immutable-style dev channel release for a specific dev version.

set -euo pipefail

VERSION="$1"
BUNDLE_DIR="$2"
DESCRIPTION="${3:-"Development channel build published from CI. Dev builds intentionally skip provenance verification during install/update."}"

TAG="v${VERSION}"
TITLE="v${VERSION}"

NOTES=$(cat <<EOF
## Development Channel

**Version:** ${VERSION}
**Commit:** ${GITHUB_SHA}
**Build:** [#${GITHUB_RUN_NUMBER}](${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID})

${DESCRIPTION}

- This release is public and installable as the **Dev** channel.
- Dev channel installs intentionally skip provenance verification by design.
EOF
)

if gh release view "$TAG" --repo "${GITHUB_REPOSITORY}" &>/dev/null; then
  gh release edit "$TAG" \
    --repo "${GITHUB_REPOSITORY}" \
    --prerelease \
    --latest=false \
    --title "$TITLE" \
    --notes "$NOTES"

  gh release upload "$TAG" \
    --repo "${GITHUB_REPOSITORY}" \
    "${BUNDLE_DIR}"/* \
    --clobber
else
  gh release create "$TAG" \
    --repo "${GITHUB_REPOSITORY}" \
    --target "${GITHUB_SHA}" \
    --prerelease \
    --latest=false \
    --title "$TITLE" \
    --notes "$NOTES" \
    "${BUNDLE_DIR}"/*
fi
