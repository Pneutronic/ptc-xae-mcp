using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>signal testbench: the condition parser and the way a condition applies to a typed value.</summary>
public class SignalConditionTests
{
    private static SignalCondition Parse(string text)
    {
        Assert.True(SignalConditionParser.TryParse(text, out var c, out string error), error);
        return c;
    }

    [Theory]
    [InlineData("MAIN.nCount > 5", "MAIN.nCount", "Gt", "5")]
    [InlineData("MAIN.nCount>=5", "MAIN.nCount", "Ge", "5")]
    [InlineData("GVL.bDone == true", "GVL.bDone", "Eq", "true")]
    [InlineData("GVL.bDone!=FALSE", "GVL.bDone", "Ne", "FALSE")]
    [InlineData("MAIN.fValue < -1.5", "MAIN.fValue", "Lt", "-1.5")]
    [InlineData("MAIN.fValue <= 2e3", "MAIN.fValue", "Le", "2e3")]
    [InlineData("MAIN.sText == 'hello world'", "MAIN.sText", "Eq", "hello world")]
    [InlineData("MAIN.sText == \"\"", "MAIN.sText", "Eq", "")]
    [InlineData("MAIN.aCyl[2,1].bOut == 1", "MAIN.aCyl[2,1].bOut", "Eq", "1")]
    [InlineData("MAIN.eMode == E_Mode_Run", "MAIN.eMode", "Eq", "E_Mode_Run")]
    public void Parse_ReadsSymbolOperatorAndValue(string text, string symbol, string op, string operand)
    {
        var c = Parse(text);
        Assert.Equal(symbol, c.Symbol);
        Assert.Equal(Enum.Parse<SignalOp>(op), c.Op);
        Assert.Equal(operand, c.Operand);
    }

    [Theory]
    [InlineData("MAIN.nStep changed")]
    [InlineData("  MAIN.nStep   CHANGED ")]
    public void Parse_Changed_TakesNoValue(string text)
    {
        var c = Parse(text);
        Assert.Equal(SignalOp.Changed, c.Op);
        Assert.Equal("MAIN.nStep", c.Symbol);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("== 5", "does not start with a symbol")]
    [InlineData("MAIN.nCount", "no operator")]
    [InlineData("MAIN.nCount =", "no operator")]
    [InlineData("MAIN.nCount >", "no value")]
    [InlineData("MAIN.nCount == ", "no value")]
    [InlineData("MAIN.nCount changed 5", "takes no value")]
    [InlineData("MAIN.nCount ~ 5", "no operator")]
    public void Parse_RejectsMalformedConditions_NamingTheGrammar(string text, string expected)
    {
        Assert.False(SignalConditionParser.TryParse(text, out _, out string error));
        Assert.Contains(expected, error);
        Assert.Contains("changed", error + SignalConditionParser.Grammar);
    }

    [Fact]
    public void TryParseAll_RefusesNoneAndTooMany()
    {
        Assert.False(SignalConditionParser.TryParseAll(null, out _, out string none));
        Assert.Contains("required", none);
        var nine = Enumerable.Range(1, SignalLimits.MaxConditions + 1).Select(i => $"MAIN.n{i} == 1").ToList();
        Assert.False(SignalConditionParser.TryParseAll(nine, out _, out string many));
        Assert.Contains($"At most {SignalLimits.MaxConditions}", many);
    }

    [Fact]
    public void TryParseAll_NamesTheFirstBadCondition()
    {
        Assert.False(SignalConditionParser.TryParseAll(["MAIN.a == 1", "MAIN.b"], out _, out string error));
        Assert.Contains("'MAIN.b'", error);
    }

    // ---- types ----

    private static WatchedSymbol Bool(bool v = false) => ScriptedAdsWatch.Sym("B", "BOOL", ScriptedAdsWatch.Bool(v));
    private static WatchedSymbol Uint(long v = 0) => ScriptedAdsWatch.Sym("N", "UINT", ScriptedAdsWatch.Num(v));
    private static WatchedSymbol RealSym(double v = 0) => ScriptedAdsWatch.Sym("R", "REAL", ScriptedAdsWatch.Real(v));
    private static WatchedSymbol Text() => ScriptedAdsWatch.Sym("S", "STRING", ScriptedAdsWatch.Text("abc"));
    private static WatchedSymbol Mode() => ScriptedAdsWatch.Sym("E", "E_Mode", ScriptedAdsWatch.EnumValue(1, "Run"), wire: "INT", isEnum: true);

    private static bool Holds(string text, WatchedSymbol s, SignalReading value)
    {
        var c = Parse(text);
        Assert.Null(SignalCompare.Validate(c, s));
        return SignalCompare.Test(c, s, value);
    }

    [Fact]
    public void Bool_ComparesAgainstTrueFalseOneZero()
    {
        Assert.True(Holds("B == true", Bool(), ScriptedAdsWatch.Bool(true)));
        Assert.True(Holds("B == 1", Bool(), ScriptedAdsWatch.Bool(true)));
        Assert.False(Holds("B == TRUE", Bool(), ScriptedAdsWatch.Bool(false)));
        Assert.True(Holds("B != 0", Bool(), ScriptedAdsWatch.Bool(true)));
    }

    [Fact]
    public void Bool_RefusesAnOrderedOperatorAndAnUnknownWord()
    {
        Assert.Contains("only == and !=", SignalCompare.Validate(Parse("B > 1"), Bool()));
        Assert.Contains("not a BOOL value", SignalCompare.Validate(Parse("B == on"), Bool()));
    }

    [Fact]
    public void Number_ComparesByValue_AcrossWidths()
    {
        Assert.True(Holds("N > 5", Uint(), ScriptedAdsWatch.Num(6)));
        Assert.False(Holds("N > 5", Uint(), ScriptedAdsWatch.Num(5)));
        Assert.True(Holds("N >= 5", Uint(), ScriptedAdsWatch.Num(5)));
        Assert.True(Holds("N == 5", Uint(), ScriptedAdsWatch.Num(5)));
        Assert.True(Holds("N != 5", Uint(), ScriptedAdsWatch.Num(7)));
        Assert.True(Holds("N < 5.5", Uint(), ScriptedAdsWatch.Num(5)));
    }

    [Fact]
    public void Number_ARealIsComparedAsAReal()
    {
        // 0.1f as a double is 0.10000000149011612: it equals the typed 0.1 only as a REAL.
        var value = ScriptedAdsWatch.Real((double)0.1f);
        Assert.True(Holds("R == 0.1", RealSym(), value));
        Assert.True(Holds("R > 0.09", RealSym(), value));
        var lreal = ScriptedAdsWatch.Sym("R", "LREAL", ScriptedAdsWatch.Real(0.1));
        Assert.True(Holds("R == 0.1", lreal, ScriptedAdsWatch.Real(0.1)));
    }

    [Fact]
    public void Number_ANonFiniteRealIsEqualToNothing()
    {
        var nan = new SignalReading(JsonSerializer.SerializeToElement("NaN"), null);
        Assert.False(Holds("R == 1", RealSym(), nan));
        Assert.True(Holds("R != 1", RealSym(), nan));
        Assert.False(Holds("R > 1", RealSym(), nan));
    }

    [Fact]
    public void Number_RefusesAnOperandThatIsNotANumber()
    {
        Assert.Contains("not a number", SignalCompare.Validate(Parse("N == abc"), Uint()));
    }

    [Fact]
    public void Text_EqualsOrdinal_AndRefusesOrdering()
    {
        Assert.True(Holds("S == abc", Text(), ScriptedAdsWatch.Text("abc")));
        Assert.False(Holds("S == ABC", Text(), ScriptedAdsWatch.Text("abc")));
        Assert.True(Holds("S != ABC", Text(), ScriptedAdsWatch.Text("abc")));
        Assert.Contains("only == and !=", SignalCompare.Validate(Parse("S < b"), Text()));
    }

    [Fact]
    public void Enum_ComparesByMemberNameOrByNumber()
    {
        Assert.True(Holds("E == Run", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        Assert.True(Holds("E == run", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        Assert.False(Holds("E == Stop", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        Assert.True(Holds("E == 1", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        Assert.True(Holds("E > 0", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        Assert.True(Holds("E != Stop", Mode(), ScriptedAdsWatch.EnumValue(1, "Run")));
        // A value that matches no member has no name: only a number can describe it.
        Assert.False(Holds("E == Run", Mode(), ScriptedAdsWatch.EnumValue(9, null)));
        Assert.True(Holds("E == 9", Mode(), ScriptedAdsWatch.EnumValue(9, null)));
    }

    [Fact]
    public void Enum_RefusesAnOrderedOperatorWithAName()
    {
        Assert.Contains("by number", SignalCompare.Validate(Parse("E > Run"), Mode()));
    }

    // ---- bound sets ----

    [Fact]
    public void Bind_NamesTheConditionThatCannotApply()
    {
        var symbols = new[] { Bool(), Uint() };
        var conditions = new[] { Parse("B == true"), Parse("N == abc") };
        var set = SignalConditionSet.Bind(conditions, symbols, all: true, out string error);
        Assert.Null(set);
        Assert.Contains("'N == abc'", error);
    }

    [Fact]
    public void Bind_RefusesASymbolThatWasNotWatched()
    {
        var set = SignalConditionSet.Bind([Parse("X == 1")], [Bool()], all: true, out string error);
        Assert.Null(set);
        Assert.Contains("not among the watched symbols", error);
    }

    [Fact]
    public void SymbolsOf_IsDistinctCaseInsensitive_InOrderOfMention()
    {
        var names = SignalConditionSet.SymbolsOf([Parse("B == 1"), Parse("N > 1"), Parse("b changed")]);
        Assert.Equal(["B", "N"], names);
    }

    [Fact]
    public void Evaluate_AllNeedsEveryCondition_AnyNeedsOne()
    {
        var symbols = new[] { Bool(true), Uint(3) };
        var state = new SignalWatchState(symbols);
        var conditions = new[] { Parse("B == true"), Parse("N > 5") };

        var all = SignalConditionSet.Bind(conditions, symbols, all: true, out _)!;
        var any = SignalConditionSet.Bind(conditions, symbols, all: false, out _)!;
        Assert.False(all.Evaluate(state).Met);
        var (anyMet, each) = any.Evaluate(state);
        Assert.True(anyMet);
        Assert.Equal([true, false], each);
    }

    [Fact]
    public void Changed_IsLatchedFromTheFirstDifferentValue()
    {
        var symbols = new[] { Uint(3) };
        var state = new SignalWatchState(symbols);
        var set = SignalConditionSet.Bind([Parse("N changed")], symbols, all: true, out _)!;
        Assert.False(set.Evaluate(state).Met);

        // The same value again is no change; a different one is, and stays one when it returns.
        Assert.False(state.Apply(new SignalSample(0, TimeSpan.FromMilliseconds(1), 1, ScriptedAdsWatch.Num(3)), out _));
        Assert.False(set.Evaluate(state).Met);
        Assert.True(state.Apply(new SignalSample(0, TimeSpan.FromMilliseconds(2), 2, ScriptedAdsWatch.Num(4)), out var previous));
        Assert.Equal(3, previous.Value.GetInt32());
        Assert.True(set.Evaluate(state).Met);
        state.Apply(new SignalSample(0, TimeSpan.FromMilliseconds(3), 3, ScriptedAdsWatch.Num(3)), out _);
        Assert.True(set.Evaluate(state).Met);
    }
}
