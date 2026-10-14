# 開発と検証

このフォルダーは独立Gitリポジトリ。C#/.NET 10 Windows Forms、Windows SDK 10.0.19041.57を使用する。録画は外部FFmpegによるH.264エンコード。FFmpegバイナリをGitや配布へ含めていない。

```powershell
pwsh -File dev/scripts/build.ps1
pwsh -File dev/scripts/test.ps1
pwsh -File dev/scripts/test.ps1 -FfmpegPath C:\path\ffmpeg.exe -FfprobePath C:\path\ffprobe.exe
```

通常テストは入力・設定・ファイル操作の合成データを扱う。FFmpeg引数を渡すと合成映像を実際にMP4/MKVへ保存し、デコードとフレーム数を検査する。実行結果は`dev/history/v0.1.0/logs/tests-*/`に置く。

```powershell
dotnet build tests/CapchunScreen.CaptureSmoke/CapchunScreen.CaptureSmoke.csproj
./tests/CapchunScreen.CaptureSmoke/bin/Debug/net10.0-windows10.0.19041.0/CapchunScreen.CaptureSmoke.exe dev/history/v0.1.0/logs/capture-local
```

キャプチャスモークは自作の検証窓だけを取得し、WGCの他窓分離・サイズ変更・停止条件を検査する。対話デスクトップへのアクセスが必要。制限環境のAccessDeniedを製品の動作不良と同一視しない。

同じhelperの`--scene`は閉じるまで動く録画試験用窓。実UI検証ではこれを対象に選ぶ。テスト用設定を分離して起動する場合は`start.ps1 -SettingsDirectory <対象版logs内の設定フォルダー> -FfmpegPath <絶対パス>`を使う。

## 構成

- Capture: モニター/GDI、HWND選択、WGCとD3D11資源管理。
- Input: 録画時だけ有効なフック、入力蓄積、対象範囲判定。
- Recording: エンコーダ、単調時計、固定寸法・TSV対応、保存確定。
- Storage: 設定、録画一覧、改名・ごみ箱。
- Playback: FFmpegによる独立デコード、単調時計、シーク・終了処理。
- VirtualCamera: UnityCaptureの登録検出と共有メモリ送信、最新フレームの配信ワーカー。
- UI: 録画・設定・改名・独立プレビュー・複数再生画面と所有資源。

初版のアイコンは親の採用04共通ICOをこのリポジトリへ複製し、親相対パスを実行時依存にしない。独立ウィンドウはBrandedFormとWindowTitle.Createを使用する。
