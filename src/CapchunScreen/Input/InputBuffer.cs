namespace CapchunScreen.Input;

[Flags]
internal enum InputModifiers { None = 0, Control = 1, Shift = 2, Alt = 4, Windows = 8 }

/// <summary>フレーム間の短い入力を保持し、消費後も押下中の入力を扱う。</summary>
internal sealed class InputBuffer(bool includePlainKeys = false, IReadOnlyDictionary<string, bool>? recordOverrides = null,
    Func<long>? clock = null)
{
    private const int RecordingHotkeyKey = 0x78;
    // フック処理中はOSの非同期キー状態がまだ更新されていないため、直前の押下は照合から外す。
    internal const long StaleKeyGraceMilliseconds = 250;
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    // キー → 最後に押下（自動リピートを含む）を受けた時刻と観測順の連番。
    private readonly Dictionary<int, (long Time, long Sequence)> _heldKeys = [];
    private readonly Dictionary<(int Key, InputModifiers Modifiers), KeyStroke> _pendingKeys = [];
    private readonly Dictionary<string, MouseStroke> _pendingMouse = [];
    private readonly HashSet<string> _heldMouse = [];
    private bool _suppressRecordingHotkey;
    private bool _droppedProtectedKeys;
    private long _mouseSequence;

    private sealed record KeyStroke(int Key, InputModifiers Modifiers, nint Foreground, long Sequence = 0, long Time = 0);
    private sealed record MouseStroke(string Action, Point Position, InputModifiers Modifiers, nint Foreground, long Sequence);

    /// <param name="sequence">パスワード入力の判定に使う観測順の連番。判定しない場合は0。</param>
    internal void KeyChanged(int key, bool pressed, nint foreground, long sequence = 0)
    {
        lock (_gate)
        {
            if (!pressed)
            {
                ReleaseKey(key);
                return;
            }

            long now = _clock();
            _heldKeys[key] = (now, sequence);
            var modifiers = GetModifiers();
            if (!_suppressRecordingHotkey && _heldKeys.ContainsKey(RecordingHotkeyKey)
                && modifiers.HasFlag(InputModifiers.Control | InputModifiers.Shift))
            {
                // 修飾キーを先に離しても、同じ押下と自動リピートはキーアップまで除外する。
                _suppressRecordingHotkey = true;
                foreach (var pending in _pendingKeys.Keys.Where(item => item.Key == RecordingHotkeyKey).ToArray())
                    _pendingKeys.Remove(pending);
            }
            if (IsModifier(key)) return;
            if (IsAcceptedKey(key, modifiers))
                _pendingKeys[(key, modifiers)] = new(key, modifiers, foreground, sequence, now);
        }
    }

    /// <summary>
    /// フォーカスを動かし得るキーか。修飾なしの文字・数字とShiftだけの文字入力は入力欄の中の操作として扱い、
    /// Tab・Enter・Escなどの非文字キーと、Ctrl・Alt・Win付きのキーを区切りにする。
    /// </summary>
    internal bool IsFocusBoundary(int key)
    {
        if (IsModifier(key)) return false;
        lock (_gate)
            return !IsPlainKey(key) || (GetModifiers() & (InputModifiers.Control | InputModifiers.Alt | InputModifiers.Windows)) != 0;
    }

    /// <summary>記録の可否がまだ決まっていないキーのうち、最も新しい観測順の連番。</summary>
    internal long? LatestUnresolvedKeySequence
    {
        get
        {
            lock (_gate)
            {
                long latest = 0;
                foreach (var stroke in _pendingKeys.Values) latest = Math.Max(latest, stroke.Sequence);
                foreach (var (key, held) in _heldKeys) if (!IsModifier(key)) latest = Math.Max(latest, held.Sequence);
                return latest == 0 ? null : latest;
            }
        }
    }

    /// <summary>パスワード入力中または判定不能として、キー操作を記録しなかったことがあるか。</summary>
    internal bool DroppedProtectedKeys { get { lock (_gate) return _droppedProtectedKeys; } }

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

    private void ReleaseKey(int key)
    {
        _heldKeys.Remove(key);
        if (key == RecordingHotkeyKey) _suppressRecordingHotkey = false;
    }

    /// <summary>
    /// キーアップを取りこぼしたキーを、実際の押下状態に合わせて解放する。
    /// 修飾キーが残ると以降の通常文字がショートカットとして記録されるため、毎フレーム照合する。
    /// </summary>
    private void ReleaseStaleKeys(Func<int, bool> keyIsDown)
    {
        long now = _clock();
        foreach (var (key, held) in _heldKeys.ToArray())
            if (now - held.Time >= StaleKeyGraceMilliseconds && !keyIsDown(key)) ReleaseKey(key);
    }

    /// <param name="classify">
    /// キーの連番と時刻からパスワード入力中かを返す。保留のキーは次のフレームまで残し、保護中のキーは記録せずに破棄する。
    /// </param>
    internal InputSample Consume(Rectangle bounds, Point currentPosition, nint foreground, nint targetWindow,
        Func<nint, nint, bool> foregroundMatches, Func<int, bool>? keyIsDown = null,
        Func<long, long, InputProtectionVerdict>? classify = null)
    {
        lock (_gate)
        {
            if (keyIsDown is not null) ReleaseStaleKeys(keyIsDown);
            bool AcceptForeground(nint window) => foregroundMatches(window, targetWindow);
            var keyNames = new HashSet<string>(StringComparer.Ordinal);
            InputModifiers modifiers = InputModifiers.None;
            InputProtectionVerdict Verdict(long sequence, long time)
            {
                var verdict = classify?.Invoke(sequence, time) ?? InputProtectionVerdict.Allowed;
                if (verdict == InputProtectionVerdict.Protected) _droppedProtectedKeys = true;
                return verdict;
            }
            void AddKey(KeyStroke stroke)
            {
                if (!AcceptForeground(stroke.Foreground) || !IsAcceptedKey(stroke.Key, stroke.Modifiers)) return;
                string? name = GetKeyName(stroke.Key);
                if (name is null) return;
                keyNames.Add(name);
                modifiers |= stroke.Modifiers;
            }

            foreach (var (slot, stroke) in _pendingKeys.ToArray())
            {
                // 録画対象の外で押したキーは記録しないため、保護判定にもかけずに捨てる。
                if (!AcceptForeground(stroke.Foreground)) { _pendingKeys.Remove(slot); continue; }
                var verdict = Verdict(stroke.Sequence, stroke.Time);
                if (verdict == InputProtectionVerdict.Pending) continue;
                // 記録しないキーは名前も保持し続けず、この場で捨てる。
                _pendingKeys.Remove(slot);
                if (verdict == InputProtectionVerdict.Allowed) AddKey(stroke);
            }
            var heldModifiers = GetModifiers();
            foreach (var (key, held) in _heldKeys)
                if (!IsModifier(key) && AcceptForeground(foreground) && Verdict(held.Sequence, held.Time) == InputProtectionVerdict.Allowed)
                    AddKey(new(key, heldModifiers, foreground));

            var actions = new HashSet<string>(StringComparer.Ordinal);
            Point position = currentPosition;
            MouseStroke? latest = null;
            foreach (var stroke in _pendingMouse.Values)
            {
                if (!bounds.Contains(stroke.Position)) continue;
                if (targetWindow != 0 && !AcceptForeground(stroke.Foreground)) continue;
                if (!IsMouseAccepted(stroke.Action, stroke.Modifiers)) continue;
                actions.Add(stroke.Action);
                if (AcceptForeground(stroke.Foreground)) modifiers |= stroke.Modifiers;
                if (latest is null || stroke.Sequence > latest.Sequence) latest = stroke;
            }
            // 短いクリック後にカーソルが移動しても、イベント地点を失わない。
            if (latest is not null) position = latest.Position;
            if (bounds.Contains(currentPosition) && (targetWindow == 0 || AcceptForeground(foreground)))
            {
                bool acceptedMouse = false;
                foreach (string action in _heldMouse)
                    if (IsMouseAccepted(action, heldModifiers)) { actions.Add(action); acceptedMouse = true; }
                if (acceptedMouse && AcceptForeground(foreground)) modifiers |= heldModifiers;
            }

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
        if (HeldAny(0x11, 0xA2, 0xA3)) value |= InputModifiers.Control;
        if (HeldAny(0x10, 0xA0, 0xA1)) value |= InputModifiers.Shift;
        if (HeldAny(0x12, 0xA4, 0xA5)) value |= InputModifiers.Alt;
        if (HeldAny(0x5B, 0x5C)) value |= InputModifiers.Windows;
        return value;
    }

    private bool HeldAny(params int[] keys) => keys.Any(_heldKeys.ContainsKey);

    internal string Describe(string action)
    {
        lock (_gate) return InputShortcutCatalog.Combine(action, GetModifiers());
    }

    private bool IsAcceptedKey(int key, InputModifiers modifiers)
    {
        // 録画開始・停止に使う共通ホットキーは操作履歴へ出さない。
        if (key == RecordingHotkeyKey
            && (_suppressRecordingHotkey || modifiers.HasFlag(InputModifiers.Control | InputModifiers.Shift))) return false;
        // Shiftだけの修飾は大文字・記号などの通常文字入力なので、既定では記録しない。
        const InputModifiers shortcutModifiers = InputModifiers.Control | InputModifiers.Alt | InputModifiers.Windows;
        if (GetKeyName(key) is { } name && recordOverrides?.TryGetValue(InputShortcutCatalog.Combine(name, modifiers), out bool enabled) == true)
            return enabled;
        return GetKeyName(key) is not null && (includePlainKeys || !IsPlainKey(key) || (modifiers & shortcutModifiers) != InputModifiers.None);
    }

    private static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
    private bool IsMouseAccepted(string action, InputModifiers modifiers) =>
        recordOverrides?.TryGetValue(InputShortcutCatalog.Combine(action, modifiers), out bool enabled) != true || enabled;

    internal static bool IsPlainKey(int key) => key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2;
    private static int MouseOrder(string action) => action switch
    {
        "L-Click" => 0, "R-Click" => 1, "M-Click" => 2, "X1-Click" => 3, "X2-Click" => 4,
        "Wheel-Up" => 5, "Wheel-Down" => 6, "Wheel-Left" => 7, "Wheel-Right" => 8, _ => 9
    };

    internal static string? GetKeyName(int key) => key switch
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
