# Release, signing, and provenance

This template includes content and workflows for GitHub Releases-based distribution, optional Azure signing (for Windows assets), and provenance verification. The included release workflows are designed to be functional out of the box with minimal configuration, while also supporting optional signing and provenance features that can be enabled as needed.

## Release channels

The template uses three release channels:

- **Dev** - public immutable prerelease builds published directly from `ci.yml`; install/update intentionally skip provenance verification for this channel
- **Pre-release** - official tagged releases published by `release.yml`
- **Stable** - official tagged releases published by `release.yml` and eligible to become the repo latest release

## Workflow split

- `ci.yml` builds and tests assets, publishes a public immutable Dev release, uploads a promotable bundle, and advances the mutable `release-state` branch
- `publish-release.yml` is the admin-run promotion workflow; it chooses the current official version, creates the release tag, and dispatches `release.yml` on that tag ref
- `release.yml` runs on the release tag ref, downloads the already-built CI bundle, signs Windows assets, regenerates metadata/checksums after signing, attests the final release archives, publishes the GitHub release, and advances `release-state`
- `bump-version.yml` updates the mutable `release-state` branch when you want to move between `pre`, `rc`, and `rtm` lines or bump the base version ahead of the next CI/dev cycle

## Install script publication

Install scripts are published independently of software releases:

- `install-scripts.yml` signs/stages the latest bootstrap scripts from `main`, publishes them to the mutable `install-scripts` branch, tags that snapshot, and dispatches `attest-install-scripts.yml` on that tag ref
- `attest-install-scripts.yml` creates the immutable install-script snapshot release and publishes tag-bound attestations for the scripts

Latest bootstrap URLs come from the branch, for example:

- `https://raw.githubusercontent.com/example/templatecli/install-scripts/install.sh`
- `https://raw.githubusercontent.com/example/templatecli/install-scripts/install.ps1`

This design supports both mutable and immutable consumption. The branch-backed URLs provide a convenient "latest" bootstrap entry point, while each publication also creates a tag-bound immutable snapshot release that consumers can pin to when they want a fixed install-script version.

The `install-scripts` branch is treated as workflow-managed mutable state. Repositories created from this template typically will not have that branch yet unless the creator explicitly chose to include all branches, so the publication flow is expected to create `install-scripts` on first publish when it is missing. If repository permissions or branch protection prevent the workflow from creating or force-updating that branch, the workflow should fail with a clear, actionable error.

## Optional repository configuration

The release and installer workflows are designed to work before signing is configured.

### Optional Azure signing secrets

If all of these secrets are present, Windows binaries and the PowerShell installer are signed:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_SIGNING_ENDPOINT`
- `AZURE_SIGNING_ACCOUNT`
- `AZURE_CERT_PROFILE`

If any are missing, the workflows log a warning and continue with unsigned artifacts instead of failing.

### Optional repository variables

- `DEFAULT_POST_RELEASE_PHASE` - optional default for the post-release version bump phase (`pre`, `rc`, or `rtm`)

### Optional environment variables at install/update time

- `GITHUB_TOKEN` or `GH_TOKEN` - optional for GitHub attestation bundle downloads, useful for private repositories or avoiding anonymous API rate limits

## Provenance and trust model

- The Windows installer script remains the single source of truth for Authenticode trust configuration. `Generate-VerifyProvenance.ps1` derives the embedded verifier from it during build.
- On Linux/macOS, the CLI self-update path verifies GitHub attestation bundles locally with the `Sigstore` NuGet package instead of shelling out to `gh attestation verify`.
- Official Pre-release and Stable assets are attested in `release.yml` on the exact release tag ref, and Unix verification pins to that workflow + tag combination.
- On Linux/macOS, `install-templatecli.sh` verifies GitHub attestation bundles with `cosign`. `cosign` is therefore a prerequisite unless `--skip-provenance` is used or a Dev build is being installed.
- For macOS, the recommended `cosign` install path is Homebrew: `brew install cosign`. On Linux, the installer prints package-manager-specific guidance where possible and otherwise points to the official Cosign install docs.
- If you use a local update source via `TEMPLATECLI_UPDATE_SOURCE`, provenance verification for Unix self-update is intentionally not supported; use `templatecli update --skip-provenance-checks` for local testing scenarios.

## Mutable supporting branches

- `release-state` tracks the current mutable versioning state used by the workflows
- `install-scripts` serves the latest published bootstrap scripts and is expected to be created/updated by the install-script publication workflow
