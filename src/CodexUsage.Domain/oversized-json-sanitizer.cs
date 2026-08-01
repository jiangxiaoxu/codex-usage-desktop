using System.Buffers;
using System.Text;

namespace CodexUsage.Domain;

internal sealed class OversizedJsonSanitizer
{
    private const int MaximumRetainedStringBytes = 64 * 1024;
    private JsonLexicalMode _mode;
    private JsonNumberState _numberState;
    private JsonLiteral _literal;
    private int _literalIndex;
    private int _unicodeDigitsRemaining;
    private ArrayBufferWriter<byte>? _stringBytes;
    private bool _largeString;

    public bool Consume(
        ReadOnlySpan<byte> input,
        ref int cursor,
        int maximumCursorExclusive,
        ArrayBufferWriter<byte> output)
    {
        while (true)
        {
            if (cursor >= input.Length) return true;
            var current = input[cursor];
            switch (_mode)
            {
                case JsonLexicalMode.Default:
                    if (IsJsonWhitespace(current))
                    {
                        cursor++;
                        return true;
                    }
                    if (current == (byte)'"')
                    {
                        _mode = JsonLexicalMode.String;
                        _stringBytes = new ArrayBufferWriter<byte>(256);
                        _largeString = false;
                        AppendString(input.Slice(cursor, 1));
                        cursor++;
                        return true;
                    }
                    if (current == (byte)'-' || IsDigit(current))
                    {
                        _mode = JsonLexicalMode.Number;
                        _numberState = current switch
                        {
                            (byte)'-' => JsonNumberState.AfterMinus,
                            (byte)'0' => JsonNumberState.Zero,
                            _ => JsonNumberState.Integer,
                        };
                        cursor++;
                        return true;
                    }
                    if (current is (byte)'t' or (byte)'f' or (byte)'n')
                    {
                        _mode = JsonLexicalMode.Literal;
                        _literal = current switch
                        {
                            (byte)'t' => JsonLiteral.True,
                            (byte)'f' => JsonLiteral.False,
                            _ => JsonLiteral.Null,
                        };
                        _literalIndex = 1;
                        cursor++;
                        return true;
                    }
                    if (current is (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)':' or (byte)',')
                    {
                        Write(output, current);
                        cursor++;
                        return true;
                    }
                    return false;

                case JsonLexicalMode.String:
                    var runStart = cursor;
                    while (cursor < maximumCursorExclusive)
                    {
                        var item = input[cursor];
                        if (item < 0x20 || item >= 0x80 || item is (byte)'"' or (byte)'\\') break;
                        cursor++;
                    }
                    if (cursor > runStart)
                    {
                        AppendString(input[runStart..cursor]);
                        return true;
                    }
                    if (current == (byte)'"')
                    {
                        AppendString(input.Slice(cursor, 1));
                        cursor++;
                        WriteCompletedString(output);
                        _mode = JsonLexicalMode.Default;
                        return true;
                    }
                    if (current == (byte)'\\')
                    {
                        AppendString(input.Slice(cursor, 1));
                        cursor++;
                        _mode = JsonLexicalMode.StringEscape;
                        return true;
                    }
                    if (current < 0x20) return false;
                    if (current < 0x80)
                    {
                        AppendString(input.Slice(cursor, 1));
                        cursor++;
                        return true;
                    }
                    var runeStatus = Rune.DecodeFromUtf8(input[cursor..], out _, out var runeBytes);
                    if (runeStatus != OperationStatus.Done) return false;
                    AppendString(input.Slice(cursor, runeBytes));
                    cursor = checked(cursor + runeBytes);
                    return true;

                case JsonLexicalMode.StringEscape:
                    if (current is (byte)'"' or (byte)'\\' or (byte)'/'
                        or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')
                    {
                        AppendString(input.Slice(cursor, 1));
                        cursor++;
                        _mode = JsonLexicalMode.String;
                        return true;
                    }
                    if (current != (byte)'u') return false;
                    AppendString(input.Slice(cursor, 1));
                    cursor++;
                    _unicodeDigitsRemaining = 4;
                    _mode = JsonLexicalMode.StringUnicode;
                    return true;

                case JsonLexicalMode.StringUnicode:
                    if (!IsHexDigit(current)) return false;
                    AppendString(input.Slice(cursor, 1));
                    cursor++;
                    if (--_unicodeDigitsRemaining == 0) _mode = JsonLexicalMode.String;
                    return true;

                case JsonLexicalMode.Number:
                    var numberResult = ConsumeNumberByte(current, ref cursor);
                    if (numberResult == NumberConsumeResult.Consumed) return true;
                    if (numberResult == NumberConsumeResult.Invalid || !IsPotentialJsonTokenStart(current)) return false;
                    Write(output, (byte)'0');
                    _mode = JsonLexicalMode.Default;
                    continue;

                case JsonLexicalMode.Literal:
                    var expected = LiteralBytes(_literal);
                    if (_literalIndex >= expected.Length)
                    {
                        Write(output, expected);
                        _mode = JsonLexicalMode.Default;
                        continue;
                    }
                    if (current != expected[_literalIndex]) return false;
                    _literalIndex++;
                    cursor++;
                    return true;

                default:
                    throw new InvalidOperationException($"Unsupported JSON lexical mode: {_mode}.");
            }
        }
    }

    public bool Complete(ArrayBufferWriter<byte> output)
    {
        if (_mode == JsonLexicalMode.Number)
        {
            if (!NumberCanEnd(_numberState)) return false;
            Write(output, (byte)'0');
            _mode = JsonLexicalMode.Default;
        }
        else if (_mode == JsonLexicalMode.Literal)
        {
            var expected = LiteralBytes(_literal);
            if (_literalIndex != expected.Length) return false;
            Write(output, expected);
            _mode = JsonLexicalMode.Default;
        }
        return _mode == JsonLexicalMode.Default;
    }

    private NumberConsumeResult ConsumeNumberByte(byte value, ref int cursor)
    {
        switch (_numberState)
        {
            case JsonNumberState.AfterMinus:
                if (!IsDigit(value)) return NumberConsumeResult.Invalid;
                _numberState = value == (byte)'0' ? JsonNumberState.Zero : JsonNumberState.Integer;
                cursor++;
                return NumberConsumeResult.Consumed;
            case JsonNumberState.Zero:
                if (IsDigit(value)) return NumberConsumeResult.Invalid;
                return ConsumeNumberSuffix(value, ref cursor);
            case JsonNumberState.Integer:
                if (IsDigit(value))
                {
                    cursor++;
                    return NumberConsumeResult.Consumed;
                }
                return ConsumeNumberSuffix(value, ref cursor);
            case JsonNumberState.Dot:
                if (!IsDigit(value)) return NumberConsumeResult.Invalid;
                _numberState = JsonNumberState.Fraction;
                cursor++;
                return NumberConsumeResult.Consumed;
            case JsonNumberState.Fraction:
                if (IsDigit(value))
                {
                    cursor++;
                    return NumberConsumeResult.Consumed;
                }
                if (value is not ((byte)'e' or (byte)'E')) return NumberConsumeResult.Complete;
                _numberState = JsonNumberState.Exponent;
                cursor++;
                return NumberConsumeResult.Consumed;
            case JsonNumberState.Exponent:
                if (value is (byte)'+' or (byte)'-')
                {
                    _numberState = JsonNumberState.ExponentSign;
                    cursor++;
                    return NumberConsumeResult.Consumed;
                }
                if (!IsDigit(value)) return NumberConsumeResult.Invalid;
                _numberState = JsonNumberState.ExponentDigits;
                cursor++;
                return NumberConsumeResult.Consumed;
            case JsonNumberState.ExponentSign:
                if (!IsDigit(value)) return NumberConsumeResult.Invalid;
                _numberState = JsonNumberState.ExponentDigits;
                cursor++;
                return NumberConsumeResult.Consumed;
            case JsonNumberState.ExponentDigits:
                if (!IsDigit(value)) return NumberConsumeResult.Complete;
                cursor++;
                return NumberConsumeResult.Consumed;
            default:
                throw new InvalidOperationException($"Unsupported JSON number state: {_numberState}.");
        }
    }

    private NumberConsumeResult ConsumeNumberSuffix(byte value, ref int cursor)
    {
        if (value == (byte)'.')
        {
            _numberState = JsonNumberState.Dot;
            cursor++;
            return NumberConsumeResult.Consumed;
        }
        if (value is (byte)'e' or (byte)'E')
        {
            _numberState = JsonNumberState.Exponent;
            cursor++;
            return NumberConsumeResult.Consumed;
        }
        return NumberConsumeResult.Complete;
    }

    private void AppendString(ReadOnlySpan<byte> bytes)
    {
        if (_largeString) return;
        var buffer = _stringBytes ?? throw new InvalidOperationException("JSON string buffer is unavailable.");
        if (buffer.WrittenCount + bytes.Length > MaximumRetainedStringBytes)
        {
            _largeString = true;
            _stringBytes = null;
            return;
        }
        Write(buffer, bytes);
    }

    private void WriteCompletedString(ArrayBufferWriter<byte> output)
    {
        if (_largeString)
            Write(output, "\"\""u8);
        else
            Write(output, (_stringBytes ?? throw new InvalidOperationException("JSON string buffer is unavailable.")).WrittenSpan);
        _stringBytes = null;
        _largeString = false;
    }

    private static bool NumberCanEnd(JsonNumberState value) =>
        value is JsonNumberState.Zero or JsonNumberState.Integer
            or JsonNumberState.Fraction or JsonNumberState.ExponentDigits;

    private static bool IsPotentialJsonTokenStart(byte value) =>
        IsJsonWhitespace(value)
        || value is (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)':' or (byte)','
            or (byte)'"' or (byte)'-' or (byte)'t' or (byte)'f' or (byte)'n'
        || IsDigit(value);

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsHexDigit(byte value) =>
        IsDigit(value) || value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static ReadOnlySpan<byte> LiteralBytes(JsonLiteral value) => value switch
    {
        JsonLiteral.True => "true"u8,
        JsonLiteral.False => "false"u8,
        JsonLiteral.Null => "null"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    private static void Write(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private static void Write(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private enum JsonLexicalMode { Default, String, StringEscape, StringUnicode, Number, Literal }
    private enum JsonLiteral { True, False, Null }
    private enum NumberConsumeResult { Consumed, Complete, Invalid }
    private enum JsonNumberState
    {
        AfterMinus,
        Zero,
        Integer,
        Dot,
        Fraction,
        Exponent,
        ExponentSign,
        ExponentDigits,
    }
}
