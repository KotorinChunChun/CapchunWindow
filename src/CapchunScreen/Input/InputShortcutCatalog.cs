using Microsoft.VisualBasic.FileIO;
using System.Text;

namespace CapchunScreen.Input;

internal sealed record InputShortcut(string Combination, string Action, InputModifiers Modifiers, bool PlainKey)
{
    internal bool DefaultEnabled(bool includePlainKeys) => Combination != InputShortcutCatalog.ReservedHotkey
        && (includePlainKeys || !PlainKey || (Modifiers & (InputModifiers.Control | InputModifiers.Alt | InputModifiers.Windows)) != 0);
}

internal static class InputShortcutCatalog
{
    internal const string ReservedHotkey = "Ctrl+Shift+F9";
    internal static readonly string[] MouseActions = ["L-Click", "R-Click", "M-Click", "X1-Click", "X2-Click", "Wheel-Up", "Wheel-Down", "Wheel-Left", "Wheel-Right"];
    internal static readonly IReadOnlyList<InputShortcut> All = Build();

    private static IReadOnlyList<InputShortcut> Build()
    {
        List<InputShortcut> result = [];
        for (int key = 0; key < 256; key++)
        {
            if (InputBuffer.GetKeyName(key) is not { } name) continue;
            for (int modifier = 0; modifier < 16; modifier++)
                result.Add(new(Combine(name, (InputModifiers)modifier), name, (InputModifiers)modifier, InputBuffer.IsPlainKey(key)));
        }
        foreach (string action in MouseActions)
            for (int modifier = 0; modifier < 16; modifier++)
                result.Add(new(Combine(action, (InputModifiers)modifier), action, (InputModifiers)modifier, false));
        return result;
    }

    internal static string Combine(string action, InputModifiers modifiers)
    {
        List<string> parts = [];
        if (modifiers.HasFlag(InputModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(InputModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(InputModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(InputModifiers.Windows)) parts.Add("Win");
        parts.Add(action);
        return string.Join('+', parts);
    }

    internal static string? Normalize(string value)
    {
        string[] parts = value.Trim().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        InputModifiers modifiers = InputModifiers.None;
        foreach (string part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => InputModifiers.Control, "shift" => InputModifiers.Shift,
                "alt" => InputModifiers.Alt, "win" or "windows" => InputModifiers.Windows, _ => InputModifiers.None
            };
            if (modifier == InputModifiers.None || modifiers.HasFlag(modifier)) return null;
            modifiers |= modifier;
        }
        string? match = All.FirstOrDefault(item => item.Modifiers == InputModifiers.None && item.Action.Equals(parts[^1], StringComparison.OrdinalIgnoreCase))?.Action;
        return match is null ? null : Combine(match, modifiers);
    }

    internal static Dictionary<string, bool> ImportRecording(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string[] row in ReadTsv(path))
        {
            if (row.Length < 2 || row[0].Equals("Key", StringComparison.OrdinalIgnoreCase)) continue;
            string? combo = Normalize(row[0]);
            if (combo is null || combo == ReservedHotkey) continue;
            result[combo] = row[1].Trim() switch
            {
                "1" => true, "0" => false, _ => throw new InvalidDataException($"記録設定が0または1ではありません: {combo}")
            };
        }
        if (result.Count == 0) throw new InvalidDataException("取り込める記録設定がありません。Key、RecordEnabled（またはEnabled）のTSVを選択してください。");
        return result;
    }

    internal static Dictionary<string, string> ImportDescriptions(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string[] row in ReadTsv(path))
        {
            if (row.Length < 2 || Normalize(row[0]) is not { } combo) continue;
            string description = row[1].Trim();
            if (description.Length > 0) result[combo] = description;
        }
        if (result.Count == 0) throw new InvalidDataException("取り込める説明がありません。キー名と説明の2列のTSVを選択してください。");
        return result;
    }

    private static IEnumerable<string[]> ReadTsv(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("設定TSVは4MB以下にしてください。");
        using var parser = new TextFieldParser(path, Encoding.UTF8, detectEncoding: true)
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters("\t");
        int rows = 0;
        while (!parser.EndOfData)
        {
            if (++rows > 20000) throw new InvalidDataException("設定TSVは20,000行以下にしてください。");
            if (parser.ReadFields() is { } fields) yield return fields;
        }
    }

    internal static string MouseDescription(string action) => action switch
    {
        "L-Click" => "左クリック", "R-Click" => "右クリック", "M-Click" => "中クリック",
        "X1-Click" => "戻るボタン", "X2-Click" => "進むボタン", "Wheel-Up" => "ホイール上",
        "Wheel-Down" => "ホイール下", "Wheel-Left" => "ホイール左", "Wheel-Right" => "ホイール右", _ => ""
    };
}
