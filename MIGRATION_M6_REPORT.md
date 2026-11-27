# M-Phase 6 CDP取得元統合

実施日: 2026-11-26

## 実装内容

- `CdpCaptureSource`を追加し、設定したloopback CDP portから完全一致するページURLを一意に選んで接続する。
- `/kcsapi/api_port/port`かつ明示されたAPI hostの成功レスポンスだけを対象にし、`responseReceived`後の`loadingFinished`で`getResponseBody`を要求する。
- CDP許可コマンドは`Network.enable`、`Network.getResponseBody`、`Network.disable`のみに限定。cookie、リクエスト本文、他API本文、ページJavaScriptにはアクセスしない。
- ブラウザ切断や対象タブ未起動時は3秒間隔で再接続を試み、停止時に`Network.disable`を送り終了する。debugging endpointは127.0.0.1とloopback WebSocketだけを受け入れる。
- 既存の74EN JSON監視とCDPを設定画面で切替可能にした。CDP設定は利用者が明示入力し、ブラウザ起動設定やレジストリを自動変更しない。
- 設定値は`cdp_enabled`、`cdp_debug_port`、`cdp_page_url`、`cdp_api_hosts`として保存する。C++互換キーも維持する。

## 変更ファイル

- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureOptions.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.Core/AppSettings.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCaptureOptionsTests.cs`
- `tools/CdpCaptureIntegrationProbe/`
- `SenkaSpeedMeasuringDevice.sln`

## ローカル検証

- .NET SDK 10.0.404 Releaseソリューションビルド: 成功、警告0・エラー0。
- xUnit: 20件成功、失敗0件、スキップ0件。
- Edge 158.0.4370.45の一時プロファイル、loopback限定CDP endpoint、合成HTTPページ/APIで製品の`CdpCaptureSource`を実行。88-byte本文のSHA-256がテストサーバー値と一致し、`CDP停止`まで確認した。
- これはローカル試験であり、実ゲーム通信の本文一致試験ではない。HTTPSではない。

## 設計上の制約・未検証

- 通常ブラウザでCDPを使うには、専用ブラウザプロファイルでloopback限定のremote debuggingを利用者が設定する必要がある。本アプリはその設定を追加・変更しない。
- 74ENの公開`main`ソースはWebView2の`AdditionalBrowserArguments`を使うが、現行配布版にremote debuggingが有効か、外部CDP接続できるかは未確認。74ENでは引き続きJSON監視を利用できる。
- HTTPS証明書検証を維持したローカル応答取得、OOPIF、複数同一URLタブ、実ゲーム、ブラウザ/74ENの強制終了・再接続は未検証。
- 設定画面のAPI host allowlistは完全一致。誤ったhostではデータを取らず、CDP接続待機表示になる。
- タブURLの完全一致を必須とし、同じURLのタブが複数なら接続しない。

## セキュリティ確認

- API本文取得は選択したタブ内の指定host/pathに限定。
- 診断serverとCDP endpointのテストは127.0.0.1に限定。外部インターフェースへ公開していない。
- プローブおよびアプリはゲームAPIを生成・再送せず、レスポンスも変更しない。
- CDP設定をブラウザへ注入したり、74ENのレジストリ値を自動編集したりしない。

## 次工程

M7で74EN JSONモード、CDPモードの手動切替、正常終了・強制終了・再起動、CSVとの連続動作、タスクトレイ、最前面表示を統合確認する。HTTPSと実74EN CDPは個別の検証条件が揃ってから実施する。
