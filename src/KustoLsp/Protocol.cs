using System.Text.Json;
using StreamJsonRpc;

namespace KustoLsp;

public static class Protocol
{
    public static LocalRpcException Error(int code, string message) => new(message) { ErrorCode = code };
    public static LocalRpcException Invalid(string message) => Error(-32602, message);

    public static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            throw Invalid($"Missing property: {name}");
        return property;
    }

    public static string String(JsonElement value, string name, bool allowEmpty = false)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(property.GetString())))
            throw Invalid($"Expected string: {name}");
        return property.GetString()!;
    }

    public static int Integer(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (!property.TryGetInt32Safe(out var number))
            throw Invalid($"Expected 32-bit integer: {name}");
        return number;
    }

    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    public static JsonElement.ArrayEnumerator Array(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Array)
            throw Invalid($"Expected array: {name}");
        return property.EnumerateArray();
    }

    public static string Uri(JsonElement value)
    {
        var uri = String(value, "uri");
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw Invalid("Expected absolute document URI");
        return uri;
    }
}

public sealed record Position(int Line, int Character);
public sealed record LspRange(Position Start, Position End);

public sealed class TextLines
{
    private readonly string text;
    private readonly int[] starts;
    private readonly int[] ends;

    public TextLines(string text)
    {
        this.text = text;
        var s = new List<int> { 0 };
        var e = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            e.Add(i);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            s.Add(i + 1);
        }
        e.Add(text.Length);
        starts = s.ToArray();
        ends = e.ToArray();
    }

    public int Offset(Position position)
    {
        if (position.Line < 0 || position.Character < 0)
            throw Protocol.Invalid("Position outside document");
        if (position.Line >= starts.Length) return text.Length;
        // LSP positions beyond line length are clamped to the line end.
        var offset = starts[position.Line] + Math.Min(position.Character, ends[position.Line] - starts[position.Line]);
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
            throw Protocol.Invalid("Position splits a UTF-16 surrogate pair");
        return offset;
    }

    public Position PositionAt(int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = System.Array.BinarySearch(starts, offset);
        if (line < 0) line = ~line - 1;
        return new(line, Math.Min(offset, ends[line]) - starts[line]);
    }

    public LspRange Range(int start, int length) => new(PositionAt(start), PositionAt(start + length));

    public IEnumerable<(int Line, int Character, int Length)> Split(int start, int length)
    {
        var end = Math.Min(text.Length, start + length);
        var firstLine = PositionAt(start).Line;
        for (var line = firstLine; line < starts.Length && starts[line] < end; line++)
        {
            var left = Math.Max(start, starts[line]);
            var right = Math.Min(end, ends[line]);
            if (right > left) yield return (line, left - starts[line], right - left);
        }
    }
}
