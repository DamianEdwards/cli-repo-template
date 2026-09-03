using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;
using TemplateCli.Infrastructure;

namespace TemplateCli.Tests;

public sealed class GitHubReleaseServiceTests
{
    [Fact]
    public void SelectLatestReleaseUsesSemVerInsteadOfApiOrder()
    {
        using var releases = JsonDocument.Parse("""
            [
              {"tag_name":"v1.1.0","draft":false,"prerelease":false,"assets":[{"name":"templatecli-platform-arch.zip"}]},
              {"tag_name":"v1.10.0","draft":false,"prerelease":false,"assets":[{"name":"templatecli-platform-arch.zip"}]},
              {"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[{"name":"templatecli-platform-arch.zip"}]}
            ]
            """);

        var selected = GitHubReleaseService.SelectLatestRelease(
            releases.RootElement,
            NuGetVersion.Parse("1.0.0"),
            allowPreRelease: false,
            stableOnly: false,
            "templatecli-platform-arch.zip");

        Assert.NotNull(selected);
        Assert.Equal("v1.10.0", selected.TagName);
    }

    [Fact]
    public void ExtractReleaseArchiveRejectsTarPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"templatecli-tar-test-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(root, "payload.tar.gz");
        var destination = Path.Combine(root, "extract");
        var outsidePath = Path.Combine(root, "outside.txt");
        Directory.CreateDirectory(root);

        try
        {
            using (var archive = File.Create(archivePath))
            using (var gzip = new GZipStream(archive, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../outside.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("unexpected"))
                });
            }

            Assert.Throws<UserFacingException>(
                () => GitHubReleaseService.ExtractReleaseArchive(archivePath, destination));
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
