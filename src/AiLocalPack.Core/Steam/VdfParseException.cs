namespace AiLocalPack.Core.Steam;

/// <summary>A VDF document is malformed. <see cref="Line"/> and <see cref="Column"/> are 1-based.</summary>
public sealed class VdfParseException : FormatException
{
    public VdfParseException(string reason, int line, int column)
        : base($"Invalid VDF at line {line}, column {column}: {reason}")
    {
        Reason = reason;
        Line = line;
        Column = column;
    }

    public string Reason { get; }

    public int Line { get; }

    public int Column { get; }
}
