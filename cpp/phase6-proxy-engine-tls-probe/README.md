# Phase 6 サイドカーHTTPS検証プローブ

Debugビルドの.NET 10 / Titanium.Web.Proxy 8.0.2サイドカーを起動し、loopbackの自己署名HTTPSテストサーバーへPOSTを一度だけ中継します。上流オリジンはSHA-256でピン留めし、localhostのホスト名と証明書有効期間も確認します。クライアント側はサイドカーが一時出力した公開CAだけをプロセス内で信頼します。Windows証明書ストアは変更しません。

```powershell
dotnet build ..\phase6-proxy-engine\Phase6ProxyEngine.csproj -c Debug
dotnet run --project Phase6ProxyEngineTlsProbe.csproj
```

Releaseビルドにテスト用ピン指定は含まれません。.NET 10 SDKが必要です。この試験はローカル専用で、実74EN/ゲーム/外部ネットワークを使用しません。

