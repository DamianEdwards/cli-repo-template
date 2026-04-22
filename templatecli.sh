#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/templatecli"

if [[ -z "${TEMPLATECLI_HOME:-}" ]]; then
  export TEMPLATECLI_HOME="$SCRIPT_DIR/.templatecli-home"
fi

exec dotnet run --project "$PROJECT_DIR" --no-build -- "$@"
