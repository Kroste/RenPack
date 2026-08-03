using FluentAssertions;
using RenPack.Services;
using Razorvine.Pickle;
using System.Collections;
using Xunit;

namespace RenPack.Tests;

public sealed class PicklePatcherTests
{
    private static object? RoundtripEncode(object? value)
    {
        var bytes = PicklePatcher.EncodeValue(value);
        // Terminieren mit STOP damit Razorvine loads() parsen kann.
        var complete = new byte[bytes.Length + 1];
        Array.Copy(bytes, complete, bytes.Length);
        complete[^1] = 0x2E; // STOP
        using var u = new Unpickler();
        return u.loads(complete);
    }

    [Fact]
    public void Encode_and_roundtrip_scalars()
    {
        RoundtripEncode(null).Should().BeNull();
        RoundtripEncode(true).Should().Be(true);
        RoundtripEncode(false).Should().Be(false);
        RoundtripEncode(42).Should().Be(42);
        RoundtripEncode(1234567L).Should().Be(1234567);
        RoundtripEncode(3.14).Should().Be(3.14);
        RoundtripEncode("hello").Should().Be("hello");
    }

    [Fact]
    public void Encode_and_roundtrip_flat_list()
    {
        var list = new List<object?> { 1L, 2L, 3L };
        var result = RoundtripEncode(list);
        result.Should().BeAssignableTo<IList>();
        ((IList)result!).Cast<object?>().Should().Equal(new object?[] { 1, 2, 3 });
    }

    [Fact]
    public void Encode_and_roundtrip_flat_dict()
    {
        var dict = new Dictionary<object, object?> { { "a", 1L }, { "b", true } };
        var result = (IDictionary)RoundtripEncode(dict)!;
        result["a"].Should().Be(1);
        result["b"].Should().Be(true);
    }

    [Fact]
    public void Encode_and_roundtrip_nested_container()
    {
        var nested = new Dictionary<object, object?>
        {
            { "outer", new List<object?> { 1L, 2L, new Dictionary<object, object?> { { "inner", true } } } },
        };
        var result = (IDictionary)RoundtripEncode(nested)!;
        result.Contains("outer").Should().BeTrue();
        var outer = (IList)result["outer"]!;
        outer.Count.Should().Be(3);
        var inner = (IDictionary)outer[2]!;
        inner["inner"].Should().Be(true);
    }

    [Fact]
    public void Encode_and_roundtrip_tuple()
    {
        var tuple = new object?[] { 1L, "two", true };
        var result = RoundtripEncode(tuple);
        var arr = result.Should().BeAssignableTo<object?[]>().Which;
        arr.Should().Equal(new object?[] { 1, "two", true });
    }

    [Fact]
    public void Encode_and_roundtrip_empty_containers()
    {
        RoundtripEncode(new List<object?>()).Should().BeAssignableTo<IList>()
            .Which.Count.Should().Be(0);
        RoundtripEncode(new Dictionary<object, object?>()).Should().BeAssignableTo<IDictionary>()
            .Which.Count.Should().Be(0);
    }

    [Fact]
    public void MeasureValueLength_for_flat_list()
    {
        var bytes = PicklePatcher.EncodeValue(new List<object?> { 1L, 2L, 3L });
        var len = PicklePatcher.MeasureValueLength(bytes, 0);
        len.Should().Be(bytes.Length);
    }

    [Fact]
    public void MeasureValueLength_for_flat_dict()
    {
        var bytes = PicklePatcher.EncodeValue(new Dictionary<object, object?>
        {
            { "a", 1L }, { "b", "hello" }
        });
        PicklePatcher.MeasureValueLength(bytes, 0).Should().Be(bytes.Length);
    }

    [Fact]
    public void MeasureValueLength_for_nested_dict()
    {
        var bytes = PicklePatcher.EncodeValue(new Dictionary<object, object?>
        {
            { "outer", new List<object?> { 1L, new Dictionary<object, object?> { { "k", 2L } } } }
        });
        PicklePatcher.MeasureValueLength(bytes, 0).Should().Be(bytes.Length);
    }

    [Fact]
    public void MeasureValueLength_for_empty_containers()
    {
        PicklePatcher.MeasureValueLength(new byte[] { 0x5D }, 0).Should().Be(1); // EMPTY_LIST
        PicklePatcher.MeasureValueLength(new byte[] { 0x7D }, 0).Should().Be(1); // EMPTY_DICT
        PicklePatcher.MeasureValueLength(new byte[] { 0x29 }, 0).Should().Be(1); // EMPTY_TUPLE
    }
}
