using System.Globalization;
using System.Text;

namespace RenPack.Services;

/// <summary>
/// Parser und Formatter fuer Python-Literale — die textuelle Repraesentation
/// die Python's <c>repr()</c> fuer einfache Werte liefert.
///
/// Unterstuetzte Typen:
/// <list type="bullet">
///   <item>Zahlen: <c>42</c>, <c>-3.14</c>, <c>1e10</c></item>
///   <item>Booleans: <c>True</c>, <c>False</c></item>
///   <item>None: <c>None</c></item>
///   <item>Strings: <c>"hallo"</c>, <c>'welt'</c> — inkl. Escape-Sequenzen <c>\n \t \" \\ \x41 ä</c></item>
///   <item>Listen: <c>[1, "zwei", True]</c> — inkl. verschachtelt</item>
///   <item>Dicts: <c>{"key": "value", 1: 2}</c> — inkl. verschachtelt</item>
///   <item>Tuples: <c>(1, 2, 3)</c> — als <c>object[]</c> materialisiert</item>
/// </list>
///
/// Verwendet vom Save-Editor v0.5 fuer Listen/Dict-Editing: der User sieht
/// den Wert als Python-Literal, editiert ihn direkt im Text-Feld,
/// beim Save wird geparst und ueber <see cref="PicklePatcher.EncodeValue"/>
/// als Pickle-Bytes gesplict.
/// </summary>
public static class PythonLiteral
{
    /// <summary>Parst einen Python-Literal-String zu einem .NET-Objekt.
    /// Wirft <see cref="FormatException"/> bei Syntax-Fehlern.</summary>
    public static object? Parse(string input)
    {
        var parser = new Parser(input);
        parser.SkipWhitespace();
        var result = parser.ParseValue();
        parser.SkipWhitespace();
        if (!parser.AtEnd())
            throw new FormatException(
                $"Unerwartetes Zeichen '{parser.Peek()}' an Position {parser.Position} — Ende erwartet.");
        return result;
    }

    /// <summary>Versucht zu parsen — gibt <c>false</c> bei Syntax-Fehler zurueck
    /// statt zu werfen.</summary>
    public static bool TryParse(string input, out object? value)
    {
        try
        {
            value = Parse(input);
            return true;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>Formatiert einen .NET-Wert als Python-Literal (Roundtrip mit
    /// <see cref="Parse"/>).</summary>
    public static string Format(object? value)
    {
        var sb = new StringBuilder();
        FormatTo(sb, value);
        return sb.ToString();
    }

    private static void FormatTo(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null: sb.Append("None"); break;
            case bool b: sb.Append(b ? "True" : "False"); break;
            case string s: FormatString(sb, s); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case short or byte or sbyte or uint or ulong or ushort:
                sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
            case decimal dec:
                sb.Append(dec.ToString(CultureInfo.InvariantCulture)); break;
            case System.Collections.IDictionary dict:
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(", ");
                    FormatTo(sb, e.Key);
                    sb.Append(": ");
                    FormatTo(sb, e.Value);
                    first = false;
                }
                sb.Append('}');
                break;
            case object?[] arr:
                sb.Append('(');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    FormatTo(sb, arr[i]);
                }
                if (arr.Length == 1) sb.Append(','); // Python trailing comma für 1-Tuple
                sb.Append(')');
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                bool first2 = true;
                foreach (var item in list)
                {
                    if (!first2) sb.Append(", ");
                    FormatTo(sb, item);
                    first2 = false;
                }
                sb.Append(']');
                break;
            default:
                sb.Append(value.ToString() ?? "None");
                break;
        }
    }

    private static void FormatString(StringBuilder sb, string s)
    {
        // Python-Style: bevorzugt einzelne Quotes wenn String keine enthaelt,
        // sonst doppelte Quotes.
        bool hasSingle = s.Contains('\'');
        bool hasDouble = s.Contains('"');
        char quote = hasSingle && !hasDouble ? '"' : '\'';
        sb.Append(quote);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote) { sb.Append('\\'); sb.Append(c); }
                    else if (c < 0x20) sb.Append($"\\x{(int)c:x2}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append(quote);
    }

    private sealed class Parser
    {
        private readonly string _src;
        public int Position { get; private set; }

        public Parser(string src) { _src = src; }

        public bool AtEnd() => Position >= _src.Length;
        public char Peek() => AtEnd() ? '\0' : _src[Position];
        public char Next() => _src[Position++];

        public void SkipWhitespace()
        {
            while (!AtEnd() && char.IsWhiteSpace(_src[Position])) Position++;
        }

        public object? ParseValue()
        {
            SkipWhitespace();
            if (AtEnd()) throw new FormatException("Unerwartetes Ende der Eingabe.");
            char c = Peek();

            if (c == '[') return ParseList();
            if (c == '{') return ParseDict();
            if (c == '(') return ParseTuple();
            if (c == '"' || c == '\'') return ParseString();
            if (c == '-' || char.IsDigit(c)) return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseKeyword();
            throw new FormatException($"Unerwartetes Zeichen '{c}' an Position {Position}.");
        }

        private List<object?> ParseList()
        {
            var result = new List<object?>();
            Position++; // consume '['
            SkipWhitespace();
            if (Peek() == ']') { Position++; return result; }
            while (true)
            {
                result.Add(ParseValue());
                SkipWhitespace();
                if (Peek() == ',') { Position++; SkipWhitespace(); if (Peek() == ']') break; continue; }
                if (Peek() == ']') break;
                throw new FormatException($"Erwartet ',' oder ']' an Position {Position}.");
            }
            Position++; // consume ']'
            return result;
        }

        private Dictionary<object, object?> ParseDict()
        {
            var result = new Dictionary<object, object?>();
            Position++; // consume '{'
            SkipWhitespace();
            if (Peek() == '}') { Position++; return result; }
            while (true)
            {
                var key = ParseValue();
                if (key is null) throw new FormatException($"Dict-Key darf nicht None sein (Position {Position}).");
                SkipWhitespace();
                if (Peek() != ':') throw new FormatException($"Erwartet ':' an Position {Position}.");
                Position++;
                var value = ParseValue();
                result[key] = value;
                SkipWhitespace();
                if (Peek() == ',') { Position++; SkipWhitespace(); if (Peek() == '}') break; continue; }
                if (Peek() == '}') break;
                throw new FormatException($"Erwartet ',' oder '}}' an Position {Position}.");
            }
            Position++; // consume '}'
            return result;
        }

        private object?[] ParseTuple()
        {
            var items = new List<object?>();
            Position++; // consume '('
            SkipWhitespace();
            if (Peek() == ')') { Position++; return Array.Empty<object?>(); }
            while (true)
            {
                items.Add(ParseValue());
                SkipWhitespace();
                if (Peek() == ',')
                {
                    Position++;
                    SkipWhitespace();
                    if (Peek() == ')') break;
                    continue;
                }
                if (Peek() == ')') break;
                throw new FormatException($"Erwartet ',' oder ')' an Position {Position}.");
            }
            Position++;
            return items.ToArray();
        }

        private string ParseString()
        {
            char quote = Next();
            var sb = new StringBuilder();
            while (!AtEnd() && Peek() != quote)
            {
                char c = Next();
                if (c == '\\' && !AtEnd())
                {
                    char esc = Next();
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case '0': sb.Append('\0'); break;
                        case 'x':
                            sb.Append(ReadHex(2));
                            break;
                        case 'u':
                            sb.Append(ReadHex(4));
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            if (AtEnd()) throw new FormatException("Unabgeschlossener String.");
            Position++; // consume closing quote
            return sb.ToString();
        }

        private char ReadHex(int digits)
        {
            if (Position + digits > _src.Length)
                throw new FormatException($"Erwartet {digits} Hex-Ziffern.");
            var hex = _src.Substring(Position, digits);
            Position += digits;
            return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private object ParseNumber()
        {
            int start = Position;
            if (Peek() == '-') Position++;
            while (!AtEnd() && (char.IsDigit(Peek()) || Peek() == '.' || Peek() == 'e' || Peek() == 'E' || Peek() == '+' || Peek() == '-'))
                Position++;
            string text = _src[start..Position];
            if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
                return double.Parse(text, CultureInfo.InvariantCulture);
            return long.Parse(text, CultureInfo.InvariantCulture);
        }

        private object? ParseKeyword()
        {
            int start = Position;
            while (!AtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_')) Position++;
            string word = _src[start..Position];
            return word switch
            {
                "True" => true,
                "False" => false,
                "None" => null,
                _ => throw new FormatException($"Unbekanntes Literal '{word}' an Position {start}."),
            };
        }
    }
}
