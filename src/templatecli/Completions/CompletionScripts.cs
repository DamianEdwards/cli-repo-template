namespace TemplateCli.Completions;

public static class CompletionScripts
{
    private static readonly string[] s_supportedShells = ["bash", "zsh", "fish", "pwsh"];

    public static IReadOnlyList<string> SupportedShells => s_supportedShells;

    public static string? GetDefaultShell()
    {
        if (OperatingSystem.IsWindows())
            return "pwsh";

        var shellPath = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shellPath))
            return null;

        return NormalizeShell(Path.GetFileName(shellPath));
    }

    public static bool TryGenerate(string? shell, string commandName, out string script, out string error)
        => TryGenerate(shell, [commandName], out script, out error);

    public static bool TryGenerate(string? shell, IReadOnlyList<string> commandNames, out string script, out string error)
    {
        var normalizedShell = NormalizeShell(shell);
        if (normalizedShell is null)
        {
            script = string.Empty;
            error = $"Unsupported shell '{shell ?? "(default)"}'. Supported shells: {string.Join(", ", SupportedShells)}.";
            return false;
        }

        var normalizedCommandNames = commandNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedCommandNames.Length == 0)
        {
            script = string.Empty;
            error = "At least one command name must be specified.";
            return false;
        }

        script = normalizedShell switch
        {
            "bash" => GenerateBashScript(normalizedCommandNames),
            "zsh" => GenerateZshScript(normalizedCommandNames),
            "fish" => GenerateFishScript(normalizedCommandNames),
            "pwsh" => GeneratePowerShellScript(normalizedCommandNames),
            _ => string.Empty
        };

        error = string.Empty;
        return true;
    }

    private static string? NormalizeShell(string? shell)
        => shell?.Trim().ToLowerInvariant() switch
        {
            "bash" => "bash",
            "zsh" => "zsh",
            "fish" => "fish",
            "pwsh" or "powershell" or "powershell.exe" or "pwsh.exe" => "pwsh",
            _ => null
        };

    private static string GenerateBashScript(IReadOnlyList<string> commandNames)
    {
        var functionName = $"_{MakeSafeFunctionName(commandNames[0])}_completion";
        var registrationNames = string.Join(" ", commandNames);
        return $$"""
        #!/usr/bin/env bash
        {{functionName}}()
        {
            local command
            local completions
            local word
            local IFS=$'\n'
            local suggestions

            command="${COMP_WORDS[0]}"
            completions=$("$command" "[suggest:${COMP_POINT}]" "${COMP_LINE}" 2>/dev/null)
            word="${COMP_WORDS[COMP_CWORD]}"
            suggestions=($(compgen -W "$completions" -- "$word"))

            for i in "${!suggestions[@]}"; do
                suggestions[i]="$(printf '%q' "${suggestions[$i]}")"
            done

            COMPREPLY=("${suggestions[@]}")
        }

        complete -F {{functionName}} {{registrationNames}}
        """;
    }

    private static string GenerateZshScript(IReadOnlyList<string> commandNames)
    {
        var functionName = $"_{MakeSafeFunctionName(commandNames[0])}";
        var registrationNames = string.Join(" ", commandNames);
        return $$"""
        #compdef {{registrationNames}}

        {{functionName}}()
        {
            local command
            local completions
            local exploded
            local full_line="$words"

            command="$words[1]"
            completions=$("$command" "[suggest:${#full_line}]" "$full_line" 2>/dev/null)
            exploded=(${(f)completions})
            _values 'suggestions' $exploded
        }

        compdef {{functionName}} {{registrationNames}}
        """;
    }

    private static string GenerateFishScript(IReadOnlyList<string> commandNames)
    {
        var safeName = MakeSafeFunctionName(commandNames[0]);
        var registrations = string.Join(
            Environment.NewLine,
            commandNames.Select(commandName => GenerateFishRegistration(commandName, safeName)));

        return $$"""
        # fish parameter completion for {{commandNames[0]}}
        function __{{safeName}}_complete
            set -l pos (commandline -C)
            set -l line (commandline -cp)
            set -l tokens (commandline -opc)

            if test (count $tokens) -eq 0
                return
            end

            set -l command $tokens[1]
            $command "[suggest:$pos]" $line 2>/dev/null
        end

        {{registrations}}
        """;
    }

    private static string GeneratePowerShellScript(IReadOnlyList<string> commandNames)
    {
        var escapedCommandNames = string.Join(", ", commandNames.Select(commandName => $"'{EscapePowerShellSingleQuotedString(commandName)}'"));
        return $$"""
        using namespace System.Management.Automation

        Register-ArgumentCompleter -Native -CommandName {{escapedCommandNames}} -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $commandName = $commandAst.CommandElements[0].Extent.Text
            & $commandName "[suggest:$cursorPosition]" $commandAst.ToString() 2>$null |
                Where-Object { $_ -like "$wordToComplete*" } |
                ForEach-Object { [CompletionResult]::new($_, $_, [CompletionResultType]::ParameterValue, $_) }
        }
        """;
    }

    private static string GenerateFishRegistration(string commandName, string safeName)
    {
        var escapedCommandName = EscapeFishString(commandName);
        return Path.IsPathRooted(commandName)
            ? $"complete -f -p \"{escapedCommandName}\" -a '(__{safeName}_complete)'"
            : $"complete -f -c \"{escapedCommandName}\" -a '(__{safeName}_complete)'";
    }

    private static string MakeSafeFunctionName(string commandName)
        => commandName.Replace('-', '_').Replace('.', '_').Replace('\\', '_').Replace('/', '_');

    private static string EscapeFishString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapePowerShellSingleQuotedString(string value)
        => value.Replace("'", "''");
}
