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
    {
        var normalizedShell = NormalizeShell(shell);
        if (normalizedShell is null)
        {
            script = string.Empty;
            error = $"Unsupported shell '{shell ?? "(default)"}'. Supported shells: {string.Join(", ", SupportedShells)}.";
            return false;
        }

        script = normalizedShell switch
        {
            "bash" => GenerateBashScript(commandName),
            "zsh" => GenerateZshScript(commandName),
            "fish" => GenerateFishScript(commandName),
            "pwsh" => GeneratePowerShellScript(commandName),
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

    private static string GenerateBashScript(string commandName)
    {
        var functionName = $"_{MakeSafeFunctionName(commandName)}_completion";
        return $$"""
        #!/usr/bin/env bash
        {{functionName}}()
        {
            local completions
            local word
            local IFS=$'\n'
            local suggestions

            completions=$({{commandName}} "[suggest:${COMP_POINT}]" "${COMP_LINE}" 2>/dev/null)
            word="${COMP_WORDS[COMP_CWORD]}"
            suggestions=($(compgen -W "$completions" -- "$word"))

            for i in "${!suggestions[@]}"; do
                suggestions[i]="$(printf '%q' "${suggestions[$i]}")"
            done

            COMPREPLY=("${suggestions[@]}")
        }

        complete -F {{functionName}} {{commandName}}
        """;
    }

    private static string GenerateZshScript(string commandName)
    {
        var functionName = $"_{MakeSafeFunctionName(commandName)}";
        return $$"""
        #compdef {{commandName}}

        {{functionName}}()
        {
            local completions
            local exploded
            local full_line="$words"

            completions=$({{commandName}} "[suggest:${#full_line}]" "$full_line" 2>/dev/null)
            exploded=(${(f)completions})
            _values 'suggestions' $exploded
        }

        compdef {{functionName}} {{commandName}}
        """;
    }

    private static string GenerateFishScript(string commandName)
    {
        var safeName = MakeSafeFunctionName(commandName);
        return $$"""
        # fish parameter completion for {{commandName}}
        function __{{safeName}}_complete
            set -l pos (commandline -C)
            set -l line (commandline -cp)
            {{commandName}} "[suggest:$pos]" $line 2>/dev/null
        end

        complete -f -c {{commandName}} -a '(__{{safeName}}_complete)'
        """;
    }

    private static string GeneratePowerShellScript(string commandName)
    {
        var escapedCommandName = EscapePowerShellSingleQuotedString(commandName);
        return $$"""
        using namespace System.Management.Automation

        Register-ArgumentCompleter -Native -CommandName '{{escapedCommandName}}' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            & '{{escapedCommandName}}' "[suggest:$cursorPosition]" $commandAst.ToString() 2>$null |
                Where-Object { $_ -like "$wordToComplete*" } |
                ForEach-Object { [CompletionResult]::new($_, $_, [CompletionResultType]::ParameterValue, $_) }
        }
        """;
    }

    private static string MakeSafeFunctionName(string commandName)
        => commandName.Replace('-', '_').Replace('.', '_');

    private static string EscapePowerShellSingleQuotedString(string value)
        => value.Replace("'", "''");
}
