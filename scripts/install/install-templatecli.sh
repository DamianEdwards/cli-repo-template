#!/usr/bin/env bash
# install-templatecli.sh — Linux & macOS installer for templatecli
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/example/templatecli/install-scripts/install.sh | bash -s -- [options]
#
# Options:
#   --quality <Dev|PreRelease|Stable>   Release quality (default: Stable)
#   --force                             Skip confirmation prompts
#   --target-path <path>                Install directory (default: ~/.templatecli/bin)
#   --no-update-path                    Do not update PATH in shell profile
#   --repository <owner/repo>           Repository to download from (default: example/templatecli)
#   --skip-provenance                   Skip artifact attestation verification (otherwise requires cosign)
#   --verbose                           Enable verbose output

set -euo pipefail

# ─── Defaults ────────────────────────────────────────────────────────────────

QUALITY="Stable"
FORCE=false
TARGET_PATH="${HOME}/.templatecli/bin"
UPDATE_PATH=true
REPOSITORY="example/templatecli"
SKIP_PROVENANCE=false
VERBOSE=false

# ─── Argument parsing ────────────────────────────────────────────────────────

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quality)
            QUALITY="$2"
            shift 2
            ;;
        --force)
            FORCE=true
            shift
            ;;
        --target-path)
            TARGET_PATH="$2"
            shift 2
            ;;
        --no-update-path)
            UPDATE_PATH=false
            shift
            ;;
        --repository)
            REPOSITORY="$2"
            shift 2
            ;;
        --skip-provenance)
            SKIP_PROVENANCE=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        -h|--help)
            cat <<'HELPTEXT'
install-templatecli.sh — Linux & macOS installer for templatecli
Usage:
  curl -fsSL https://raw.githubusercontent.com/example/templatecli/install-scripts/install.sh | bash -s -- [options]

Options:
  --quality <Dev|PreRelease|Stable>   Release quality (default: Stable)
  --force                             Skip confirmation prompts
  --target-path <path>                Install directory (default: ~/.templatecli/bin)
  --no-update-path                    Do not update PATH in shell profile
  --repository <owner/repo>           Repository to download from (default: example/templatecli)
  --skip-provenance                   Skip artifact attestation verification (otherwise requires cosign)
  --verbose                           Enable verbose output
HELPTEXT
            exit 0
            ;;
        *)
            echo "Error: Unknown option '$1'." >&2
            exit 1
            ;;
    esac
done

case "$QUALITY" in
    Dev|PreRelease|Stable) ;;
    *) echo "Error: --quality must be Dev, PreRelease, or Stable." >&2; exit 1 ;;
esac

# ─── Helpers ─────────────────────────────────────────────────────────────────

log_verbose() {
    if [[ "$VERBOSE" == true ]]; then
        echo "[verbose] $*" >&2
    fi
}

die() {
    echo "Error: $*" >&2
    exit 1
}

status_step() {
    local message="$1"
    shift
    printf '%s... ' "$message"
    "$@"
    echo 'done'
}

# ─── Prerequisites ───────────────────────────────────────────────────────────

assert_jq_available() {
    if ! command -v jq &>/dev/null; then
        echo ''
        echo 'Error: jq is required but was not found on PATH.' >&2
        echo ''
        echo 'Install it from: https://jqlang.github.io/jq/download/' >&2
        echo '  macOS:  brew install jq' >&2
        echo '  Ubuntu: sudo apt install jq' >&2
        echo ''
        exit 1
    fi
}

assert_curl_available() {
    if ! command -v curl &>/dev/null; then
        echo ''
        echo 'Error: curl is required for attestation bundle downloads but was not found on PATH.' >&2
        echo ''
        exit 1
    fi
}

print_cosign_install_instructions() {
    local os
    os=$(uname -s)

    if [[ "$os" == "Darwin" ]]; then
        echo 'Install Cosign with Homebrew:' >&2
        echo '  brew install cosign' >&2
        echo 'More options: https://docs.sigstore.dev/cosign/system_config/installation/' >&2
        return
    fi

    if command -v apt-get &>/dev/null; then
        echo 'Ubuntu/Debian: install the latest Cosign .deb package from the GitHub releases page, then run:' >&2
        echo '  sudo dpkg -i ./cosign_<version>_amd64.deb' >&2
        echo 'Releases: https://github.com/sigstore/cosign/releases/latest' >&2
        return
    fi

    if command -v dnf &>/dev/null; then
        echo 'Fedora/RHEL (dnf): install the latest Cosign .rpm package from the GitHub releases page, then run:' >&2
        echo '  sudo dnf install ./cosign-<version>-1.x86_64.rpm' >&2
        echo 'Releases: https://github.com/sigstore/cosign/releases/latest' >&2
        return
    fi

    if command -v yum &>/dev/null; then
        echo 'RHEL/CentOS (yum): install the latest Cosign .rpm package from the GitHub releases page, then run:' >&2
        echo '  sudo yum install ./cosign-<version>-1.x86_64.rpm' >&2
        echo 'Releases: https://github.com/sigstore/cosign/releases/latest' >&2
        return
    fi

    if command -v pacman &>/dev/null; then
        echo 'Arch Linux:' >&2
        echo '  sudo pacman -S cosign' >&2
        return
    fi

    if command -v apk &>/dev/null; then
        echo 'Alpine Linux:' >&2
        echo '  sudo apk add cosign' >&2
        return
    fi

    if command -v nix-env &>/dev/null; then
        echo 'Nix/NixOS:' >&2
        echo '  nix-env -iA nixpkgs.cosign' >&2
        return
    fi

    if command -v brew &>/dev/null; then
        echo 'Homebrew/Linuxbrew:' >&2
        echo '  brew install cosign' >&2
        return
    fi

    echo 'Install Cosign using a package manager or release binary:' >&2
    echo '  https://docs.sigstore.dev/cosign/system_config/installation/' >&2
    echo '  https://github.com/sigstore/cosign/releases/latest' >&2
}

assert_cosign_available() {
    if command -v cosign &>/dev/null; then
        return
    fi

    echo '' >&2
    echo 'Error: cosign is required for artifact attestation verification but was not found on PATH.' >&2
    echo '' >&2
    print_cosign_install_instructions
    echo '' >&2
    exit 1
}

# ─── GitHub API helpers ──────────────────────────────────────────────────────

invoke_github_api() {
    local uri="$1"
    local temp_file
    temp_file=$(mktemp)

    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    local curl_args=(
        -sS
        -L
        -H "Accept: application/vnd.github+json"
        -H "X-GitHub-Api-Version: 2022-11-28"
        -A "templatecli-install-script"
    )
    if [[ -n "$token" ]]; then
        curl_args+=(-H "Authorization: Bearer ${token}")
    fi

    local status_code
    status_code=$(curl "${curl_args[@]}" -o "$temp_file" -w "%{http_code}" "$uri") || {
        local curl_exit=$?
        local output=""
        if [[ -f "$temp_file" ]]; then
            output=$(cat "$temp_file")
            rm -f "$temp_file"
        fi
        die "GitHub API request failed for '$uri' (curl exit ${curl_exit}): ${output}"
    }

    local output=""
    if [[ -f "$temp_file" ]]; then
        output=$(cat "$temp_file")
        rm -f "$temp_file"
    fi

    if [[ "$status_code" == "404" ]]; then
        echo ""
        return
    fi

    if [[ ! "$status_code" =~ ^2[0-9][0-9]$ ]]; then
        die "GitHub API request failed for '$uri' (HTTP ${status_code}): ${output}"
    fi

    if [[ -z "${output:-}" ]]; then
        echo ""
        return
    fi

    echo "$output"
}

invoke_github_asset_download() {
    local asset_url="$1"
    local asset_name="$2"
    local dest_dir="$3"
    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    local curl_args=(-fsSL -A "templatecli-install-script")
    if [[ -n "$token" ]]; then
        curl_args+=(-H "Authorization: Bearer ${token}")
    fi

    mkdir -p "$dest_dir"
    curl "${curl_args[@]}" -o "${dest_dir}/${asset_name}" "$asset_url" \
        || die "Failed to download asset '$asset_name' from '$asset_url'."
}

# ─── Release resolution ─────────────────────────────────────────────────────

get_release_by_tag() {
    local repo="$1"
    local tag="$2"
    local uri="https://api.github.com/repos/${repo}/releases/tags/${tag}"
    invoke_github_api "$uri"
}

get_release_asset_url() {
    local release_json="$1"
    local asset_name="$2"

    echo "$release_json" | jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .browser_download_url // empty' | head -n 1
}

has_release_asset() {
    local release_json="$1"
    local asset_name="$2"

    local url
    url=$(get_release_asset_url "$release_json" "$asset_name")
    [[ -n "$url" ]]
}

get_release_tag() {
    local release_json="$1"
    echo "$release_json" | jq -r '.tag_name // empty'
}

get_release_name() {
    local release_json="$1"
    echo "$release_json" | jq -r '.name // empty'
}

get_release_for_quality() {
    local repo="$1"
    local selected_quality="$2"
    local asset_name="$3"

    local all_releases
    all_releases=$(invoke_github_api "https://api.github.com/repos/${repo}/releases?per_page=100")

    local candidate_release=""
    local release_count
    release_count=$(echo "$all_releases" | jq 'length')

    for (( i = 0; i < release_count; i++ )); do
        local release
        release=$(echo "$all_releases" | jq -c ".[$i]")

        local is_draft
        is_draft=$(echo "$release" | jq -r '.draft')
        if [[ "$is_draft" == "true" ]]; then
            continue
        fi

        local tag_name
        tag_name=$(echo "$release" | jq -r '.tag_name')
        if [[ "$tag_name" == install-scripts-v* ]]; then
            continue
        fi

        if ! has_release_asset "$release" "$asset_name"; then
            continue
        fi

        local is_prerelease
        is_prerelease=$(echo "$release" | jq -r '.prerelease')

        local is_dev_release=false
        if [[ "$tag_name" == *".dev."* || "$tag_name" == *"-dev."* ]]; then
            is_dev_release=true
        fi

        if [[ "$selected_quality" == "Dev" ]]; then
            if [[ "$is_prerelease" == "true" && "$is_dev_release" == "true" ]]; then
                echo "$release"
                return
            fi
            continue
        fi

        if [[ "$selected_quality" == "PreRelease" ]]; then
            if [[ "$is_prerelease" == "true" && "$is_dev_release" == "false" ]]; then
                echo "$release"
                return
            fi
            continue
        fi

        if [[ "$selected_quality" == "Stable" && "$is_prerelease" == "true" ]]; then
            if [[ "$is_dev_release" == "false" && -z "$candidate_release" ]]; then
                candidate_release="$release"
            fi
            continue
        fi

        echo "$release"
        return
    done

    if [[ "$selected_quality" == "Stable" && -n "$candidate_release" ]]; then
        echo "Warning: No stable release containing '$asset_name' was found. Falling back to latest prerelease." >&2
        echo "$candidate_release"
        return
    fi

    die "No '$selected_quality' release containing '$asset_name' was found in '$repo'."
}

# ─── Checksum verification ──────────────────────────────────────────────────

get_expected_sha256() {
    local checksums_path="$1"
    local asset_name="$2"

    local line
    line=$(grep -E "\s\*?${asset_name}$" "$checksums_path" | head -n 1)

    if [[ -z "$line" ]]; then
        die "checksums.txt did not contain an entry for '$asset_name'."
    fi

    local hash
    hash=$(echo "$line" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')

    if [[ ! "$hash" =~ ^[0-9a-f]{64}$ ]]; then
        die "Invalid checksum line format for '$asset_name' in checksums.txt."
    fi

    echo "$hash"
}

assert_archive_integrity() {
    local archive_path="$1"
    local asset_name="$2"
    local checksums_path="$3"
    local release_metadata_path="${4:-}"

    log_verbose "Validating archive SHA256 for '$asset_name' using '$checksums_path'."
    local expected_sha
    expected_sha=$(get_expected_sha256 "$checksums_path" "$asset_name")

    local actual_sha
    actual_sha=$(shasum -a 256 "$archive_path" | awk '{print $1}' | tr '[:upper:]' '[:lower:]')

    log_verbose "checksums.txt expected SHA256 for '$asset_name': '$expected_sha'."
    log_verbose "Actual SHA256 for '$archive_path': '$actual_sha'."

    if [[ "$expected_sha" != "$actual_sha" ]]; then
        die "SHA256 mismatch for '$asset_name'. Expected '$expected_sha' but got '$actual_sha'."
    fi

    if [[ -n "$release_metadata_path" && -f "$release_metadata_path" ]]; then
        log_verbose "Validating release metadata for '$asset_name' using '$release_metadata_path'."
        local metadata_sha
        metadata_sha=$(jq -r --arg name "$asset_name" '.assets[] | select(.name == $name) | .sha256 // empty' "$release_metadata_path" | head -n 1 | tr '[:upper:]' '[:lower:]')

        if [[ -z "$metadata_sha" ]]; then
            die "release-metadata.json did not contain an entry for '$asset_name'."
        fi

        log_verbose "release-metadata.json SHA256 for '$asset_name': '$metadata_sha'."
        if [[ "$metadata_sha" != "$expected_sha" ]]; then
            die "release-metadata.json SHA256 for '$asset_name' did not match checksums.txt."
        fi
    fi

    echo "$actual_sha"
}

# ─── Attestation verification ───────────────────────────────────────────────

escape_regex() {
    printf '%s' "$1" | sed -e 's/[.[\*^$()+?{}|\\]/\\&/g'
}

compute_sha256() {
    local file_path="$1"
    shasum -a 256 "$file_path" | awk '{print tolower($1)}'
}

download_attestation_bundle() {
    local file_path="$1"
    local repo="$2"
    local bundle_path="$3"

    local owner repo_name
    IFS='/' read -r owner repo_name <<< "$repo"
    if [[ -z "${owner:-}" || -z "${repo_name:-}" ]]; then
        die "Repository '$repo' must be in owner/name format to download attestation bundles."
    fi

    local digest
    digest=$(compute_sha256 "$file_path")
    if [[ -z "$digest" ]]; then
        die "Failed to compute SHA256 for '$file_path'."
    fi

    local url="https://api.github.com/repos/${owner}/${repo_name}/dependency-graph/artifact-attestations/sha256/${digest}/bundle"
    local response_path="${bundle_path}.response"
    local http_code
    local token="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
    local curl_args=(
        -sS
        -L
        -o "$response_path"
        -w "%{http_code}"
        -H "Accept: application/vnd.github+json"
        -H "X-GitHub-Api-Version: 2022-11-28"
    )

    if [[ -n "$token" ]]; then
        curl_args+=(-H "Authorization: Bearer ${token}")
    fi

    http_code=$(curl "${curl_args[@]}" "$url") || {
        rm -f "$response_path"
        die "Failed to download GitHub attestation bundle for sha256:${digest}."
    }

    case "$http_code" in
        200)
            mv "$response_path" "$bundle_path"
            ;;
        401|403)
            local response_body=""
            response_body=$(cat "$response_path" 2>/dev/null || true)
            rm -f "$response_path"
            die "GitHub denied access to the attestation bundle for sha256:${digest}. Set GITHUB_TOKEN or GH_TOKEN for private repositories or when public API rate limits are exceeded. ${response_body}"
            ;;
        404)
            rm -f "$response_path"
            die "No GitHub attestation bundle was found for sha256:${digest} in '${repo}'."
            ;;
        *)
            local response_body=""
            response_body=$(cat "$response_path" 2>/dev/null || true)
            rm -f "$response_path"
            die "GitHub attestation bundle request failed with HTTP ${http_code}: ${response_body}"
            ;;
    esac
}

assert_artifact_attestation() {
    local file_path="$1"
    local repo="$2"
    local description="${3:-artifact}"
    local expected_ref="$4"

    log_verbose "Verifying $description attestation for '$file_path' from '$repo' at '$expected_ref'."
    local bundle_path="${file_path}.sigstore.json"
    download_attestation_bundle "$file_path" "$repo" "$bundle_path"

    local repo_pattern
    repo_pattern=$(escape_regex "$repo")
    local last_error=""
    local workflow_pattern
    workflow_pattern=$(escape_regex "release.yml")
    local expected_ref_pattern
    expected_ref_pattern=$(escape_regex "$expected_ref")
    local identity_pattern="^https://github\\.com/${repo_pattern}/\\.github/workflows/${workflow_pattern}@${expected_ref_pattern}$"
    local output
    output=$(cosign verify-blob-attestation \
        --bundle "$bundle_path" \
        --new-bundle-format \
        --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
        --certificate-identity-regexp "$identity_pattern" \
        "$file_path" 2>&1) || last_error="$output"

    if [[ -z "${last_error:-}" ]]; then
        log_verbose "Artifact attestation verification succeeded for $description '$file_path'."
        rm -f "$bundle_path"
        return
    fi

    rm -f "$bundle_path"
    die "Artifact attestation verification failed for $description '$file_path': ${last_error:-Cosign did not find a matching attestation bundle.}"
}

# ─── Platform detection ─────────────────────────────────────────────────────

get_platform() {
    local os
    os=$(uname -s)
    case "$os" in
        Linux)  echo "linux" ;;
        Darwin) echo "osx" ;;
        *)      die "Unsupported operating system '$os'. Only Linux and macOS are supported." ;;
    esac
}

get_architecture() {
    local arch
    arch=$(uname -m)
    case "$arch" in
        x86_64|amd64)  echo "x64" ;;
        arm64|aarch64) echo "arm64" ;;
        *)             die "Unsupported architecture '$arch'. Only x64 and arm64 are supported." ;;
    esac
}

# ─── Version helpers ─────────────────────────────────────────────────────────

get_templatecli_version_string() {
    local binary_path="$1"
    local output
    local exit_code=0

    output=$("$binary_path" --version 2>/dev/null) || exit_code=$?

    if [[ $exit_code -eq 0 && -n "$output" ]]; then
        local version
        version=$(echo "$output" | head -n 1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.\-]+)?' | head -n 1)
        if [[ -n "$version" ]]; then
            echo "$version"
            return
        fi
    fi

    die "Could not determine version for '$binary_path'."
}

# Compares two semantic versions. Returns via stdout:
#   "1"  if left > right
#   "0"  if left == right
#   "-1" if left < right
compare_semantic_version() {
    local left="$1"
    local right="$2"

    # Parse major.minor.patch (optional .build) (-prerelease)
    local left_core left_pre right_core right_pre

    left_core="${left%%-*}"
    right_core="${right%%-*}"

    if [[ "$left" == *-* ]]; then
        left_pre="${left#*-}"
    else
        left_pre=""
    fi

    if [[ "$right" == *-* ]]; then
        right_pre="${right#*-}"
    else
        right_pre=""
    fi

    # Compare major.minor.patch numerically
    IFS='.' read -r -a left_parts <<< "$left_core"
    IFS='.' read -r -a right_parts <<< "$right_core"

    for i in 0 1 2; do
        local l=$((10#${left_parts[$i]:-0}))
        local r=$((10#${right_parts[$i]:-0}))
        if (( l > r )); then echo "1"; return; fi
        if (( l < r )); then echo "-1"; return; fi
    done

    # Both have no prerelease — equal
    if [[ -z "$left_pre" && -z "$right_pre" ]]; then
        echo "0"
        return
    fi

    # No prerelease wins over prerelease
    if [[ -z "$left_pre" ]]; then echo "1"; return; fi
    if [[ -z "$right_pre" ]]; then echo "-1"; return; fi

    # Segment-by-segment prerelease comparison (per SemVer spec)
    IFS='.' read -r -a left_ids <<< "$left_pre"
    IFS='.' read -r -a right_ids <<< "$right_pre"
    local max_len=${#left_ids[@]}
    if (( ${#right_ids[@]} > max_len )); then
        max_len=${#right_ids[@]}
    fi

    for (( i = 0; i < max_len; i++ )); do
        # Fewer identifiers = lower precedence
        if (( i >= ${#left_ids[@]} )); then echo "-1"; return; fi
        if (( i >= ${#right_ids[@]} )); then echo "1"; return; fi

        local l_id="${left_ids[$i]}"
        local r_id="${right_ids[$i]}"
        local l_is_num=false r_is_num=false

        [[ "$l_id" =~ ^[0-9]+$ ]] && l_is_num=true
        [[ "$r_id" =~ ^[0-9]+$ ]] && r_is_num=true

        if [[ "$l_is_num" == true && "$r_is_num" == true ]]; then
            local l_num=$((10#$l_id))
            local r_num=$((10#$r_id))
            if (( l_num > r_num )); then echo "1"; return; fi
            if (( l_num < r_num )); then echo "-1"; return; fi
            continue
        fi

        # Numeric identifiers always have lower precedence than alphanumeric
        if [[ "$l_is_num" == true && "$r_is_num" == false ]]; then echo "-1"; return; fi
        if [[ "$l_is_num" == false && "$r_is_num" == true ]]; then echo "1"; return; fi

        # Lexicographic comparison for alphanumeric identifiers
        if [[ "$l_id" > "$r_id" ]]; then echo "1"; return; fi
        if [[ "$l_id" < "$r_id" ]]; then echo "-1"; return; fi
    done

    echo "0"
}

# ─── Release label ───────────────────────────────────────────────────────────

get_release_status_label() {
    local selected_quality="$1"
    local release_json="$2"

    local is_prerelease
    is_prerelease=$(echo "$release_json" | jq -r '.prerelease')

    local release_label
    case "$selected_quality" in
        Stable)
            if [[ "$is_prerelease" == "true" ]]; then
                release_label="latest prerelease (no stable release available)"
            else
                release_label="latest stable release"
            fi
            ;;
        PreRelease) release_label="latest prerelease" ;;
        Dev)        release_label="latest development build" ;;
        *)          release_label="release" ;;
    esac

    local tag_name
    tag_name=$(get_release_tag "$release_json")
    local name
    name=$(get_release_name "$release_json")

    local release_version="${tag_name:-${name:-unknown version}}"
    echo "${release_label} (${release_version})"
}

# ─── PATH management ────────────────────────────────────────────────────────

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

try_symlink_to_existing_path_dir() {
    local binary_path="$1"
    local binary_name
    binary_name=$(basename "$binary_path")

    # Only attempt on Linux — macOS has no standard user-writable PATH directory
    if [[ "$(uname -s)" != "Linux" ]]; then
        log_verbose "Skipping symlink strategy (not Linux)."
        return 1
    fi

    # Well-known user bin directories, in preference order
    local candidate_dirs=("${HOME}/.local/bin" "${HOME}/bin")

    for candidate in "${candidate_dirs[@]}"; do
        if ! echo ":${PATH}:" | grep -q ":${candidate}:"; then
            log_verbose "'$candidate' is not on PATH; skipping."
            continue
        fi

        if [[ ! -d "$candidate" ]]; then
            log_verbose "'$candidate' is on PATH but does not exist; creating it."
            if ! mkdir -p "$candidate" 2>/dev/null; then
                log_verbose "Failed to create '$candidate'; skipping."
                continue
            fi
        fi

        local link_path="${candidate}/${binary_name}"

        # If something already exists at the link path, handle it
        if [[ -e "$link_path" || -L "$link_path" ]]; then
            if [[ -L "$link_path" ]]; then
                local existing_target
                existing_target=$(readlink -f "$link_path" 2>/dev/null || readlink "$link_path")
                local resolved_binary
                resolved_binary=$(readlink -f "$binary_path" 2>/dev/null || readlink "$binary_path" || echo "$binary_path")
                if [[ "$existing_target" == "$resolved_binary" ]]; then
                    log_verbose "Symlink '$link_path' already points to '$binary_path'."
                    echo "templatecli is available via existing symlink '$link_path'."
                    return 0
                fi
                log_verbose "Updating symlink '$link_path' (was '$existing_target', now '$binary_path')."
                if ! ln -sf "$binary_path" "$link_path" 2>/dev/null; then
                    log_verbose "Failed to update symlink '$link_path'; skipping."
                    continue
                fi
            else
                # Regular file or directory — don't overwrite
                log_verbose "'$link_path' exists and is not a symlink; skipping."
                continue
            fi
        else
            if ! ln -s "$binary_path" "$link_path" 2>/dev/null; then
                log_verbose "Failed to create symlink '$link_path'; skipping."
                continue
            fi
        fi

        echo "Created symlink '$link_path' → '$binary_path'."
        echo "templatecli is available in your current shell session."
        return 0
    done

    log_verbose "No suitable existing PATH directory found for symlink."
    return 1
}

ensure_path_in_profile() {
    local entry="$1"

    # Check if already on PATH (profile may already handle it)
    if echo ":${PATH}:" | grep -q ":${entry}:"; then
        log_verbose "'$entry' is already in the current session PATH."
        return 1
    fi

    # Update shell profile
    local profile
    profile=$(get_shell_profile)

    if [[ -f "$profile" ]] && grep -Fq "$entry" "$profile" 2>/dev/null; then
        log_verbose "'$entry' is already in '$profile'."
        return 0
    fi

    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    local path_line
    if [[ "$shell_name" == "fish" ]]; then
        path_line="fish_add_path ${entry}"
    else
        path_line="export PATH=\"${entry}:\$PATH\""
    fi

    local profile_dir
    profile_dir=$(dirname "$profile")
    if [[ ! -d "$profile_dir" ]]; then
        mkdir -p "$profile_dir" 2>/dev/null || true
    fi

    {
        echo ""
        echo "# Added by templatecli installer"
        echo "$path_line"
    } >> "$profile"

    echo "Added '$entry' to '$profile' for future terminal sessions."
    return 0
}

ensure_path_contains() {
    local install_dir="$1"
    local binary_path="${install_dir}/templatecli"
    local already_on_path=false

    # Check if already on PATH
    if echo ":${PATH}:" | grep -q ":${install_dir}:"; then
        log_verbose "'$install_dir' is already in the current session PATH."
        already_on_path=true
    fi

    # Always ensure profile is updated for future sessions
    ensure_path_in_profile "$install_dir" || true

    if [[ "$already_on_path" == true ]]; then
        return 0
    fi

    # Strategy 1 (Linux): symlink into a directory already on PATH
    if try_symlink_to_existing_path_dir "$binary_path"; then
        return 0
    fi

    # Strategy 2: print instructions for current session
    local profile
    profile=$(get_shell_profile)
    echo ""
    echo "To use templatecli in this terminal session, run:"
    echo "  source ${profile}"
    echo ""
    echo "Or open a new terminal session."
    return 0
}

profile_has_completion_entry() {
    local profile="$1"
    local script_path="$2"
    [[ -f "$profile" ]] && grep -Fq "$script_path" "$profile" 2>/dev/null
}

get_completion_shell() {
    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    case "$shell_name" in
        bash) echo "Bash" ;;
        zsh)  echo "Zsh" ;;
        fish) echo "fish" ;;
        *)    return 1 ;;
    esac
}

configure_shell_completion() {
    local command_path="$1"
    local completion_shell
    if ! completion_shell=$(get_completion_shell); then
        log_verbose "Skipping automatic completion setup for unsupported shell '${SHELL:-unknown}'."
        return 0
    fi

    local shell_name
    shell_name=$(basename "${SHELL:-/bin/bash}")

    local profile
    profile=$(get_shell_profile)
    local extension
    extension=$(echo "$completion_shell" | tr '[:upper:]' '[:lower:]')
    local completion_script_path
    completion_script_path="$(dirname "$command_path")/templatecli-completions.${extension}"

    if ! "$command_path" completions script "$extension" > "$completion_script_path"; then
        echo "Warning: templatecli was installed, but the shell completion script could not be generated." >&2
        return 0
    fi

    local profile_dir
    profile_dir=$(dirname "$profile")
    if [[ ! -d "$profile_dir" ]]; then
        mkdir -p "$profile_dir" 2>/dev/null || true
    fi

    if profile_has_completion_entry "$profile" "$completion_script_path"; then
        echo "Updated ${shell_name} tab completion script at '$completion_script_path'."
        return 0
    fi

    local profile_entry
    if [[ "$shell_name" == "fish" ]]; then
        profile_entry="test -f \"$completion_script_path\"; and source \"$completion_script_path\""
    else
        profile_entry="[ -f \"$completion_script_path\" ] && . \"$completion_script_path\""
    fi

    {
        echo ""
        echo "# Added by templatecli installer for shell completion"
        echo "$profile_entry"
    } >> "$profile"

    echo "Enabled ${shell_name} tab completion in '$profile'."
}

# ─── Extract ─────────────────────────────────────────────────────────────────

expand_release_archive() {
    local archive_path="$1"
    local dest_path="$2"

    log_verbose "Expanding '$archive_path' to '$dest_path'."
    mkdir -p "$dest_path"
    tar -xzf "$archive_path" -C "$dest_path"

    local binary_path="${dest_path}/templatecli"
    if [[ ! -f "$binary_path" ]]; then
        die "Downloaded archive '$(basename "$archive_path")' did not contain 'templatecli'."
    fi

    chmod +x "$binary_path"
    log_verbose "Found extracted binary '$binary_path'."
    echo "$binary_path"
}

# ─── Main install ────────────────────────────────────────────────────────────

install_templatecli() {
    assert_curl_available
    assert_jq_available

    local platform
    platform=$(get_platform)
    local architecture
    architecture=$(get_architecture)
    local rid="${platform}-${architecture}"
    local asset_name="templatecli-${rid}.tar.gz"

    log_verbose "Selecting release asset '$asset_name' for quality '$QUALITY' from '$REPOSITORY'."

    local release
    release=$(get_release_for_quality "$REPOSITORY" "$QUALITY" "$asset_name")
    log_verbose "Selected release '$(get_release_name "$release")' ($(get_release_tag "$release"))."
    local release_status_label
    release_status_label=$(get_release_status_label "$QUALITY" "$release")

    local release_tag
    release_tag=$(get_release_tag "$release")

    if [[ "$QUALITY" != "Dev" && "$SKIP_PROVENANCE" != true ]]; then
        assert_curl_available
        assert_cosign_available
    fi

    if [[ "$QUALITY" == "Dev" && "$FORCE" != true ]]; then
        if [[ -e /dev/tty ]]; then
            printf "Dev quality disables checksum/provenance verification. Type YES to continue: "
            local confirmation
            read -r confirmation < /dev/tty
            if [[ "$confirmation" != "YES" ]]; then
                die "Installation canceled by user."
            fi
        else
            die "Dev quality requires --force when running in non-interactive mode (no terminal available)."
        fi
    fi

    TEMPLATECLI_INSTALL_TEMP_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/templatecli-install-XXXXXX")
    local temp_root="$TEMPLATECLI_INSTALL_TEMP_ROOT"
    local download_path="${temp_root}/${asset_name}"
    local extract_path="${temp_root}/extract"

    log_verbose "Using temporary workspace '$temp_root'."

    cleanup() {
        if [[ -d "${TEMPLATECLI_INSTALL_TEMP_ROOT:-}" ]]; then
            rm -rf "$TEMPLATECLI_INSTALL_TEMP_ROOT"
        fi
    }
    trap cleanup EXIT

    local asset_url
    asset_url=$(get_release_asset_url "$release" "$asset_name")
    [[ -n "$asset_url" ]] || die "Release '$release_tag' does not contain asset '$asset_name'."

    status_step "Downloading ${release_status_label}" \
        invoke_github_asset_download "$asset_url" "$asset_name" "$temp_root"

    if [[ "$QUALITY" != "Dev" ]]; then
        # Download and verify checksums
        if ! has_release_asset "$release" "checksums.txt"; then
            die "Release '$release_tag' did not include checksums.txt."
        fi

        local checksums_path="${temp_root}/checksums.txt"
        log_verbose "Downloading checksums from release '$release_tag' to '$checksums_path'."
        local checksums_url
        checksums_url=$(get_release_asset_url "$release" "checksums.txt")
        [[ -n "$checksums_url" ]] || die "Release '$release_tag' did not include checksums.txt."
        invoke_github_asset_download "$checksums_url" "checksums.txt" "$temp_root"

        local release_metadata_path=""
        if has_release_asset "$release" "release-metadata.json"; then
            release_metadata_path="${temp_root}/release-metadata.json"
            log_verbose "Downloading release metadata from release '$release_tag' to '$release_metadata_path'."
            local release_metadata_url
            release_metadata_url=$(get_release_asset_url "$release" "release-metadata.json")
            [[ -n "$release_metadata_url" ]] || die "Release '$release_tag' did not include release-metadata.json."
            invoke_github_asset_download "$release_metadata_url" "release-metadata.json" "$temp_root"
        fi

        status_step "Verifying asset checksums" \
            assert_archive_integrity "$download_path" "$asset_name" "$checksums_path" "$release_metadata_path"

        # Verify artifact attestation
        if [[ "$SKIP_PROVENANCE" == true ]]; then
            echo "Skipping provenance verification (--skip-provenance)."
        else
            status_step "Verifying archive provenance" \
                assert_artifact_attestation "$download_path" "$REPOSITORY" "archive" "refs/tags/${release_tag}"
        fi
    else
        echo "Skipping checksum and provenance verification for development build."
    fi

    local downloaded_binary_path
    downloaded_binary_path=$(expand_release_archive "$download_path" "$extract_path")

    local install_directory
    install_directory=$(cd "$TARGET_PATH" 2>/dev/null && pwd || echo "$TARGET_PATH")
    # Resolve to absolute path
    install_directory=$(mkdir -p "$TARGET_PATH" && cd "$TARGET_PATH" && pwd)
    log_verbose "Installing to '$install_directory'."

    local destination_path="${install_directory}/templatecli"
    local downloaded_version
    downloaded_version=$(get_templatecli_version_string "$downloaded_binary_path")
    local installed_version="$downloaded_version"
    log_verbose "Downloaded templatecli version: '$downloaded_version'."

    local should_install=true
    if [[ -f "$destination_path" ]]; then
        local existing_version
        existing_version=$(get_templatecli_version_string "$destination_path") || true
        if [[ -n "${existing_version:-}" ]]; then
            local comparison
            comparison=$(compare_semantic_version "$downloaded_version" "$existing_version")
            log_verbose "Existing templatecli version at '$destination_path': '$existing_version'. Comparison result: $comparison."
            if [[ "$comparison" != "1" ]]; then
                should_install=false
                installed_version="$existing_version"
                echo "Existing templatecli version '$existing_version' is newer than or equal to downloaded version '$downloaded_version'; skipping overwrite."
            fi
        fi
    fi

    if [[ "$should_install" == true ]]; then
        status_step "Installing ${downloaded_version} to '${install_directory}'" \
            cp "$downloaded_binary_path" "$destination_path"
        chmod +x "$destination_path"
    fi

    if [[ "$UPDATE_PATH" == true ]]; then
        ensure_path_contains "$install_directory" || true
    else
        echo "Skipped PATH updates because --no-update-path was specified."
    fi

    configure_shell_completion "$destination_path"

    echo "templatecli ${installed_version} is ready to use from '${install_directory}'."
}

install_templatecli
