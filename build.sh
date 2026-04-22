#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

exec dotnet build "$SCRIPT_DIR/src/templatecli/templatecli.csproj" --no-logo "$@"
