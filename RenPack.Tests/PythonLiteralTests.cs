using System.Collections;
using FluentAssertions;
using RenPack.Services;
using Xunit;

namespace RenPack.Tests;

public sealed class PythonLiteralTests
{
    [Fact]
    public void Parses_scalars()
    {
        PythonLiteral.Parse("42").Should().Be(42L);
        PythonLiteral.Parse("-7").Should().Be(-7L);
        PythonLiteral.Parse("3.14").Should().Be(3.14);
        PythonLiteral.Parse("-2.5e10").Should().Be(-2.5e10);
        PythonLiteral.Parse("True").Should().Be(true);
        PythonLiteral.Parse("False").Should().Be(false);
        PythonLiteral.Parse("None").Should().BeNull();
    }

    [Fact]
    public void Parses_strings_with_escapes()
    {
        PythonLiteral.Parse("\"hello\"").Should().Be("hello");
        PythonLiteral.Parse("'world'").Should().Be("world");
        PythonLiteral.Parse("\"line\\nbreak\"").Should().Be("line\nbreak");
        PythonLiteral.Parse("\"tab\\there\"").Should().Be("tab\there");
        PythonLiteral.Parse("'quote\\'inside'").Should().Be("quote'inside");
    }

    [Fact]
    public void Parses_flat_list()
    {
        var result = PythonLiteral.Parse("[1, 2, 3]");
        result.Should().BeAssignableTo<IList>();
        var list = (IList)result!;
        list.Count.Should().Be(3);
        list[0].Should().Be(1L);
        list[2].Should().Be(3L);
    }

    [Fact]
    public void Parses_mixed_list()
    {
        var list = (IList)PythonLiteral.Parse("[True, \"hello\", None, 42]")!;
        list.Cast<object?>().Should().Equal(new object?[] { true, "hello", null, 42L });
    }

    [Fact]
    public void Parses_empty_list_and_dict()
    {
        ((IList)PythonLiteral.Parse("[]")!).Count.Should().Be(0);
        ((IDictionary)PythonLiteral.Parse("{}")!).Count.Should().Be(0);
    }

    [Fact]
    public void Parses_dict_with_string_keys()
    {
        var d = (IDictionary)PythonLiteral.Parse("{\"a\": 1, \"b\": True}")!;
        d["a"].Should().Be(1L);
        d["b"].Should().Be(true);
    }

    [Fact]
    public void Parses_nested_containers()
    {
        var d = (IDictionary)PythonLiteral.Parse("{\"outer\": [1, {\"inner\": [True, False]}]}")!;
        var outer = (IList)d["outer"]!;
        outer.Count.Should().Be(2);
        var innerDict = (IDictionary)outer[1]!;
        var innerList = (IList)innerDict["inner"]!;
        innerList.Cast<object?>().Should().Equal(new object?[] { true, false });
    }

    [Fact]
    public void Parses_tuple()
    {
        var tuple = (object?[])PythonLiteral.Parse("(1, 2, 3)")!;
        tuple.Should().Equal(new object?[] { 1L, 2L, 3L });
    }

    [Fact]
    public void Format_roundtrip_matches()
    {
        object?[] cases =
        {
            42L, -7L, 3.14, true, false, null!, "hello",
            new List<object?> { 1L, 2L, 3L },
            new Dictionary<object, object?> { { "a", 1L }, { "b", true } },
            new object?[] { 1L, 2L },
        };
        foreach (var c in cases)
        {
            string formatted = PythonLiteral.Format(c);
            var parsed = PythonLiteral.Parse(formatted);
            PythonLiteral.Format(parsed).Should().Be(formatted, $"roundtrip for {formatted}");
        }
    }

    [Fact]
    public void Formats_strings_with_quotes_correctly()
    {
        PythonLiteral.Format("hello").Should().Be("'hello'");
        PythonLiteral.Format("has'quote").Should().Be("\"has'quote\"");
        PythonLiteral.Format("has\"double").Should().Be("'has\"double'");
        PythonLiteral.Format("has\nnewline").Should().Be(@"'has\nnewline'");
    }

    [Fact]
    public void Rejects_garbage_input()
    {
        PythonLiteral.TryParse("not python", out _).Should().BeFalse();
        PythonLiteral.TryParse("[1, 2,", out _).Should().BeFalse();
        PythonLiteral.TryParse("{'a':", out _).Should().BeFalse();
    }
}
