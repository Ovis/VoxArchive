using System.Globalization;
using System.Windows.Input;

namespace VoxArchive.Wpf;

internal static class KeyboardShortcutHelper
{
    public const string DefaultStartStopHotkey = "Ctrl+F12";

    public static bool TryParseAndNormalize(string? input, out KeyGesture? gesture, out string normalized)
    {
        gesture = null;
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            var converter = new KeyGestureConverter();
            if (converter.ConvertFromInvariantString(input.Trim()) is not KeyGesture parsed)
            {
                return false;
            }

            if (parsed.Key == Key.None || IsModifierKey(parsed.Key))
            {
                return false;
            }

            gesture = parsed;
            normalized = ToConfigText(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryBuildFromInput(ModifierKeys modifiers, Key key, out string normalized)
    {
        normalized = string.Empty;
        if (key == Key.None || IsModifierKey(key))
        {
            return false;
        }

        try
        {
            var gesture = new KeyGesture(key, modifiers);
            normalized = ToConfigText(gesture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
    }

    private static string ToConfigText(KeyGesture gesture)
    {
        var parts = new List<string>(4);

        if (gesture.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        var keyText = new KeyConverter().ConvertToString(null, CultureInfo.InvariantCulture, gesture.Key) ?? gesture.Key.ToString();
        parts.Add(keyText);

        return string.Join("+", parts);
    }
}


