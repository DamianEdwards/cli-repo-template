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

The `install-scripts` branch is treated as workflow-managed mutable state. Repositories created from this template typically will not have that branch yet unless the creator explicitly chose to include all branches, so the publication flow is expected to create `install-scripts` on first publish when it is missing. Subsequent publications add regular commits so the branch history is preserved. If repository permissions or branch protection prevent the workflow from creating or updating that branch, the workflow should fail with a clear, actionable error.

`install-scripts.yml` uses the `production` environment for the single approval gate before signing keys are accessed or the public `install-scripts` branch and snapshot tag are updated. `attest-install-scripts.yml` intentionally remains ungated and only publishes attestations and the immutable release for the already-approved snapshot tag.

## Required repository setup

Before publishing official releases or install scripts, configure the repository so the workflows can use the `production` environment and the workflow-managed branches and tags cannot be accidentally destroyed.

### `production` environment

Create a repository environment named `production`. This environment is the single approval gate for operations that either use signing credentials or publish public release/install assets.

Recommended environment settings:

- Add required reviewers for release/install publication approval.
- If more than one reviewer is available, enable **Prevent self-review**.
- Use **Selected branches and tags** for deployment branches/tags.
- Allow the `main` branch so `install-scripts.yml` can publish install scripts.
- Allow `v*` tags so `release.yml` can sign and publish official release assets.
- Leave `install-scripts-v*` tags out unless `attest-install-scripts.yml` is later changed to use the `production` environment.
- Disable administrator bypass if the approval gate should apply even to repository administrators; otherwise leave it enabled as an emergency escape hatch.

### `install-scripts` branch ruleset

Create a branch ruleset targeting exactly:

```text
install-scripts
```

The minimal ruleset that still allows the workflow to publish with the default `GITHUB_TOKEN` is:

- **Restrict deletions**: enabled
- **Block force pushes**: enabled
- **Restrict creations**: disabled
- **Restrict updates**: disabled
- Bypass list: empty

This protects the mutable installer branch from deletion and history rewrites while still allowing `install-scripts.yml` to create the branch on first publish and push regular history-preserving updates on subsequent publishes.

To also block direct human pushes, use a dedicated machine identity instead of the default `GITHUB_TOKEN`: create a GitHub App or write deploy key, add that actor to the ruleset bypass list, store its credentials in the `production` environment, and have `install-scripts.yml` push with that token/key. Then enable **Restrict creations** and **Restrict updates** for the `install-scripts` ruleset.

### Install-script snapshot tag ruleset

Create a tag ruleset targeting:

```text
install-scripts-v*
```

Recommended rules:

- **Restrict deletions**: enabled
- **Restrict updates**: enabled
- **Restrict creations**: disabled
- Bypass list: empty

`install-scripts.yml` creates a new snapshot tag for each publication and fails if the tag already exists, so updates and deletions should not be needed after creation.

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

## Workflow helper implementation notes

The repo uses a mix of PowerShell and C# file-based apps for workflow support scripts:

- `scripts\merge-release-bundle.cs`
- `scripts\update-release-bundle-metadata.cs`
- `scripts\expand-windows-release-assets.cs`
- `scripts\compress-windows-release-assets.cs`
- `scripts\write-install-scripts-manifest.cs`
- `scripts\version.cs`

These helpers are good C# candidates because they are workflow-only, cross-platform file/JSON/archive utilities with no dependency on PowerShell-specific runtime features.

The remaining PowerShell scripts stay in PowerShell for now because they are either user-facing shell entry points (`build.ps1`, `templatecli.ps1`), installer/shipping assets (`scripts\install\install-templatecli.ps1`), or closely tied to PowerShell-specific Authenticode/provenance behavior (`Generate-VerifyProvenance.ps1`, `Verify-WindowsBinaryIssuer.ps1`, `Test-InstallerProvenance.ps1`, `Verify-PowerShellSyntax.ps1`).

`Publish-NativeAsset.ps1` remains the main workflow-only PowerShell candidate for a future pass. It does not ship to end users, but it still has more Windows-specific toolchain setup and external process orchestration than the helpers above.
