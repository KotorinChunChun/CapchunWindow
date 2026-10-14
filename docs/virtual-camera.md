# 仮想カメラ

CapchunScreenは、導入済みの **Unity Capture / Unity Video Capture** へ映像を送信する。Python、pyvirtualcam、送信用の独自ネイティブDLLは使用しない。現在の対応先は最初のUnity Video Capture（デバイス番号0）。

## 利用条件と状態

- Unity CaptureのDirectShowフィルターが利用するPCに別途導入されていること。CapchunScreenはドライバーのダウンロード・登録・管理者昇格を行わない。
- 受信アプリが使用する32bit／64bitに対応するフィルターが必要。アプリは両方の登録と登録先DLLの存在を調べ、導入未確認を配信成功として表示しない。
- 受信アプリのカメラ選択で「Unity Video Capture」を選ぶ。受信アプリ未接続時は「接続待ち」となり、カメラに映像が届いたとは判定しない。
- 同じUnity Video Captureへ、別の送信アプリから同時送信しない。公開プロトコルには送信者を識別する排他機構がない。
- ドライバー導入後のTeams／Discord等の実カメラ入力は、合成受信テストとは別に確認する。

停止時は黒背景の「No Signal」を送る。受信側のmutex待機は100msで打ち切り、終了時送信にもキャンセル期限を設ける。受信側が停止・異常終了しても無期限には待たない。

## 移植した送信処理

元のCapchunScreen.pyはpyvirtualcam 0.15.0の`unitycapture`バックエンドを明示し、RGB映像を20fps指定で送信していた。C#版は同じ公開共有メモリ手順を直接実装する。送信頻度は呼出側のフレーム供給頻度に従う。

共有領域の先頭32バイトは、容量・幅・高さ・ピクセル単位stride・形式・リサイズ方式・鏡像方式・最終フレーム保持時間の32bit値。映像はその後ろへ、上下を反転したRGBA8として書く。デバイス0の名前は`UnityCapture_Mutx`、`UnityCapture_Want`、`UnityCapture_Sent`、`UnityCapture_Data`。mutex中でヘッダーと画素を一括更新し、送信イベントで受信側へ通知する。受信側がデータ領域を作るまで送信側は待機する。

参照した一次資料:

- [UnityCaptureの共有メモリ仕様・送受信処理](https://github.com/schellingb/UnityCapture/blob/master/Source/shared.inl)
- [UnityCaptureのDirectShowフィルター・登録CLSID](https://github.com/schellingb/UnityCapture/blob/master/Source/UnityCaptureFilter.cpp)
- [pyvirtualcamのUnityCaptureバックエンド](https://github.com/letmaik/pyvirtualcam/blob/main/pyvirtualcam/native_windows_unity_capture/virtual_output.h)
- [UnityCaptureの導入説明](https://github.com/schellingb/UnityCapture)

共有プロトコルはUnityCaptureのMITライセンスのソースを参照してC#へ移植した。[著作権表示とライセンス](../licenses/UnityCapture-LICENSE.txt)を保存する。ドライバーのバイナリはこのリポジトリに含めない。

## 開発時の確認

```powershell
dotnet run --project tests/CapchunScreen.VirtualCameraTests/CapchunScreen.VirtualCameraTests.csproj
```

テスト専用のランダムな名前付きオブジェクトを作り、受信側を模擬して上下・色順・ヘッダー・イベント・容量・キャンセル・待機期限を検査する。製品用のUnityCapture共有領域や既存録画は使用しない。実ドライバーの導入・受信を成功した証拠にはしない。
