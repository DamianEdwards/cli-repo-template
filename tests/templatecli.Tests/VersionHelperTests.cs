using NuGet.Versioning;
using TemplateCli.Infrastructure;

namespace TemplateCli.Tests;

public sealed class VersionHelperTests
{
    [Fact]
    public void StableSelectionIsStrict()
    {
        var current = NuGetVersion.Parse("1.0.0");

        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
        Assert.False(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: false));
    }

    [Fact]
    public void PreviewCanAdvanceToStable()
    {
        Assert.True(VersionHelper.IsUpdateCandidate(
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
    }

    [Fact]
    public void DevCanAdvanceToAnyNewerChannel()
    {
        var current = NuGetVersion.Parse("1.1.0-pre.1.dev.2");

        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: false));
        Assert.True(VersionHelper.IsUpdateCandidate(
            current,
            NuGetVersion.Parse("1.1.0"),
            allowPreRelease: false));
    }

    [Fact]
    public void StableOnlyRejectsNewerPreview()
    {
        Assert.False(VersionHelper.IsUpdateCandidate(
            NuGetVersion.Parse("1.0.0-pre.1.rel"),
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: true,
            stableOnly: true));
    }
}
