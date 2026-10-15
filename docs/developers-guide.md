# 開発と検証

このフォルダーは独立Gitリポジトリ。C#/.NET 10 Windows Forms、Windows SDK 10.0.19041.57を使用する。録画は外部FFmpegによるH.264エンコード。FFmpegバイナリをGitや配布へ含めていない。

```powershell
pwsh -File dev/scripts/build.ps1
pwsh -File dev/scripts/test.ps1
pwsh -File dev/scripts/test.ps1 -FfmpegPath C:\path\ffmpeg.exe -FfprobePath C:\path\ffprobe.exe
```

通常テストは入力・設定・ファイル操作の合成データ、非表示画面の配置と再生状態を扱う。FFmpeg引数を渡すと合成映像を実際にMP4/MKVへ保存し、デコードとフレーム数、時刻で内容の変わる映像のシーク、16デコーダーの同時取得と終了を検査する。実行結果は製品版情報に対応する`dev/history/vX.Y.Z/logs/tests-*/`に置く。`test.ps1 -Version v0.1.2`で対象版を明示できる。Unity Captureの合成受信テストは[仮想カメラの開発時の確認](virtual-camera.md#開発時の確認)を参照する。

```powershell
dotnet build tests/CapchunScreen.CaptureSmoke/CapchunScreen.CaptureSmoke.csproj
./tests/CapchunScreen.CaptureSmoke/bin/Debug/net10.0-windows10.0.19041.0/CapchunScreen.CaptureSmoke.exe dev/history/v0.1.2/logs/capture-local
```

キャプチャスモークは自作の検証窓だけを取得し、WGCの他窓分離・サイズ変更・停止条件を検査する。対話デスクトップへのアクセスが必要。制限環境のAccessDeniedを製品の動作不良と同一視しない。

同じhelperの`--scene`は閉じるまで動く録画試験用窓。実UI検証ではこれを対象に選ぶ。テスト用設定を分離して起動する場合は`start.ps1 -SettingsDirectory <対象版logs内の設定フォルダー> -FfmpegPath <絶対パス>`を使う。

実施済みのテスト件数、録画したMP4とTSVの照合、実操作動画の台帳と未検証範囲は[この版の実装報告](../dev/history/v0.1.2/v0.1.2-imp.md)に記録する。操作風景の録画と、CapchunWindow自身が生成した動画は別の証跡として扱う。

## 構成

- Capture: モニター/GDI、HWND選択、WGCとD3D11資源管理。
- Input: 録画時だけ有効なフック、入力蓄積、対象範囲判定。
- Recording: エンコーダ、単調時計、固定寸法・TSV対応、保存確定。
- Storage: 設定、録画一覧、改名・ごみ箱。
- Playback: FFmpegによる独立デコード、単調時計、シーク・終了処理。
- VirtualCamera: UnityCaptureの登録検出と共有メモリ送信、最新フレームの配信ワーカー。
- UI: 録画・設定・改名・独立プレビュー・複数再生画面と所有資源。

初版のアイコンは親の採用04共通ICOをこのリポジトリへ複製し、親相対パスを実行時依存にしない。独立ウィンドウはBrandedFormとWindowTitle.Createを使用する。
## デスクトップアクセスの事前確認

最終GUIテストの前に次を実行します。セッション切断・入力デスクトップの不一致・カーソル取得拒否がある場合は非ゼロで終了するため、GUIテストを開始しません。診断結果は対象版のlogsへ出力します。

```powershell
pwsh -File dev/scripts/check-desktop.ps1
```

Computer Useでアクセス拒否が発生したら入力を中止し、最終テスト録画を停止して診断を実行します。接続を復旧した後は事前確認をやり直し、ウィンドウ一覧とスクリーンショットを新しく取得します。古い要素番号・スクリーンショットID・座標を再利用しません。管理者起動やUAC無効化を代替策にしません。

製品本体は取得前の確認と500ms周期の状態監視を行い、ログの連続出力を抑制します。アクセス中断時には新規取得を止め、既存録画を中断保存します。エンコーダーやディスクの失敗は別エラーとして扱います。中断保存・復旧通知は自動テストで状態を模擬して検証し、実際のロックやUAC操作を自動化しません。
