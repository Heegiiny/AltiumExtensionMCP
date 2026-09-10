using System;
using System.Collections.Generic;
using AltiumMcp.Contracts.Model;
using Xunit;

namespace AltiumMcp.Tests;

/// <summary>
/// Review 2026-09-10 §3: agent clients send "None"/"null"/"" for omitted arguments; such values must be treated as
/// absent BEFORE they reach any Altium API (a literal file "None" once produced a modal dialog).
/// </summary>
public class InputNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("NULL")]
    [InlineData("null")]
    [InlineData("undefined")]
    [InlineData("nil")]
    [InlineData("N/A")]
    [InlineData("\"None\"")]
    [InlineData("'null'")]
    public void Optional_treats_placeholders_as_absent(string? value)
    {
        Assert.Null(InputNormalizer.Optional(value));
        Assert.Null(InputNormalizer.FilePath(value));
    }

    [Theory]
    [InlineData("U1", "U1")]
    [InlineData("  GND ", "GND")]
    [InlineData("Nonexistent", "Nonexistent")]
    [InlineData("NoneOfTheAbove", "NoneOfTheAbove")]
    public void Optional_keeps_real_values_trimmed(string value, string expected)
    {
        Assert.Equal(expected, InputNormalizer.Optional(value));
    }

    [Fact]
    public void OptionalList_drops_placeholders_and_duplicates()
    {
        List<string> list = InputNormalizer.OptionalList(new[] { "U1", "None", " u1 ", "", null, "R2" });
        Assert.Equal(new[] { "U1", "R2" }, list);
        Assert.Empty(InputNormalizer.OptionalList(null));
    }

    [Fact]
    public void FilePath_returns_full_path_for_rooted_input()
    {
        string? p = InputNormalizer.FilePath(@"C:\Designs\Board\Main.SchDoc");
        Assert.Equal(@"C:\Designs\Board\Main.SchDoc", p);
    }

    [Fact]
    public void FilePath_resolves_relative_against_base_directory()
    {
        string? p = InputNormalizer.FilePath(@"sub\Main.SchDoc", @"C:\Designs\Board");
        Assert.Equal(@"C:\Designs\Board\sub\Main.SchDoc", p);
    }

    [Fact]
    public void FilePath_rejects_invalid_characters_and_directories()
    {
        Assert.Throws<ArgumentException>(() => InputNormalizer.FilePath("C:\\bad\0name.SchDoc"));
        Assert.Throws<ArgumentException>(() => InputNormalizer.FilePath(@"C:\Designs\"));
    }

    [Theory]
    [InlineData("Main.SchDoc", true)]
    [InlineData(@"C:\x\Main.SchDoc", false)]
    [InlineData("dir/Main.SchDoc", false)]
    public void IsBareFileName_detects_names_without_directory(string value, bool expected)
    {
        Assert.Equal(expected, InputNormalizer.IsBareFileName(value));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-5, 200)]
    [InlineData(50, 50)]
    [InlineData(99999, 2000)]
    public void Limit_clamps(int requested, int expected)
    {
        Assert.Equal(expected, InputNormalizer.Limit(requested, 200, 2000));
    }

    [Theory]
    [InlineData(null, DetailLevel.Connectivity)]
    [InlineData("", DetailLevel.Connectivity)]
    [InlineData("Summary", DetailLevel.Summary)]
    [InlineData("FULL", DetailLevel.Full)]
    [InlineData("pins", DetailLevel.Connectivity)]
    public void DetailLevel_normalizes_aliases(string? value, string expected)
    {
        Assert.Equal(expected, DetailLevel.Normalize(value));
    }

    [Fact]
    public void DetailLevel_rejects_unknown()
    {
        Assert.Throws<ArgumentException>(() => DetailLevel.Normalize("everything"));
    }
}
