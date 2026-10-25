namespace CapchunScreen.UI;

/// <summary>
/// 画面に表示している行だけをUI Automation・MSAAへ公開するDataGridView。
/// </summary>
/// <remarks>
/// 標準の実装は仮想モードでも全行をセルごとに子要素として公開する。操作記録の設定画面（約1,900行）では、
/// 支援ツールや自動操作が画面を探索すると、名前による探索だけで約60秒UIスレッドが応答しなくなった。
/// 子要素を「見出し行・表示中の行・スクロールバー」に詰め、行の前後移動も同じ番号でたどる。
/// フォーカスのある行は常に表示中なので、キーボード操作と読み上げは従来どおり辿れる。
/// 行を非表示にしない一覧（行番号と表示順が一致する一覧）だけで使う。
/// </remarks>
internal sealed class DisplayedRowsDataGridView : DataGridView
{
    internal DisplayedRowsDataGridView() => RowTemplate = new DisplayedRow();

    protected override AccessibleObject CreateAccessibilityInstance() => new DisplayedRowsAccessibleObject(this);

    /// <summary>表示中の行の範囲（先頭の行番号と行数）。行がなければ行数0。</summary>
    internal (int First, int Count) DisplayedRows
    {
        get
        {
            int first = FirstDisplayedScrollingRowIndex;
            return first < 0 || RowCount == 0 ? (0, 0) : (first, Math.Min(DisplayedRowCount(includePartialRow: true), RowCount - first));
        }
    }

    private int HeaderCount => ColumnHeadersVisible ? 1 : 0;

    /// <summary>公開している子要素のうち、見出し行と表示中の行が占める番号の終端（この番号は含まない）。</summary>
    internal int RowChildrenEnd => HeaderCount + DisplayedRows.Count;

    /// <summary>行番号から、公開している子要素の番号を返す。表示外の行は-1。</summary>
    internal int ChildIndexOfRow(int rowIndex)
    {
        var (first, count) = DisplayedRows;
        return rowIndex >= first && rowIndex < first + count ? HeaderCount + rowIndex - first : -1;
    }

    private sealed class DisplayedRowsAccessibleObject(DisplayedRowsDataGridView owner) : DataGridViewAccessibleObject(owner)
    {
        public override int GetChildCount()
        {
            int all = base.GetChildCount();
            return all <= 0 ? all : all - owner.RowCount + owner.DisplayedRows.Count;
        }

        public override AccessibleObject? GetChild(int index)
        {
            if (index < 0) return null;
            int header = owner.HeaderCount;
            var (first, count) = owner.DisplayedRows;
            if (index < header) return base.GetChild(index);
            if (index < header + count) return owner.Rows[first + index - header].AccessibilityObject;
            // 行より後ろ（スクロールバー）は、標準の番号へ戻して取得する。
            return base.GetChild(index - count + owner.RowCount);
        }
    }

    /// <summary>行の前後移動を、詰めた子要素の番号でたどる行。共有行の複製でも同じ型になるよう公開コンストラクタを持つ。</summary>
    internal sealed class DisplayedRow : DataGridViewRow
    {
        protected override AccessibleObject CreateAccessibilityInstance() => new DisplayedRowAccessibleObject(this);

        private sealed class DisplayedRowAccessibleObject(DataGridViewRow row) : DataGridViewRowAccessibleObject(row)
        {
            public override AccessibleObject? Navigate(AccessibleNavigation navigationDirection)
            {
                if (navigationDirection is not (AccessibleNavigation.Next or AccessibleNavigation.Previous or AccessibleNavigation.Down or AccessibleNavigation.Up)
                    || Owner?.DataGridView is not DisplayedRowsDataGridView grid)
                    return base.Navigate(navigationDirection);
                int index = grid.ChildIndexOfRow(Owner.Index);
                if (index < 0) return null;
                int target = index + (navigationDirection is AccessibleNavigation.Next or AccessibleNavigation.Down ? 1 : -1);
                // 標準と同じく、最後の行の次は返さない。スクロールバーへ進むとUI Automationが一覧の先頭へ戻って循環する。
                return target >= 0 && target < grid.RowChildrenEnd ? grid.AccessibilityObject.GetChild(target) : null;
            }
        }
    }
}
