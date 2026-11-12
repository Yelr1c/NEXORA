using Microsoft.Win32;
using Nexora.Core.Platform;

namespace Nexora.Platform.Windows.Services;

/// <summary>Registry access through the 64-bit view. Values are never expanded so they round-trip exactly.</summary>
public sealed class WindowsRegistryAccessor : IRegistryAccessor
{
    public RegistryValue? GetValue(RegistryRoot root, string keyPath, string valueName)
    {
        using var key = Open(root, keyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null)
        {
            return null;
        }

        return key.GetValueKind(valueName) switch
        {
            RegistryValueKind.DWord => RegistryValue.FromDWord(Convert.ToInt32(data, System.Globalization.CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => RegistryValue.FromQWord(Convert.ToInt64(data, System.Globalization.CultureInfo.InvariantCulture)),
            RegistryValueKind.String => RegistryValue.FromString((string)data),
            RegistryValueKind.ExpandString => RegistryValue.FromExpandString((string)data),
            RegistryValueKind.Binary => RegistryValue.FromBinary((byte[])data),
            RegistryValueKind.MultiString => RegistryValue.FromMultiString((string[])data),
            _ => null,
        };
    }

    public void SetValue(RegistryRoot root, string keyPath, string valueName, RegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var key = BaseKey(root).CreateSubKey(keyPath, writable: true)
            ?? throw new UnauthorizedAccessException($"Registry key could not be opened for writing.");
        switch (value.Type)
        {
            case RegistryValueType.DWord:
                key.SetValue(valueName, value.Data, RegistryValueKind.DWord);
                break;
            case RegistryValueType.QWord:
                key.SetValue(valueName, value.Data, RegistryValueKind.QWord);
                break;
            case RegistryValueType.String:
                key.SetValue(valueName, value.Data, RegistryValueKind.String);
                break;
            case RegistryValueType.ExpandString:
                key.SetValue(valueName, value.Data, RegistryValueKind.ExpandString);
                break;
            case RegistryValueType.Binary:
                key.SetValue(valueName, value.Data, RegistryValueKind.Binary);
                break;
            case RegistryValueType.MultiString:
                key.SetValue(valueName, value.Data, RegistryValueKind.MultiString);
                break;
            default:
                throw new NotSupportedException(value.Type.ToString());
        }
    }

    public bool DeleteValue(RegistryRoot root, string keyPath, string valueName)
    {
        using var key = Open(root, keyPath, writable: true);
        if (key?.GetValue(valueName) is null)
        {
            return false;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    public bool KeyExists(RegistryRoot root, string keyPath)
    {
        using var key = Open(root, keyPath, writable: false);
        return key is not null;
    }

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, string keyPath)
    {
        using var key = Open(root, keyPath, writable: false);
        return key?.GetValueNames() ?? [];
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath)
    {
        using var key = Open(root, keyPath, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    private static RegistryKey? Open(RegistryRoot root, string keyPath, bool writable)
    {
        try
        {
            return BaseKey(root).OpenSubKey(keyPath, writable);
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (!writable)
        {
            return null;
        }
    }

    private static RegistryKey BaseKey(RegistryRoot root) => RegistryKey.OpenBaseKey(root switch
    {
        RegistryRoot.CurrentUser => RegistryHive.CurrentUser,
        RegistryRoot.LocalMachine => RegistryHive.LocalMachine,
        RegistryRoot.Users => RegistryHive.Users,
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    }, RegistryView.Registry64);
}
