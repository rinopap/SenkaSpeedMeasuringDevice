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

第2引数にページからAPIリクエストを送るまでの秒数を指定できます（1～300秒、省略時10秒）。強制終了試験では、CDP取得元を接続後に止めてもブラウザが後続のローカルリクエストを継続できることを確認できます。

## 製品CDP取得元の接続プローブ

`CdpCaptureIntegrationProbe`は製品の`CdpCaptureSource`を使い、固定の艦これページと選択サーバーの対象APIを観測できるか確認します。任意URL/APIホストを受け付けないため、M10までのloopback合成サーバー試験をこのプローブで再現するものではありません。実ゲームで実行する場合は既に利用者が起動したブラウザに接続するだけで、計測機からゲームAPIを送信しません。

```powershell
dotnet build SenkaSpeedMeasuringDevice.sln -c Release
dotnet run --project tools/CdpCaptureIntegrationProbe/CdpCaptureIntegrationProbe.csproj -- `
  9222 20
```

第2引数はサーバーID（1～20）です。この補助プローブは正式配布物に含めません。


