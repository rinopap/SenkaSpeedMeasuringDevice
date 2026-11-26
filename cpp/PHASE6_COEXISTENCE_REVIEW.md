# Phase 6 併用検証の進捗

> **履歴資料:** 2026-11-26の仕様変更により、独自プロキシは主要方式として採用しません。この文書の結果はローカル模擬中継試験の記録です。実74EN互換性やCDP方式の成立を示すものではなく、実74ENにプロキシを設定する手順には進まないでください。

検証日: 2026-11-26

## 結果

**ローカル模擬環境の中継・ポート競合・停止・再起動試験を通過しました。Phase 6全体は未完了です。**

`phase6-prototype/` の試験ハーネスをビルドし、loopback上のHTTPテストサーバーとTitanium.Web.Proxy 3.2.0で確認しました。

```text
port_conflict_detected=True
first_request=True; stopped_proxy_fails_closed=True; restart_same_port=True
forced_process_exit_releases_port=True
origin_request_count=2 (expected 2; one per explicit successful local request)
response_observation_count=2 (expected 2)
PASS=True
```

- 先に確保したポートをプロキシが取得できず、競合として起動失敗する。
- プロキシ停止中のクライアント要求は接続失敗し、オリジンへ届かない。
- 同じポートで再起動すると通信が復旧する。
- 子プロセスで待受中のプロキシを強制終了した後、同じポートを別ソケットが再取得できる。
- 成功した2要求は試験側がそれぞれ明示的に1回送信し、オリジンは2件だけ受信した。プロキシによる再送は観測されなかった。
- レスポンス観測イベントは成功した2件だけを受け取った。

TLS中継およびA/B/Cの直列経路は、Phase 5のローカルHTTPSプローブで別途PASS済みです。今回のポート・復旧試験自体はHTTPです。

## サイドカー中継エンジンのローカル検証

実74ENとの手動試験へ進む前段として、独立プロセスの検証用サイドカーとHTTP/HTTPSプローブを追加しました。サイドカーはloopback限定で待受け、`Start(false)`で起動し、OSの証明書ストアやシステムプロキシ設定には触れません。CAの出力は明示したファイルパスへの公開証明書だけです。HTTPS上流の証明書検証は既定で維持し、ローカルテスト時だけDebugビルドでSHA-256ピンとホスト名・有効期間を照合します。Releaseビルドにテスト用ピン例外は含まれません。

HTTP/HTTPS双方で、メソッド・宛先/クエリ・Host・要求本文、クライアントへ返る応答本文の一致を確認しました。オリジンは明示した一要求のみを受け取り、解析出力は経験値数値のみで、要求本文・応答本文は標準出力にもファイルにも記録しません。試験時はTLS 1.2、期限・ホスト名の検証に加え、ローカル上流証明書を完全一致でピン留めしています。

Titanium.Web.Proxy 8.0.2への更新後、HTTP/2を有効にした初回HTTPSプローブは応答待ちでタイムアウトしました。両側のTLSハンドシェイクは成功しましたが、HTTP要求はローカルオリジンへ届きませんでした。そこで製品経路へ進まず、HTTP/2を無効にしたHTTP/1.1限定設定に切り替えて再試験し、現在はPASSしています。HTTP/2経路の原因と互換性は未解決です。

サイドカー自身を通すローカルプローブの出力:

```text
request_method_target_body_unchanged=True
response_body_unchanged=True
metrics_only_observation=True; raw_payload_not_logged=True
origin_request_count=1 (expected 1)
PASS=True

tls_upstream_pin_and_proxy_ca_validation=PASS
request_method_target_body_unchanged=True
response_body_unchanged=True
metrics_only_observation=True; raw_payload_not_logged=True
origin_request_count=1 (expected 1)
PASS=True
```

サイドカーは.NET 10.0.404 / Titanium.Web.Proxy 8.0.2の自己完結型win-x64実行ファイルとして発行しました。Release/Debugビルドは警告・エラーなしです。Qtソースには通信プロキシ画面、明示同意、CA公開証明書の出力、上流ポート指定、メトリクスだけを受け取る一方向IPCを追加しました。プロキシ開始時にJSON監視を停止し、終了時に再開します。CA秘密鍵はユーザー別アプリデータに保持し、再起動後の安定化を図っています。Qt Releaseもビルドし、新しい実行ファイルに更新しました。

ただし、実行ファイルを実際に起動してUIからサイドカーを開始・停止する操作確認はまだです。実74ENやゲームを経由した確認もしていません。HTTPS上流CAを手動で信頼させる操作は本アプリから自動実行しません。HTTP/2は停止問題を受けて無効にしています。

利用者の手元ではCSVファイルをexeと同じフォルダに置き、アプリを完全終了して起動し直すことで読み込みを確認済みです。これはローカルファイル互換性の確認で、74EN通信経路の併用試験とは別です。

## 未検証

| 項目 | 状態 | 理由 |
|---|---|---|
| 実74ENを上流・下流にした接続 | 未実施 | 試験はローカル模擬プロキシで、実74ENの設定・証明書・CONNECT処理は使っていない |
| 74EN内蔵ブラウザとウィンドウ取り込みの同時利用 | 未実施 | 74EN実機との組合せを確認していない |
| 実運用環境のHTTPS証明書互換性 | 未実施 | テスト専用証明書とプロセス内の限定信頼のみ |
| 実ゲーム通信の非改変性 | 未実証 | ゲームサーバーへ接続していない。ローカル試験でHTTP/1.1のメソッド、宛先、本文、応答本文のみ一致を確認 |
| Qt UIとサイドカーの開始・停止・一方向IPC | ソース実装済み、実行未確認 | Qt Releaseをビルドしたが、UI操作からの起動・終了およびCSV反映は未確認 |
| 長時間動作・CPU/メモリ負荷 | 未実施 | 統合後の確認項目 |
| 現行サポートランタイムと保守中の通信エンジン | ローカル確認済み | .NET 10.0.404 / Titanium.Web.Proxy 8.0.2でビルド。実74ENとの互換性は未確認 |
| HTTP/2を通す経路 | 未達 | Titanium 8.0.2のHTTPSプローブが停止し、現在はHTTP/2を無効にしたHTTP/1.1経路のみPASS |

ここから必要な手動確認は、まず新しいQt版の通信プロキシ画面からローカルサイドカーが開始・停止でき、HTTP/HTTPS試験の経験値数値がCSVへ一度だけ記録されることです。その後に、実74ENとの併用・証明書互換性・ウィンドウ取り込み確認を別途行います。実74EN試験は自己署名CAの手動信頼登録が必要になり得ます。登録前にCAの拇印を画面上で照合する手順と削除手順を用意する必要があり、現時点では実74ENをこのプロキシへ接続しないでください。現在の残件と実行ファイルの配布状態は下記を参照してください。

この試験はシステムプロキシ設定、Windows証明書ストア、外部接続、ゲーム通信を使用していません。実74ENでの併用可否や実ゲームへの影響を証明したものではありません。

## 再現

Windows PowerShellまたはDeveloper PowerShellで、.NET 10 SDKを使って次を実行します。

```powershell
dotnet run --project Phase6LocalCoexistenceProbe.csproj
```

詳細は[phase6-prototype/README.md](phase6-prototype/README.md)を参照してください。

