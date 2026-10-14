# CapchunScreen

モニターや選択ウィンドウを録画し、操作履歴を同名のTSVへ保存するWindows用ツールです。Python版ChulipVideo内のCapchunScreenから、録画に必要な機能をC#へ移植しました。

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

仮想カメラ、独立プレビュー、複数同時再生は追加実装中です。音声録音、動画編集、入力演出の再生時重畳は含みません。旧Pythonの設定・録画は変更しません。本体からの起動登録・配布・外部公開は未実施です。

[使い方](docs/users-guide.md) / [開発・検証](docs/developers-guide.md) / [抽出対応表](docs/migration.md) / [v0.1.0実装報告](dev/history/v0.1.0/v0.1.0-imp.md)
