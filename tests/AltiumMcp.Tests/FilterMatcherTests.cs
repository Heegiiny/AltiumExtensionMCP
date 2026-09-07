using AltiumMcp.Contracts.Model;
using Xunit;

namespace AltiumMcp.Tests;

public class FilterMatcherTests
{
    [Theory]
    [InlineData(null, "R1", true)]
    [InlineData("", "R1", true)]
    [InlineData("   ", "R1", true)]
    [InlineData("r", "R1", true)]              // substring, case-insensitive
    [InlineData("0402", "CAP 20pF 0402", true)]
    [InlineData("LM358", "R1", false)]
    [InlineData("R*", "R10", true)]            // wildcard anchored
    [InlineData("R*", "VR1", false)]
    [InlineData("U1?", "U12", true)]
    [InlineData("U1?", "U1", false)]
    [InlineData("*_N", "USB_DP_N", true)]
    [InlineData("VCC*", "16M_IN", false)]
    [InlineData("C.1", "C.1", true)]           // regex metachar in plain filter is literal
    [InlineData("C.1", "CX1", false)]
    public void Matches_single_field(string? filter, string field, bool expected)
    {
        Assert.Equal(expected, FilterMatcher.Matches(filter, field));
    }

    [Fact]
    public void Matches_any_of_several_fields_and_skips_nulls()
    {
        Assert.True(FilterMatcher.Matches("qfn", null, "U1", "CSR-QFN32"));
        Assert.False(FilterMatcher.Matches("qfn", null, "U1", null));
    }
}
