using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace PizzaHeroClicker.Models;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key plus modifiers, e.g. "Ctrl+Shift+F6". Used for both global hotkeys and KeyPress
/// actions. Serialised to JSON as its display string; an empty string means "not bound".
/// </summary>
[JsonConverter(typeof(KeyComboJsonConverter))]
public readonly record struct KeyCombo(int Vk, KeyMods Mods = KeyMods.None)
{
    public bool IsEmpty => Vk == 0;

    public string Display => IsEmpty ? "None" : ToString();

    public override string ToString()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if (Mods.HasFlag(KeyMods.Ctrl)) sb.Append("Ctrl+");
        if (Mods.HasFlag(KeyMods.Alt)) sb.Append("Alt+");
        if (Mods.HasFlag(KeyMods.Shift)) sb.Append("Shift+");
        if (Mods.HasFlag(KeyMods.Win)) sb.Append("Win+");
        sb.Append(KeyName(Vk));
        return sb.ToString();
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString(); // top-row digits
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.None => $"VK{vk}",
            // Friendlier names than the enum's primary ones; all of these parse back to the same key.
            Key.Return => "Enter",
            Key.Next => "PageDown",
            Key.Prior => "PageUp",
            Key.Capital => "CapsLock",
            Key.Snapshot => "PrintScreen",
            _ => key.ToString(),
        };
    }

    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return true; // empty = unbound

        var mods = KeyMods.None;
        int vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyMods.Ctrl; continue;
                case "alt": mods |= KeyMods.Alt; continue;
                case "shift": mods |= KeyMods.Shift; continue;
                case "win" or "windows": mods |= KeyMods.Win; continue;
            }

            if (vk != 0) return false; // two non-modifier keys
            if (raw.Length == 1 && char.IsAsciiDigit(raw[0]))
                vk = raw[0];
            else if (raw.StartsWith("VK", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw.AsSpan(2), out int n) && n is > 0 and < 256)
                vk = n;
            else if (Enum.TryParse<Key>(raw, ignoreCase: true, out var key) && key != Key.None)
                vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return false;
        }

        if (vk == 0) return false;
        combo = new KeyCombo(vk, mods);
        return true;
    }

    public static KeyCombo Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not a valid key combination.");
}

public sealed class KeyComboJsonConverter : JsonConverter<KeyCombo>
{
    public override KeyCombo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        // An unknown key name unbinds the hotkey instead of failing the whole profile.
        return KeyCombo.TryParse(reader.GetString(), out var combo) ? combo : default;
    }

    public override void Write(Utf8JsonWriter writer, KeyCombo value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
