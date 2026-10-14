# CapchunScreen

モニターや選択ウィンドウを録画し、操作履歴を同名のTSVへ保存するWindows用ツールです。Python版ChulipVideo内のCapchunScreenから、録画・独立プレビュー・仮想カメラ・複数動画再生をC#へ移植しました。

## 起動

Windows 11 x64、.NET 10 SDK、H.264（libx264）対応FFmpegが必要です。Pythonは不要です。

```powershell
cd C:\develop\KotorinToys\tools\CapchunScreen
pwsh -File dev/scripts/start.ps1
```

初回は「設定」で`ffmpeg.exe`と録画の保存先を指定します。FFmpegはEXEの隣・EXEから見た`.tools/ffmpeg/bin/`・PATHからも検出します。既存のFFmpegを使って起動する場合は、`start.ps1 -FfmpegPath <ffmpeg.exeの絶対パス>`でも指定できます。FFmpegの自動ダウンロードやドライバー導入は行いません。

## 主な機能

- モニター／ウィンドウの選択、検索、プレビュー。
- 他のウィンドウに隠れても対象だけを取得するWGC録画。
- MP4/MKV、15/30/60 FPS、画質、開始待ち時間、最長時間。
- Ctrl+Shift+F9で録画開始・停止。
- クリック・ホイール・ショートカットのTSV記録。単独文字は既定で記録しません。
- 録画一覧、既定アプリでの再生、TSV表示、動画とTSVの連動改名・ごみ箱移動。
- 最前面の独立プレビューと、導入済みUnity Captureへの仮想カメラ出力。
- 最大16本の動画の同時再生、全体・個別の一時停止とシーク。

音声録音、動画編集、入力演出の再生時重畳は含みません。同時再生は元と同じ音声なしです。仮想カメラの利用には別途Unity Captureドライバーが必要です。旧Pythonの設定・録画は変更しません。本体からの起動登録・配布・外部公開は未実施です。

[使い方](docs/users-guide.md) / [開発・検証](docs/developers-guide.md) / [抽出対応表](docs/migration.md) / [v0.1.0実装報告](dev/history/v0.1.0/v0.1.0-imp.md)
