using System.Globalization;
using System.Text.Json;

namespace Nexora.Core.Platform;

/// <summary>Registry hives NEXORA is allowed to work with.</summary>
public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
    Users,
}

public enum RegistryValueType
{
    String,
    ExpandString,
    DWord,
    QWord,
    Binary,
    MultiString,
}

/// <summary>An immutable, strongly typed registry value.</summary>
public sealed class RegistryValue : IEquatable<RegistryValue>
{
    private RegistryValue(RegistryValueType type, object data)
    {
        Type = type;
        Data = data;
    }

    public RegistryValueType Type { get; }

    /// <summary>int for DWord, long for QWord, string, byte[] or string[].</summary>
    public object Data { get; }

    public static RegistryValue FromDWord(int value) => new(RegistryValueType.DWord, value);

    public static RegistryValue FromQWord(long value) => new(RegistryValueType.QWord, value);

    public static RegistryValue FromString(string value) => new(RegistryValueType.String, value ?? string.Empty);

    public static RegistryValue FromExpandString(string value) => new(RegistryValueType.ExpandString, value ?? string.Empty);

    public static RegistryValue FromBinary(byte[] value) => new(RegistryValueType.Binary, (byte[])(value ?? []).Clone());

    public static RegistryValue FromMultiString(string[] value) => new(RegistryValueType.MultiString, (string[])(value ?? []).Clone());

    public int AsDWord() => Type == RegistryValueType.DWord ? (int)Data : throw new InvalidOperationException($"Registry value is {Type}, not DWord.");

    public string AsString() => Data switch
    {
        string s => s,
        _ => throw new InvalidOperationException($"Registry value is {Type}, not a string."),
    };

    public byte[] AsBinary() => Type == RegistryValueType.Binary ? (byte[])((byte[])Data).Clone() : throw new InvalidOperationException($"Registry value is {Type}, not Binary.");

    /// <summary>Serialises the value into a string that can be stored in a backup file.</summary>
    public string Serialize() => Type switch
    {
        RegistryValueType.DWord => ((int)Data).ToString(CultureInfo.InvariantCulture),
        RegistryValueType.QWord => ((long)Data).ToString(CultureInfo.InvariantCulture),
        RegistryValueType.String or RegistryValueType.ExpandString => (string)Data,
        RegistryValueType.Binary => Convert.ToBase64String((byte[])Data),
        RegistryValueType.MultiString => JsonSerializer.Serialize((string[])Data),
        _ => throw new NotSupportedException(Type.ToString()),
    };

    public static RegistryValue Deserialize(RegistryValueType type, string serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        return type switch
        {
            RegistryValueType.DWord => FromDWord(int.Parse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            RegistryValueType.QWord => FromQWord(long.Parse(serialized, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            RegistryValueType.String => FromString(serialized),
            RegistryValueType.ExpandString => FromExpandString(serialized),
            RegistryValueType.Binary => FromBinary(Convert.FromBase64String(serialized)),
            RegistryValueType.MultiString => FromMultiString(JsonSerializer.Deserialize<string[]>(serialized) ?? []),
            _ => throw new NotSupportedException(type.ToString()),
        };
    }

    public string ToDisplayString() => Type switch
    {
        RegistryValueType.DWord => ((int)Data).ToString(CultureInfo.InvariantCulture),
        RegistryValueType.QWord => ((long)Data).ToString(CultureInfo.InvariantCulture),
        RegistryValueType.Binary => Convert.ToHexString((byte[])Data),
        RegistryValueType.MultiString => string.Join("; ", (string[])Data),
        _ => (string)Data,
    };

    public bool Equals(RegistryValue? other)
    {
        if (other is null || other.Type != Type)
        {
            return false;
        }

        return Type switch
        {
            RegistryValueType.Binary => ((byte[])Data).AsSpan().SequenceEqual((byte[])other.Data),
            RegistryValueType.MultiString => ((string[])Data).SequenceEqual((string[])other.Data, StringComparer.Ordinal),
            _ => Data.Equals(other.Data),
        };
    }

    public override bool Equals(object? obj) => Equals(obj as RegistryValue);

    public override int GetHashCode() => HashCode.Combine(Type, Serialize());

    public override string ToString() => $"{Type}:{ToDisplayString()}";
}

/// <summary>
/// Abstraction over the Windows registry. All optimizations go through this interface so that
/// backup/restore logic can be unit-tested and so access is centralised and auditable.
/// </summary>
public interface IRegistryAccessor
{
    RegistryValue? GetValue(RegistryRoot root, string keyPath, string valueName);

    void SetValue(RegistryRoot root, string keyPath, string valueName, RegistryValue value);

    /// <returns>true if a value was deleted.</returns>
    bool DeleteValue(RegistryRoot root, string keyPath, string valueName);

    bool KeyExists(RegistryRoot root, string keyPath);

    IReadOnlyList<string> GetValueNames(RegistryRoot root, string keyPath);

    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath);
}
