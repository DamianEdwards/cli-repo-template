using System.CommandLine;
using System.CommandLine.Completions;
using TemplateCli.Completions;
using TemplateCli.Infrastructure;

namespace TemplateCli.Commands;

public static class CompletionsCommand
{
    public static Command Create()
    {
        var command = new Command("completions", "Generate shell completion scripts");
        command.Aliases.Add("completion");

        var scriptCommand = new Command("script", "Generate a shell completion script");
        var shellArgument = new Argument<string?>("shell")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Shell to generate a completion script for"
        };
        shellArgument.CompletionSources.Add(_ => CompletionScripts.SupportedShells.Select(shell => new CompletionItem(shell)));
        scriptCommand.Arguments.Add(shellArgument);

        var commandNameOption = new Option<string[]>("--command-name")
        {
            Description = "Command name to register the completion script for. Can be specified multiple times."
        };
        commandNameOption.AllowMultipleArgumentsPerToken = false;
        scriptCommand.Options.Add(commandNameOption);

        scriptCommand.SetAction(parseResult =>
        {
            var shell = parseResult.GetValue(shellArgument) ?? CompletionScripts.GetDefaultShell();
            var commandNames = parseResult.GetValue(commandNameOption);
            var effectiveCommandNames = commandNames is { Length: > 0 }
                ? commandNames
                : [AppIdentity.CommandName];

            if (!CompletionScripts.TryGenerate(shell, effectiveCommandNames, out var script, out var error))
            {
                ConsoleOutput.Error(error);
                return 1;
            }

            Console.Out.Write(script);
            return 0;
        });

        command.Subcommands.Add(scriptCommand);
        return command;
    }
}
