# Phase 6 ローカル併用・復旧プローブ

ローカルHTTPサーバーのみを使い、プロキシ待受ポート競合、停止時にプロキシが接続を失敗させること、同じポートで再起動後に復旧すること、子プロセス強制終了後にポートが解放されることを確認する試験ハーネスです。Titanium.Web.Proxy 3.2.0を使用します。

ゲーム通信、実74EN、外部サーバー、システムプロキシ設定、Windows証明書ストアは使用しません。停止中に失敗した呼び出しを自動再送しないことを確かめ、成功する各要求はテスト側から明示的に1回ずつ送ります。

## 実行

```powershell
dotnet run --project Phase6LocalCoexistenceProbe.csproj
```

A/B/CのHTTPS直列接続とTLS検証は `../phase5-prototype/README.md` のプローブで確認します。B/Cの74EN相当はTitaniumのローカル模擬プロキシであり、実74ENとの検証ではありません。

