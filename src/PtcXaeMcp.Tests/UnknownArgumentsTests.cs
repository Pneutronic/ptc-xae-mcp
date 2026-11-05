using System.Text.Json;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>An argument a tool does not declare is refused instead of being dropped without a word.</summary>
public class UnknownArgumentsTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{ "type": "object", "properties": { "kind": {}, "parentPath": {}, "name": {}, "declaration": {} }, "required": ["kind"] }""").RootElement;

    [Fact]
    public void Find_ListsOnlyTheUndeclaredNames_InCallOrder() =>
        Assert.Equal(["body", "foo"], UnknownArguments.Find(Schema, ["kind", "body", "name", "foo"]));

    [Fact]
    public void Find_ComparesCaseInsensitively_LikeTheBinding() =>
        Assert.Empty(UnknownArguments.Find(Schema, ["Kind", "PARENTPATH", "declaration"]));

    [Fact]
    public void Find_ASchemaWithoutProperties_TakesNoArguments() =>
        Assert.Equal(["x"], UnknownArguments.Find(JsonDocument.Parse("""{ "type": "object" }""").RootElement, ["x"]));

    [Fact]
    public void Message_NamesTheUnknownAndTheValidParameters()
    {
        string text = UnknownArguments.Message("twincat_plc_create", ["body"], UnknownArguments.Parameters(Schema));

        Assert.StartsWith("[Error] twincat_plc_create has no parameter 'body'", text);
        Assert.Contains("its parameters are: kind, parentPath, name, declaration", text);
        Assert.Contains("Nothing was done", text);
    }

    [Fact]
    public void Message_ForAToolWithoutParameters_SaysSo() =>
        Assert.Contains("it takes no parameters", UnknownArguments.Message("twincat_get_status", ["x"], []));
}
