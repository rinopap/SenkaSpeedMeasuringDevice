# Phase 6 サイドカー中継プローブ

Phase 6検証用サイドカー（.NET 10 / Titanium.Web.Proxy 8.0.2）を子プロセスとして起動し、loopbackのHTTPテストサーバーへPOSTを一回だけ中継します。メソッド、パス/クエリ、Host、要求本文、応答本文が保持され、サイドカーの標準出力には経験値メトリクスだけが出ることを確認します。

このテストはローカルHTTPに限ります。HTTPS証明書、実74EN、ゲーム、外部ネットワークは使用しません。TLS/A/B/CはPhase 5プローブで別に試験します。

```powershell
dotnet run --project Phase6ProxyEngineProbe.csproj
```

先に隣の `phase6-proxy-engine` をRelease構成でビルドしてください。.NET 10 SDKが必要です。

