namespace CapchunScreen.Input;

[Flags]
internal enum InputModifiers { None = 0, Control = 1, Shift = 2, Alt = 4, Windows = 8 }

/// <summary>フレーム間の短い入力を保持し、消費後も押下中の入力を扱う。</summary>
internal sealed class InputBuffer(bool includePlainKeys = false)
{
    private const int RecordingHotkeyKey = 0x78;
    private readonly object _gate = new();
    private readonly HashSet<int> _heldKeys = [];
    private readonly Dictionary<(int Key, InputModifiers Modifiers), KeyStroke> _pendingKeys = [];
    private readonly Dictionary<string, MouseStroke> _pendingMouse = [];
    private readonly HashSet<string> _heldMouse = [];
    private bool _suppressRecordingHotkey;
    private long _mouseSequence;

    private sealed record KeyStroke(int Key, InputModifiers Modifiers, nint Foreground);
    private sealed record MouseStroke(string Action, Point Position, InputModifiers Modifiers, nint Foreground, long Sequence);

    internal void KeyChanged(int key, bool pressed, nint foreground)
    {
        lock (_gate)
        {
            if (!pressed)
            {
                _heldKeys.Remove(key);
                if (key == RecordingHotkeyKey) _suppressRecordingHotkey = false;
                return;
            }

            _heldKeys.Add(key);
            var modifiers = GetModifiers();
            if (!_suppressRecordingHotkey && _heldKeys.Contains(RecordingHotkeyKey)
                && modifiers.HasFlag(InputModifiers.Control | InputModifiers.Shift))
            {
                // 修飾キーを先に離しても、同じ押下と自動リピートはキーアップまで除外する。
                _suppressRecordingHotkey = true;
                foreach (var pending in _pendingKeys.Keys.Where(item => item.Key == RecordingHotkeyKey).ToArray())
                    _pendingKeys.Remove(pending);
            }
            if (IsModifier(key)) return;
            if (IsAcceptedKey(key, modifiers))
                _pendingKeys[(key, modifiers)] = new(key, modifiers, foreground);
        }
    }

    internal void MouseButtonChanged(string action, bool pressed, Point position, nint foreground)
    {
        lock (_gate)
        {
            if (!pressed)
            {
                _heldMouse.Remove(action);
                return;
            }

            _heldMouse.Add(action);
            AddMouseStroke(action, position, foreground);
        }
    }

    internal void WheelMoved(bool horizontal, int delta, Point position, nint foreground)
    {
        if (delta == 0) return;
        lock (_gate)
        {
            string action = horizontal
                ? delta > 0 ? "Wheel-Right" : "Wheel-Left"
                : delta > 0 ? "Wheel-Up" : "Wheel-Down";
            AddMouseStroke(action, position, foreground);
        }
    }

    private void AddMouseStroke(string action, Point position, nint foreground) =>
        _pendingMouse[action] = new(action, position, GetModifiers(), foreground, ++_mouseSequence);

    internal InputSample Consume(Rectangle bounds, Point currentPosition, nint foreground, nint targetWindow,
        Func<nint, nint, bool> foregroundMatches)
    {
        lock (_gate)
        {
            bool AcceptForeground(nint window) => foregroundMatches(window, targetWindow);
            var keyNames = new HashSet<string>(StringComparer.Ordinal);
            InputModifiers modifiers = InputModifiers.None;
            void AddKey(KeyStroke stroke)
            {
                if (!AcceptForeground(stroke.Foreground) || !IsAcceptedKey(stroke.Key, stroke.Modifiers)) return;
                string? name = GetKeyName(stroke.Key);
                if (name is null) return;
                keyNames.Add(name);
                modifiers |= stroke.Modifiers;
            }

            foreach (var stroke in _pendingKeys.Values) AddKey(stroke);
            var heldModifiers = GetModifiers();
            foreach (int key in _heldKeys)
                if (!IsModifier(key)) AddKey(new(key, heldModifiers, foreground));

            var actions = new HashSet<string>(StringComparer.Ordinal);
            Point position = currentPosition;
            MouseStroke? latest = null;
            foreach (var stroke in _pendingMouse.Values)
            {
                if (!bounds.Contains(stroke.Position)) continue;
                if (targetWindow != 0 && !AcceptForeground(stroke.Foreground)) continue;
                actions.Add(stroke.Action);
                if (AcceptForeground(stroke.Foreground)) modifiers |= stroke.Modifiers;
                if (latest is null || stroke.Sequence > latest.Sequence) latest = stroke;
            }
            // 短いクリック後にカーソルが移動しても、イベント地点を失わない。
            if (latest is not null) position = latest.Position;
            if (bounds.Contains(currentPosition) && (targetWindow == 0 || AcceptForeground(foreground)))
            {
                foreach (string action in _heldMouse) actions.Add(action);
                if (_heldMouse.Count > 0 && AcceptForeground(foreground)) modifiers |= heldModifiers;
            }

            _pendingKeys.Clear();
            _pendingMouse.Clear();
            List<string> keys = [];
            if (modifiers.HasFlag(InputModifiers.Control)) keys.Add("Ctrl");
            if (modifiers.HasFlag(InputModifiers.Shift)) keys.Add("Shift");
            if (modifiers.HasFlag(InputModifiers.Alt)) keys.Add("Alt");
            if (modifiers.HasFlag(InputModifiers.Windows)) keys.Add("Win");
            keys.AddRange(keyNames.Order(StringComparer.Ordinal));
            bool inside = bounds.Contains(position);
            return new(inside ? position.X - bounds.X : -1, inside ? position.Y - bounds.Y : -1,
                actions.Count == 0 ? "None" : string.Join(',', actions.OrderBy(MouseOrder).ThenBy(a => a, StringComparer.Ordinal)),
                keys.Count == 0 ? "None" : string.Join(',', keys));
        }
    }

    private InputModifiers GetModifiers()
    {
        InputModifiers value = InputModifiers.None;
        if (_heldKeys.Overlaps([0x11, 0xA2, 0xA3])) value |= InputModifiers.Control;
        if (_heldKeys.Overlaps([0x10, 0xA0, 0xA1])) value |= InputModifiers.Shift;
        if (_heldKeys.Overlaps([0x12, 0xA4, 0xA5])) value |= InputModifiers.Alt;
        if (_heldKeys.Overlaps([0x5B, 0x5C])) value |= InputModifiers.Windows;
        return value;
    }

    private bool IsAcceptedKey(int key, InputModifiers modifiers)
    {
        // 録画開始・停止に使う共通ホットキーは操作履歴へ出さない。
        if (key == RecordingHotkeyKey
            && (_suppressRecordingHotkey || modifiers.HasFlag(InputModifiers.Control | InputModifiers.Shift))) return false;
        // Shiftだけの修飾は大文字・記号などの通常文字入力なので、既定では記録しない。
        const InputModifiers shortcutModifiers = InputModifiers.Control | InputModifiers.Alt | InputModifiers.Windows;
        return GetKeyName(key) is not null && (includePlainKeys || !IsPlainKey(key) || (modifiers & shortcutModifiers) != InputModifiers.None);
    }

    private static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private static bool IsPlainKey(int key) => key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2;
    private static int MouseOrder(string action) => action switch
    {
        "L-Click" => 0, "R-Click" => 1, "M-Click" => 2, "X1-Click" => 3, "X2-Click" => 4,
        "Wheel-Up" => 5, "Wheel-Down" => 6, "Wheel-Left" => 7, "Wheel-Right" => 8, _ => 9
    };

    private static string? GetKeyName(int key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        >= 0x60 and <= 0x69 => $"Num{key - 0x60}",
        0x08 => "BS", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "CapsLock",
        0x1B => "Esc", 0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End",
        0x24 => "Home", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Del", 0x5D => "Apps",
        0x6A => "NumMultiply", 0x6B => "NumAdd", 0x6C => "NumSeparator", 0x6D => "NumSubtract",
        0x6E => "NumDecimal", 0x6F => "NumDivide", 0x90 => "NumLock", 0x91 => "ScrollLock",
        0xBA => "Semicolon", 0xBB => "Plus", 0xBC => "Comma", 0xBD => "Minus", 0xBE => "Period",
        0xBF => "Slash", 0xC0 => "Backtick", 0xDB => "LeftBracket", 0xDC => "Backslash",
        0xDD => "RightBracket", 0xDE => "Quote", 0xDF => "Oem8", 0xE2 => "Oem102", _ => null
    };
}
