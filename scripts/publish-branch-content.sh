#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: publish-branch-content.sh <branch> <source-directory> <commit-message>" >&2
  exit 1
fi

BRANCH="$1"
SOURCE_DIR="$2"
COMMIT_MESSAGE="$3"

if [[ ! -d "$SOURCE_DIR" ]]; then
  echo "Source directory '$SOURCE_DIR' does not exist." >&2
  exit 1
fi

REPO_ROOT="$(git rev-parse --show-toplevel)"
TEMP_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/publish-branch-XXXXXX")"
WORKTREE_DIR="${TEMP_ROOT}/worktree"

cleanup() {
  if [[ -d "$WORKTREE_DIR" ]]; then
    git -C "$REPO_ROOT" worktree remove "$WORKTREE_DIR" --force >/dev/null 2>&1 || true
  fi
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT

git -C "$REPO_ROOT" fetch origin "$BRANCH" >/dev/null 2>&1 || true

if git -C "$REPO_ROOT" show-ref --verify --quiet "refs/remotes/origin/${BRANCH}"; then
  git -C "$REPO_ROOT" worktree add --detach "$WORKTREE_DIR" "origin/${BRANCH}" >/dev/null
  git -C "$WORKTREE_DIR" checkout -B "$BRANCH" "origin/${BRANCH}" >/dev/null
else
  git -C "$REPO_ROOT" worktree add --detach "$WORKTREE_DIR" HEAD >/dev/null
  git -C "$WORKTREE_DIR" checkout --orphan "$BRANCH" >/dev/null
fi

find "$WORKTREE_DIR" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +
cp -R "$SOURCE_DIR"/. "$WORKTREE_DIR"/

git -C "$WORKTREE_DIR" add -A

if git -C "$WORKTREE_DIR" diff --cached --quiet; then
  git -C "$WORKTREE_DIR" rev-parse HEAD
  exit 0
fi

git -C "$WORKTREE_DIR" -c user.name="${GIT_AUTHOR_NAME:-github-actions[bot]}" -c user.email="${GIT_AUTHOR_EMAIL:-41898282+github-actions[bot]@users.noreply.github.com}" commit -m "$COMMIT_MESSAGE" >/dev/null
git -C "$WORKTREE_DIR" push origin "HEAD:${BRANCH}" >/dev/null
git -C "$WORKTREE_DIR" rev-parse HEAD
