# 開発・検証用ツール

ここにあるツールは合成データを使う開発用です。製品のゲーム通信取得には使いません。

## CDPレスポンス観測プローブ

`CdpFeasibilityProbe`は指定されたloopback CDPポートの`/json/list`からページURLが完全一致するタブを1つ選び、そのタブのCDP WebSocketへ接続します。API host/pathが完全一致した成功レスポンスだけを対象にし、本文をメモリで読み取った後はサイズとSHA-256のみを表示します。本文や認証情報は保存しません。

プローブが送るコマンドは`Network.enable`、`Network.getResponseBody`、`Network.disable`だけです。プローブ自身はゲームAPIを送信しません。

```powershell
dotnet build tools/CdpFeasibilityProbe/CdpFeasibilityProbe.csproj -c Release
dotnet run --project tools/CdpFeasibilityProbe/CdpFeasibilityProbe.csproj -- `
  --port 9222 --page-url 'http://127.0.0.1:50880/' `
  --api-host 127.0.0.1 --api-path /kcsapi/api_port/port
```

CDPを使うにはブラウザ起動時の明示設定が必要です。通常利用中のブラウザプロファイルを流用せず、専用の一時プロファイルを使い、debugging portを`127.0.0.1`へ限定してください。74ENのレジストリや設定はこのツールから変更しません。

## 合成HTTP試験サーバー

`LocalCdpTestServer`はloopbackにだけHTTPで応答し、テスト用HTMLと合成API JSONを返します。HTTPSサーバーや証明書を生成・登録しません。

```powershell
dotnet run --project tools/LocalCdpTestServer/LocalCdpTestServer.csproj -- 50880
```

## 製品CDP取得元のloopback結合プローブ

`CdpCaptureIntegrationProbe`は製品の`CdpCaptureSource`を使い、loopback Edgeで合成レスポンス本文のハッシュ一致を確認します。Edgeには専用の一時プロファイルと`--remote-debugging-address=127.0.0.1`を使い、実ブラウザの既存プロファイルや74ENの設定は変更しません。

```powershell
dotnet build SenkaSpeedMeasuringDevice.sln -c Release
dotnet run --project tools/CdpCaptureIntegrationProbe/CdpCaptureIntegrationProbe.csproj -- `
  9222 'http://127.0.0.1:50880/' 127.0.0.1
```



