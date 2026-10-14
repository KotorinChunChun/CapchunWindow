# Python版からの抽出範囲

元: `C:\develop\ChulipVideo\20251210_動画クリップ作成_Python`。元のファイル・設定は変更しない。

|元の機能・ファイル|C#版の対応|
|---|---|
|CapchunScreen.pyの対象選択|CaptureCatalog、録画画面|
|window_utils.pyの画面取得|CaptureFactoryのGDI取得、DWM矩形追従|
|wgc_capture.pyの専用窓取得|正式なHWND interopを使うWindowFrameSource|
|recorder_core.pyの録画ループ|RecordingSession、FFmpegのH.264出力|
|recorder_core.pyの入力蓄積|InputMonitor、InputBuffer|
|同名TSV保存|UTF-8、timestamp / frame / x / y / click / keysの6列|
|録画ファイル一覧|録画一覧、既定アプリ再生、改名・削除時のTSV連動|
|ChulipVideo編集本体|抽出対象外|
|ChunapToolの窓位置・サイズ変更|抽出対象外|
|仮想カメラ・UnityCapture導入|基本機能に絞る今回の抽出対象外|
|独立プレビュー・複数同時再生・アプリ内編集連携|今回の抽出対象外|
|録画動画への音声|元のCapchunScreenにないため追加しない|

## 互換性と改善

- 時刻は動画内のフレーム番号/FPS、小数3桁。フレームは0始まり、入力なしは`None`。クリック名は`L-Click`等、キーは`Ctrl,C`のようにカンマ区切り。
- 取得と出力サイズが異なる場合は縦横比を保って黒い余白を加え、TSV座標も出力映像に合わせる。
- 操作は映像に焼き込まずTSVへ保存する。C#版の既定アプリ再生はTSVを重畳しない。
- Python版の画質UIは録画処理へ渡されていなかった。C#版はCRFでエンコーダへ渡す。
- 専用窓取得が失敗した際、画面全体の同位置へ自動切替しない。エラーを表示する。
- 旧アプリの設定ファイルやshortcuts.tsvは自動移行しない。C#版の入力設定は操作記録の有無と単独文字の有無に絞る。
- 録画途中のファイルは一時名で保存し、FFmpegとTSVの完了後に録画一覧へ公開する。

## APIの根拠

[CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)、[CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-26100)、[D3D11 interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/ns-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess)、[D3D11スレッド保護](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_4/nn-d3d11_4-id3d11multithread)を参照する。
