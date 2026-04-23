#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/templatecli"
PROJECT_FILE="$PROJECT_DIR/templatecli.csproj"
COMMAND_PATH="$SCRIPT_DIR/templatecli.sh"

if [[ -z "${TEMPLATECLI_HOME:-}" ]]; then
  export TEMPLATECLI_HOME="$SCRIPT_DIR/.templatecli-home"
fi

get_shell_profile() {
  local shell_name
  shell_name=$(basename "${SHELL:-/bin/bash}")

  case "$shell_name" in
    zsh)  echo "${HOME}/.zshrc" ;;
    bash)
      if [[ -f "${HOME}/.bash_profile" ]]; then
        echo "${HOME}/.bash_profile"
      else
        echo "${HOME}/.bashrc"
      fi
      ;;
    fish) echo "${HOME}/.config/fish/config.fish" ;;
    *)    echo "${HOME}/.profile" ;;
  esac
}

profile_has_completion_entry() {
  local profile="$1"
  local script_path="$2"
  [[ -f "$profile" ]] && grep -Fq "$script_path" "$profile" 2>/dev/null
}

get_completion_shell() {
  case "$(basename "${SHELL:-/bin/bash}")" in
    bash) echo "bash" ;;
    zsh)  echo "zsh" ;;
    fish) echo "fish" ;;
    *)    return 1 ;;
  esac
}

register_shell_completion() {
  local completion_shell
  if ! completion_shell=$(get_completion_shell); then
    return 0
  fi

  local completion_dir="$TEMPLATECLI_HOME/completions"
  local completion_script_path="$completion_dir/templatecli-completions.${completion_shell}"
  local profile
  profile=$(get_shell_profile)
  local profile_dir
  profile_dir=$(dirname "$profile")

  mkdir -p "$completion_dir"
  "$COMMAND_PATH" completions script "$completion_shell" \
    --command-name templatecli \
    --command-name templatecli.sh \
    --command-name ./templatecli.sh \
    | sed 's/\r$//' \
    > "$completion_script_path"

  mkdir -p "$profile_dir"

  if ! profile_has_completion_entry "$profile" "$completion_script_path"; then
    local profile_entry
    if [[ "$completion_shell" == "fish" ]]; then
      profile_entry="test -f \"$completion_script_path\"; and source \"$completion_script_path\""
    else
      profile_entry="[ -f \"$completion_script_path\" ] && . \"$completion_script_path\""
    fi

    {
      echo ""
      echo "# Added by templatecli local build for shell completion"
      echo "$profile_entry"
    } >> "$profile"
  fi

  if [[ "${BASH_SOURCE[0]}" != "$0" && "$completion_shell" == "bash" ]]; then
    # shellcheck disable=SC1090
    . "$completion_script_path"
  fi
}

dotnet build "$PROJECT_FILE" --no-logo "$@"
register_shell_completion
