using Microsoft.Extensions.Logging.Abstractions;
using TemplateCli.Infrastructure;

namespace TemplateCli.Tests;

public sealed class ProvenanceVerifierTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleaseMetadataDeclaresWindowsSigningState(bool windowsSigned)
    {
        var metadataPath = Path.GetTempFileName();
        try
        {
            const string hash = "1111111111111111111111111111111111111111111111111111111111111111";
            File.WriteAllText(
                metadataPath,
                $$"""
                {
                  "version": "1.2.3",
                  "sourceCommit": "2222222222222222222222222222222222222222",
                  "windowsSigned": {{windowsSigned.ToString().ToLowerInvariant()}},
                  "assets": [
                    { "name": "templatecli-platform-arch.zip", "sha256": "{{hash}}" }
                  ]
                }
                """);
            var verifier = new ProvenanceVerifier(NullLogger<ProvenanceVerifier>.Instance);

            var result = verifier.ValidateReleaseMetadata(
                metadataPath,
                "templatecli-platform-arch.zip",
                hash,
                "1.2.3");

            Assert.True(result.Success, result.Error);
            Assert.Equal(windowsSigned, result.WindowsSigned);
        }
        finally
        {
            File.Delete(metadataPath);
        }
    }

    [Fact]
    public void ReleaseMetadataRequiresWindowsSigningState()
    {
        var metadataPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                metadataPath,
                """
                {
                  "version": "1.2.3",
                  "sourceCommit": "2222222222222222222222222222222222222222",
                  "assets": []
                }
                """);
            var verifier = new ProvenanceVerifier(NullLogger<ProvenanceVerifier>.Instance);

            var result = verifier.ValidateReleaseMetadata(
                metadataPath,
                "templatecli-platform-arch.zip",
                new string('1', 64),
                "1.2.3");

            Assert.False(result.Success);
            Assert.Contains("windowsSigned", result.Error);
        }
        finally
        {
            File.Delete(metadataPath);
        }
    }

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
