# cli-repo-template

This is a repo template for building a cross-platform native command line executable using .NET. It includes a sample CLI plus reusable infrastructure for self-updating, GitHub Releases integration, provenance verification, structured logging, and more.

## What is included

- A minimal `System.CommandLine` CLI in `src\templatecli`
- Source-generated JSON serialization for persisted update state
- Structured console and file logging
- Cross-platform app-home and runtime-context helpers
- GitHub Releases integration for self-update
- Provenance verification:
  - GitHub artifact attestations on Linux/macOS
  - Windows Authenticode verification via an embedded PowerShell verifier
- Staged self-update flow in `UpdateService`
- Install scripts for Windows and Linux/macOS
- Release/build workflows plus composite GitHub Actions
- Windows signing and issuer verification support

## Placeholder identity

This first iteration uses a concrete placeholder app identity instead of tokenized replacements:

| Setting | Value |
| --- | --- |
| Command name | `templatecli` |
| Project/assembly name | `templatecli` |
| Namespace root | `TemplateCli` |
| Default repository | `example/templatecli` |
| App home env var | `TEMPLATECLI_HOME` |

The main source of truth for the app identity is `src\templatecli\Infrastructure\AppIdentity.cs`.

## Current command surface

The sample CLI intentionally keeps only the reusable maintenance command shape:

- `templatecli completions script`
- `templatecli config`
- `templatecli logs`
- `templatecli update`

`shutdown-instance` is retained as a hidden internal helper for Windows self-update shutdown behavior.

## Global config

`templatecli` stores user-managed global configuration in its home folder:

- default path: `~/.templatecli/config.json` on Linux/macOS, `~\.templatecli\config.json` on Windows
- overridden path: `%TEMPLATECLI_HOME%\config.json` or `$TEMPLATECLI_HOME/config.json`

View the current config:

```powershell
templatecli config
```

Update a value:

```powershell
templatecli config --set default_log_level=debug
templatecli config --set include_prerelease_updates=true
```

Supported keys:

- `default_log_level`
- `include_prerelease_updates`

## Shell completion

This template includes a built-in `completions script` command that emits shell completion scripts with no external dependency. The generated shell hooks call the CLI's own `System.CommandLine` `[suggest:<cursor>]` support, so users do not need `dotnet-suggest` or any other helper tool.

```text
templatecli completions script [<bash|fish|pwsh|zsh>]
```

If no shell is specified, the command uses the detected current shell, defaulting to `pwsh` on Windows.

The install scripts automatically:

1. generate a `templatecli-completions.<ext>` script next to the installed binary
2. add a profile entry that sources that script for future shells

If you want to enable it manually later, generate a script and source it from your shell profile:

1. Bash:
   - `templatecli completions script bash > ~/.templatecli/bin/templatecli-completions.bash`
   - `echo '[ -f ~/.templatecli/bin/templatecli-completions.bash ] && . ~/.templatecli/bin/templatecli-completions.bash' >> ~/.bashrc`
2. Zsh:
   - `templatecli completions script zsh > ~/.templatecli/bin/templatecli-completions.zsh`
   - `echo '[ -f ~/.templatecli/bin/templatecli-completions.zsh ] && . ~/.templatecli/bin/templatecli-completions.zsh' >> ~/.zshrc`
3. Fish:
   - `templatecli completions script fish > ~/.config/fish/completions/templatecli.fish`
4. PowerShell:
   - `templatecli completions script pwsh > $HOME\\.templatecli\\bin\\templatecli-completions.ps1`
   - `Add-Content -Path $PROFILE.CurrentUserAllHosts -Value ". '$HOME\\.templatecli\\bin\\templatecli-completions.ps1'"`

`cmd.exe` does not support this integration.

## Repository layout

- `src\templatecli` - sample CLI plus reusable infrastructure
- `scripts\install` - Windows and Linux/macOS installers
- `scripts` - publish, bundle, provenance, and validation helpers
- `.github\actions` - reusable composite actions for build/publish
- `.github\workflows` - CI, PR, versioning, release, and installer publication workflows

## Customization checklist

If you turn this into a real repository, start here:

1. Rename the app identity in `AppIdentity.cs`.
2. Update `Directory.Build.props` metadata, especially `RepositoryUrl`.
3. Replace the sample commands in `Program.cs` and `Commands\`.
4. Review installer defaults in `scripts\install\install-templatecli.ps1` and `install-templatecli.sh`.
5. Update README examples and any remaining placeholder strings like `example/templatecli`.
6. If you change persisted models, register them in `Models\JsonContext.cs`.

## Release, signing, and provenance

All of the release-channel mechanics, signing configuration, install-script publication flow, and provenance behavior live in [docs/release-and-provenance.md](docs/release-and-provenance.md).

## Local development

Build from the repo root:

```powershell
.\build.ps1
```

Or directly:

```powershell
dotnet build .\src\templatecli\templatecli.csproj -nologo
```

Run the sample CLI from source:

```powershell
.\templatecli.ps1 logs
```

Validate the script assets:

```powershell
.\scripts\Verify-PowerShellSyntax.ps1
```

```bash
./scripts/verify-shell-syntax.sh
```

## Notes

- The template intentionally preserves working release/update logic rather than abstracting everything behind a more complicated token engine.
- This is an extraction baseline, not a finished productized template. Expect to make a second pass for your final app identity, branding, and command surface.
