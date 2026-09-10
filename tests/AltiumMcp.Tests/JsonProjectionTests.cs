using System.Text.Json;
using AltiumMcp.Server;
using Xunit;

namespace AltiumMcp.Tests;

public class JsonProjectionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void No_fields_returns_input_unchanged()
    {
        JsonElement input = Parse("{\"a\":1,\"b\":2}");
        Assert.Equal("{\"a\":1,\"b\":2}", JsonProjection.Apply(input, null).GetRawText());
        Assert.Equal("{\"a\":1,\"b\":2}", JsonProjection.Apply(input, new string[0]).GetRawText());
    }

    [Fact]
    public void Projects_list_items_and_keeps_metadata()
    {
        JsonElement input = Parse("{\"total\":2,\"offset\":0,\"components\":[{\"id\":\"A\",\"designator\":\"R1\",\"comment\":\"10k\"},{\"id\":\"B\",\"designator\":\"R2\",\"comment\":\"1k\"}]}");
        string result = JsonProjection.Apply(input, new[] { "designator" }).GetRawText();
        Assert.Equal("{\"total\":2,\"offset\":0,\"components\":[{\"designator\":\"R1\"},{\"designator\":\"R2\"}]}", result);
    }

    [Fact]
    public void Projects_top_level_object_when_no_matching_list()
    {
        JsonElement input = Parse("{\"summary\":{\"id\":\"A\",\"designator\":\"U1\"},\"pins\":[{\"number\":\"1\",\"net\":\"GND\"}],\"parameters\":{\"Value\":\"x\"}}");
        string result = JsonProjection.Apply(input, new[] { "summary", "pins" }).GetRawText();
        Assert.Equal("{\"summary\":{\"id\":\"A\",\"designator\":\"U1\"},\"pins\":[{\"number\":\"1\",\"net\":\"GND\"}]}", result);
    }

    [Fact]
    public void Nested_selection_reduces_parent_object_and_arrays()
    {
        JsonElement input = Parse("{\"summary\":{\"id\":\"A\",\"designator\":\"U1\",\"comment\":\"c\"},\"pins\":[{\"number\":\"1\",\"name\":\"VCC\",\"net\":\"GND\"}]}");
        string result = JsonProjection.Apply(input, new[] { "summary.designator", "pins.net" }).GetRawText();
        Assert.Equal("{\"summary\":{\"designator\":\"U1\"},\"pins\":[{\"net\":\"GND\"}]}", result);
    }

    [Fact]
    public void Field_names_are_case_insensitive()
    {
        JsonElement input = Parse("{\"items\":[{\"netId\":\"GND\",\"pinCount\":5}]}");
        string result = JsonProjection.Apply(input, new[] { "NETID" }).GetRawText();
        Assert.Equal("{\"items\":[{\"netId\":\"GND\"}]}", result);
    }
}
