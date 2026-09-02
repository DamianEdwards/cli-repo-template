using System.Text.Json;
using TemplateCli.Infrastructure;
using TemplateCli.Models;
using TemplateCli.Services;

namespace TemplateCli.Tests;

public sealed class PayloadInstallerTests
{
    [Fact]
    public void ValidateManifestAcceptsExactPortableInventory()
    {
        using var fixture = new PayloadFixture("future-sidecar.dll", "data/future-format.json");

        var files = PayloadInstaller.ValidateManifest(fixture.Root);

        Assert.Contains("future-sidecar.dll", files);
        Assert.Contains(Path.Combine("data", "future-format.json"), files);
    }

    [Fact]
    public void ValidateManifestRejectsUndeclaredFile()
    {
        using var fixture = new PayloadFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "unexpected.txt"), "unexpected");

        Assert.Throws<UserFacingException>(() => PayloadInstaller.ValidateManifest(fixture.Root));
    }

    [Fact]
    public void ValidateManifestRejectsPathTraversal()
    {
        using var fixture = new PayloadFixture();
        fixture.WriteManifest(["../outside"]);

        Assert.Throws<UserFacingException>(() => PayloadInstaller.ValidateManifest(fixture.Root));
    }

    private sealed class PayloadFixture : IDisposable
    {
        public PayloadFixture(params string[] additionalFiles)
        {
            Root = Path.Combine(Path.GetTempPath(), $"templatecli-payload-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            var files = new[] { AppIdentity.GetExecutableFileName() }
                .Concat(additionalFiles)
                .ToArray();
            foreach (var file in files)
            {
                var path = Path.Combine(Root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file);
            }

            WriteManifest(files);
        }

        public string Root { get; }

        public void WriteManifest(IEnumerable<string> files)
        {
            var manifest = new PayloadManifest
            {
                Files = files
                    .Select(path => path.Replace('\\', '/'))
                    .Order(StringComparer.Ordinal)
                    .ToList()
            };
            File.WriteAllText(
                Path.Combine(Root, PayloadInstaller.ManifestFileName),
                JsonSerializer.Serialize(
                    manifest,
                    TemplateCliJsonContext.Default.PayloadManifest));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
