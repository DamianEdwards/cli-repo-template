using TemplateCli.Infrastructure;

namespace TemplateCli.Tests;

public sealed class ProvenanceVerifierTests
{
    [Fact]
    public void WindowsPowerShellModulePathExcludesPowerShellSeven()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = ProvenanceVerifier.GetWindowsPowerShellModulePath()
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(
            paths,
            path => path.EndsWith(
                @"WindowsPowerShell\v1.0\Modules",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            paths,
            path => path.Contains(
                @"PowerShell\7\Modules",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void WindowsPayloadVerificationSelectsEveryExeAndDll()
    {
        var files = ProvenanceVerifier.GetWindowsExecutablePayloadFileNames(
        [
            "templatecli.exe",
            "future-sidecar.dll",
            "native/helper.dll",
            "LICENSE",
            "data/settings.json"
        ]);

        Assert.Equal(
        [
            "future-sidecar.dll",
            "native/helper.dll",
            "templatecli.exe"
        ], files);
    }
}
