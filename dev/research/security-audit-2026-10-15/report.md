# CapchunScreen セキュリティ診断・改善実装レポート

診断日: 2026-10-15。診断開始版: `a5fc6d9` / main / v0.1.0。開始時clean。画面録画、低レベル入力フック、操作TSV、FFmpeg起動、管理pipe、依存と追跡秘密を静的確認した。

## 指摘

|ID|重大度・成立条件|証拠|改善・実装状況|
|---|---|---|---|
|CAP-01|高（機密性、修正済み）。既定の入力記録中、録画対象でShift付き英字・数字・記号を入力すると、通常文字記録OFFでもTSVにキーが残っていた。秘匿欄でもキー名が漏れ、TSV共有時に露出し得る。|修正前`src/CapchunScreen/Input/InputBuffer.cs:149` の判定が全修飾キーを許可。`src/CapchunScreen/Storage/AppSettings.cs:12` は入力記録ON/通常文字OFFが既定。`src/CapchunScreen/Recording/RecordingSession.cs:77` でキーをTSVへ保存。|Shiftだけによる文字入力を通常文字として既定で抑止した。Ctrl/Alt/Winショートカットと明示的な通常文字記録は維持。合成回帰で修正前9失敗→修正後全154成功。|
|CAP-02|高（条件付き、設計判断）。通常文字記録を利用者が有効にするとpassword欄でも文字キーを保存し得る。Shiftだけの修正では解決しない。|`src/CapchunScreen/Input/InputMonitor.cs:80`、`Input/InputBuffer.cs:149`。foreground/window範囲のみで、password/secure-input判定がない。|UIA判定不能時の停止、除外アプリ、入力/映像双方の保護範囲と利用者向け仕様を決める必要がある。今回の重大判断除外により保留。|
|CAP-03|中。同一利用者が管理pipeへ大きな要求や極端に長いrequestIdを送り応答を読まない場合、メモリ増大や管理受付停止が可能。|`src/CapchunScreen/Integration/ManagementEndpoint.cs:45` のReadLineAsync上限なし、`:86` のWriteLineAsyncにdeadline未適用。|2026-10-25修正済み：要求を4096文字までに制限し、超えたら応答せずに切断する。応答の書込みにも接続時の2秒期限を適用した。上限の境界値を自動検査で確認した（[v0.1.3報告](../../history/v0.1.3/v0.1.3-imp.md)）。他製品と共有する契約の側は未変更。|

`CurrentUserOnly`と2秒の読取り期限（同ファイル:38、:41）、FFmpegの`ArgumentList`/`UseShellExecute=false`（`Recording/FfmpegEncoder.cs:25`）、対象窓/録画範囲の入力絞り込み（`Input/InputMonitor.cs:46`）を確認した。これらはpassword保護の代わりにはならない。

## 依存・秘密検査

製品csprojに外部PackageReferenceなし。FFmpegは外部指定であり今回の監査対象にバイナリ/codecの脆弱性検査は含めない。追跡テキスト59ファイルをPEM秘密鍵/GitHub/AWS/OpenAIの代表パターンで値非表示検査し一致0。無視対象、過去Git履歴、一般パスワードは未検査。

## 改善実装・検証

- [x] CAP-01の入口・保存経路・既存要求R4（通常文字既定無効）を確認。
- [x] [v0.1.0の実装記録](../../history/v0.1.0/v0.1.0-imp.md)へ局所修正計画と結果を追記。
- [x] 合成入力で修正前の失敗と修正後の成功を確認し、関連回帰を実行。
- [x] ビルド警告0/エラー0、全154検査成功を確認して修正を記録。

検証: `dotnet build tests/CapchunScreen.Tests/CapchunScreen.Tests.csproj -c Debug --artifacts-path artifacts/security-audit` と生成テストEXEを実行。左右Shift＋英字/数字/記号/テンキー、押下継続/解放、明示ON、Ctrl/Alt/Win＋Shift、Shift＋F1を合成入力で検査した。既存管理ガード・ストレージ・UIロジック・プレビュー/再生ロジックの回帰を含む。実FFmpegパラメーターを渡していないため、今回の154件に実codec検証は含めない。生ログはGit対象外の`dev/history/v0.1.0/logs/security-audit-20261015/`。

実ユーザーの入力・パスワード・設定・録画を使わない。GUI録画/別ユーザーpipe/実FFmpegの最新安全性の証明は未実施。外部公開・導入済みEXEの更新・親gitlink変更は行わない。
