# CapchunWindow

モニターや選択ウィンドウを録画し、操作履歴を同名のTSVへ保存するWindows用ツールです。Python版ChulipVideo内のCapchunScreenから、録画・独立プレビュー・仮想カメラ・複数動画再生をC#へ移植しました。

## 起動

[公開Releases](https://github.com/KotorinChunChun/CapchunWindow/releases)の`CapchunWindow-0.1.2-setup.exe`で利用者単位に導入できます。.NETランタイム同梱の未署名開発版です。設定・録画・TSVは更新・削除時も保持します。[導入・依存物・注意事項](docs/install.md)を確認してください。

Windows 11 x64、.NET 10 SDK、H.264（libx264）対応FFmpegが必要です。Pythonは不要です。

```powershell
cd C:\develop\KotorinToys\tools\CapchunScreen
pwsh -File dev/scripts/start.ps1
```

初回は「設定」で`ffmpeg.exe`と録画の保存先を指定します。FFmpegはEXEの隣・EXEから見た`.tools/ffmpeg/bin/`・PATHからも検出します。既存のFFmpegを使って起動する場合は、`start.ps1 -FfmpegPath <ffmpeg.exeの絶対パス>`でも指定できます。FFmpegの自動ダウンロードやドライバー導入は行いません。

## 主な機能

- モニター／ウィンドウの選択、検索、サムネイル選択、選択時の自動プレビュー。
- 他のウィンドウに隠れても対象だけを取得するWGC録画。
- MP4/MKV、15/30/60 FPS、画質、開始待ち時間、最長時間。
- Ctrl+Shift+F9で録画開始・停止。
- クリック・ホイール・ショートカットのTSV記録。操作ごとの記録設定、検索、一括変更、入力テスト、旧設定の取り込み。
- 録画一覧、内蔵プレイヤーでの再生、TSV表示、動画とTSVの連動改名・ごみ箱移動。保存完了の右から保存先を開けます。
- 20px単位で移動でき、ダブルクリックで全画面を切り替える独立プレビュー。導入済みUnity Captureへの仮想カメラ出力。
- 最大16本の動画の各現在位置からの同時再生、全体・個別の一時停止とシーク、YouTubeに近いキー操作。
- Unity Captureの公式配布先と登録・解除手順を表示する導入ガイド。

音声録音、動画編集、入力演出の再生時重畳は含みません。同時再生は元と同じ音声なしです。仮想カメラの利用には別途Unity Captureドライバーが必要です。旧Pythonの設定・録画は変更しません。KotorinToys 0.1.2以降の本体からの導入・更新・削除と起動管理に対応します。FFmpegやドライバーは同梱しません。機密入力を含む録画・操作記録には使用しないでください。

[使い方](docs/users-guide.md) / [開発・検証](docs/developers-guide.md) / [抽出対応表](docs/migration.md) / [v0.1.2実装報告](dev/history/v0.1.2/v0.1.2-imp.md)

## KotorinToys管理連携（2026-10-15追加）

本体の一覧からの開発版起動・既存画面表示・状態確認・正常停止に対応しました。上記移植時点の未登録範囲を、この追加で更新します。配布・正式インストール・外部公開は未実施です。[管理連携と検証用起動](docs/kotorintoys-integration.md)を参照してください。
