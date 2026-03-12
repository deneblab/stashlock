using System;
using Deneblab.StashLock.Cli.Common.Simple;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class SimpleVersionParserTests
{
    [Fact]
    public void Parse_ShouldExtractSemVer()
    {
        var result = SimpleVersionParser.Parse("1.2.3+BuildCounter.5.Branch.main");

        Assert.Equal("1.2.3", result.SemVer);
    }

    [Fact]
    public void Parse_ShouldExtractBuildCounter()
    {
        var result = SimpleVersionParser.Parse("1.0.0+BuildCounter.42.Branch.main");

        Assert.Equal(42, result.BuildCounter);
    }

    [Fact]
    public void Parse_ShouldExtractBranch()
    {
        var result = SimpleVersionParser.Parse("1.0.0+Branch.production");

        Assert.Equal("production", result.Branch);
    }

    [Fact]
    public void Parse_ShouldExtractAllFields()
    {
        var result = SimpleVersionParser.Parse(
            "2.1.0+BuildCounter.10.Branch.main.DateTime.2026-01-15.Env.dev.Sha.abc123.GitCommits.99");

        Assert.Equal("2.1.0", result.SemVer);
        Assert.Equal(10, result.BuildCounter);
        Assert.Equal("main", result.Branch);
        Assert.Equal("dev", result.Env);
        Assert.Equal("abc123", result.Sha);
        Assert.Equal(99, result.GitCommits);
    }

    [Fact]
    public void Parse_ShouldHandleSemVerOnly()
    {
        var result = SimpleVersionParser.Parse("3.0.0");

        Assert.Equal("3.0.0", result.SemVer);
        Assert.Equal(0, result.BuildCounter);
        Assert.Equal(string.Empty, result.Branch);
        Assert.Equal(0, result.GitCommits);
    }

    [Fact]
    public void Parse_ShouldHandlePreReleaseVersion()
    {
        var result = SimpleVersionParser.Parse("1.0.0-beta.1+Branch.dev");

        Assert.Equal("1.0.0-beta.1", result.SemVer);
        Assert.Equal("dev", result.Branch);
    }

    [Fact]
    public void Parse_ShouldThrowOnNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => SimpleVersionParser.Parse(null!));
    }

    [Fact]
    public void Parse_ShouldThrowOnEmptyInput()
    {
        Assert.Throws<ArgumentNullException>(() => SimpleVersionParser.Parse(""));
    }

    [Fact]
    public void Parse_ShouldThrowOnWhitespaceInput()
    {
        Assert.Throws<ArgumentNullException>(() => SimpleVersionParser.Parse("   "));
    }

    [Fact]
    public void Parse_ShouldDefaultMissingFields()
    {
        var result = SimpleVersionParser.Parse("1.0.0+Branch.main");

        Assert.Equal(0, result.BuildCounter);
        Assert.Equal(string.Empty, result.Env);
        Assert.Equal(string.Empty, result.Sha);
        Assert.Equal(0, result.GitCommits);
        Assert.Equal(DateTime.MinValue, result.DateTime);
    }
}
