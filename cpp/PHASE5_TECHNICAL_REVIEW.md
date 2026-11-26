# Phase 5 通信取得方式の再調査・ローカル検証

調査日: 2026-11-26  
調査対象: 航海日誌拡張版 `develop`、ElectronicObserverEN `main`、指定されたローカル通信経路 A/B/C

## 結論と進捗

**通信エンジン候補を選定し、A/B/C を模擬したローカル HTTPS 試験は通過しました。Phase 5 全体は未完了です。**

将来の実装候補は、C++/Qt アプリから分離した Titanium.Web.Proxy 通信エンジンです。ローカル試験では、Titanium.Web.Proxy 3.2.0 のプロキシ2台で B/C の直列接続を再現し、対象 API 風リクエストのメソッド、Host、URI/クエリ、本文と、クライアントおよび解析側で受け取るレスポンス本文が一致しました。ローカル試験CAをプロセス内だけで明示的に信頼し、検証を無効化せずに実行しています。

ただし B/C は **74EN 本体ではなく、同じ Titanium.Web.Proxy バージョンを使うローカル模擬プロキシ**です。実74EN、実ブラウザ、実ゲームサーバー、実アカウント、サーバー証明書チェーン、プロセス終了時の実環境復旧、負荷は未検証です。したがって「74ENなしでゲームから実データ取得」「74ENとの実機併用」の完了判定はまだできません。ゲーム通信や設定には一切接続・変更していません。

## 参考ソースの確認結果

### 航海日誌拡張版

調査対象は [ProxyServer.java](https://github.com/Nishisonic/logbook/blob/develop/src/main/java/logbook/server/proxy/ProxyServer.java)、[JsonLoggingFilter.java](https://github.com/Nishisonic/logbook/blob/develop/src/main/java/logbook/server/proxy/JsonLoggingFilter.java)、[Filter.java](https://github.com/Nishisonic/logbook/blob/develop/src/main/java/logbook/server/proxy/Filter.java) です。

- `ProxyServer` は LittleProxy に MITM 機能を付け、BrowserMob Proxy の証明書管理を使用する。上流プロキシは `ChainedProxyManager` で設定し、リスン先は設定値に応じて loopback またはその他のアドレスになる。上流・下流の両方でTLSを終端する構造を取り得る。
- `JsonLoggingFilter` は要求・応答の `HttpContent` をコピーして蓄積し、応答完了時に対象データとして解析する。フィルターの応答処理は受け取った `httpObject` を返しているため、その経路は中身を書き換えない設計例として参考になる。
- 一方、この実装は要求本文もコピーする。`Filter.isNeed` は主にホストと `Content-Type: text/plain` で判定し、`api_port/port` に限定していない。さらに設定により解析データを TsunDB に送信する処理がある。本プロジェクトでは要求本文取得・保存と外部送信を採用しない。
- MITM用CAを読み込み、`trustAllServers` の設定をエンジンへ渡す構造も確認した。TLS検証無効化を許す設定やその既定値を安全性の根拠として流用しない。
- [v2.5.7.61-https](https://github.com/Nishisonic/logbook/releases/tag/v2.5.7.61-https) で2025年7月にHTTPS対応が追加され、その後[v2.5.7.63](https://github.com/Nishisonic/logbook/releases/tag/v2.5.7.63)以降に証明書の扱いが更新されている。2025年10月のリリース履歴には旧版で生成したCA関連ファイルを再生成する案内があり、[後続のPAC更新](https://github.com/Nishisonic/logbook/releases/tag/v2.5.7.81)ではHTTPS導入後に欠けたloopback条件の修正、PAC設定変更が記録されている。証明書/PAC運用は単純な設定追加では済まず、互換性と復旧手順が必要である。
- `build.gradle` ではJava 8、LittleProxy 1.1.2、BrowserMob Proxy 2.1.4、BouncyCastle 1.68等が固定されている。参考構造としては有用だが、これらの固定依存関係を新規アプリにそのまま流用しない。
- PAC生成は対象ドメインをloopbackプロキシへ向け、末尾に `DIRECT` を指定する形式である。PACファイルを生成することと利用者のブラウザへ適用することは別であり、プロキシ停止時に直接接続へフォールバックする可能性がある。今回の実装ではPACやシステム設定を自動変更しない。

### ElectronicObserverEN（74EN）

指定された [APIObserver.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/ElectronicObserver/Observer/APIObserver.cs)、[FormBrowserHost.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/ElectronicObserver/Window/FormBrowserHost.cs)、[ConfigurationConnectionViewModel.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/ElectronicObserver/Window/Settings/Connection/ConfigurationConnectionViewModel.cs)、[BrowserViewModel.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/Browser/BrowserViewModel.cs)、[WebView2ViewModel.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/Browser/WebView2Browser/WebView2ViewModel.cs) を確認しました。

- `APIObserver` は Titanium.Web.Proxy 3.2.0 を使用し、`ExplicitProxyEndPoint(IPAddress.Any, port, true)` でHTTPS復号を有効にしたエンドポイントを起動する。
- 上流プロキシが設定されると、`UpStreamHttpProxy` と `UpStreamHttpsProxy` の両方へ同じ `ExternalProxy` を設定する。要求方向で74ENプロキシの外側に本アプリのプロキシを置く B 構成に対応し得る実装である。
- APIObserver は要求時に `KeepBody = true` を設定して要求本文を読み、応答時に要求/応答を内部処理・任意保存する。現行実装の挙動を参考にしつつ、本アプリでは要求本文を読まず、成功した `/kcsapi/api_port/port` の応答だけを一方向に計測層へ渡す。
- `FormBrowserHost.BuildDownstreamProxy()` は、利用者指定の下流プロキシを優先し、次にシステムプロキシ使用を確認し、標準では `127.0.0.1:<74ENポート>` をHTTP/HTTPS双方に設定する。数値ポートをブラウザ側に渡す処理は [BrowserViewModel.SetProxy](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/Browser/BrowserViewModel.cs) にある。このため、本アプリをブラウザと74ENの間に置く C 構成は設定上の候補になる。
- 接続設定には `RegisterAsSystemProxy`、`UseUpstreamProxy`、`UseSystemProxy`、`DownstreamProxy` がある。システムプロキシ設定は利用者の明示操作なしに変更してはならない。
- [Browser/Browser.csproj](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/Browser/Browser.csproj) は Titanium.Web.Proxy 3.2.0 を固定している。これは今回の模擬試験にも同じ版を使えた理由だが、2026-11-26時点の上流は v8.0.2 まで更新されており、3.2.0を新規製品に採用することは勧めない。

## A/B/C と証明書の信頼関係

いずれもHTTPS本文を取得するには、どこかでTLSを終端する必要があります。TLS中間者を用いた場合、ブラウザが見るサーバー証明書はゲームサーバーの公開CA証明書そのものではなく、ローカルCAで署名された代理証明書になります。実装するなら、プロキシからゲームサーバーへのTLS証明書チェーン/ホスト名検証を厳格に維持し、ブラウザ側では利用者が明示的に信頼したプロキシCAのみを信頼する構成が必要です。検証無効化は不可です。

| 構成 | 公開ソースとローカル模擬結果 | 実機で必要な信頼 | 実74ENの判定 |
|---|---|---|---|
| A: ブラウザ → 本アプリ → サーバー | Titaniumの直接プロキシでローカルHTTPS通信を確認 | ブラウザが本アプリのCAを信頼。本アプリは実サーバー証明書を検証 | 未実施。ブラウザCA登録は明示同意が必要 |
| B: ブラウザ → 74EN → 本アプリ → サーバー | 74ENソースはHTTP/HTTPS上流の設定を持つ。ローカルでは同バージョンのTitanium二段直列を確認 | 74ENプロキシの上流TLSクライアントが本アプリのCAを信頼。ブラウザは従来どおり74EN側を信頼 | 未実施。74ENの実際の上流TLS検証、CAストア、上流認証/タイムアウト、画面設定を未確認 |
| C: ブラウザ → 本アプリ → 74EN → サーバー | 74ENブラウザの下流プロキシ設定とローカル二段直列を確認 | ブラウザが本アプリのCAを信頼。本アプリは74ENプロキシがTLS終端する場合そのCAも信頼。74ENは実サーバーを検証 | 未実施。実74ENの上流としてのHTTPS/CONNECT・証明書検証互換性を未確認 |

ローカル試験では各段が同じTitanium.Web.Proxy 3.2.0のため二段接続が成立しました。これは74ENの設定、プロセス内証明書ストア、HTTPS/CONNECT実装と同等である証明ではありません。A/B/C のうちどれかを製品設定に採用する前に、実74ENを使った利用者管理下の試験環境で追加確認が必要です。

## ローカル試験

試験ソースは [`phase5-prototype`](phase5-prototype/README.md) にあります。Titanium.Web.Proxy 3.2.0、ローカルTLSテストサーバー、ランダムなloopbackポートのみを使用し、A/B/Cを3回の独立したローカルAPI風リクエストで模擬しました。ゲームAPI/ゲームサーバーには接続していません。

試験専用の自己署名オリジン証明書はその証明書指紋だけをプロキシ側で許可し、クライアント側は各試験プロキシCAを.NETプロセス内のカスタムチェーンだけで信頼しました。Windowsのユーザー/マシン証明書ストア、ブラウザ設定、システムプロキシ設定は変更していません。証明書検証を全許可する設定も使っていません。

結果（A/B/C各1回）:

- オリジン到達数は3件で、試験クライアントが明示した3リクエスト各1件。意図しない追加リクエスト/再送は観測しなかった。
- HTTPメソッド `POST`、Host、`/kcsapi/api_port/port` とテスト用クエリ、API風本文がオリジン到達値と一致。
- オリジンが返したレスポンス本文、HTTPクライアントが受け取った本文、計測用イベントが受け取った本文が一致。
- `svdata=`付きJSONから提督経験値フィールドと空の艦一覧をローカル解析できた。
- ビルド成功、警告0件。3ケースすべてPASS。

これは HTTP/1.1、非圧縮の小さなレスポンス、試験用の証明書だけの確認です。HTTP/2/3、圧縮/チャンク/大容量応答、実ゲームの証明書チェーン、実74ENでの中継前後、長時間負荷、障害・終了復旧、実CSV記録は未検証です。また、TLS中間者ではTLSセッション自体は二つになり、TLSレコードや証明書が端末からサーバーまで同一にはなりません。試験で確認したのはアプリケーション層のHTTP本文・メソッド・Host・URI/クエリであり、全てのプロトコル条件下の非改変を証明するものではありません。

## 通信エンジン比較と判断

| 候補 | 状況・ライセンス等 | 評価 |
|---|---|---|
| C++ / Boost.Asio / Boost.Beast / OpenSSL | BeastはHTTP/1・WebSocket向けの低レベル構成要素で、TLSはOpenSSL等と組み合わせる。BoostはBSL-1.0、OpenSSL 3.xはApache-2.0 | Qtアプリ内で単一プロセスにできる利点がある。一方、明示プロキシ、CONNECT、MITM証明書、上流プロキシ、TLS検証、HTTP framing、圧縮、接続再利用、キャンセル/終了処理を自前で安全に組む必要があり、今回の最小スコープに対して保守・検証負担が大きい |
| Titanium.Web.Proxy (C#) | 74ENは3.2.0を参照。上流プロジェクトは活発に更新され、2026-11-12にv8.0.2をリリース。ライブラリはMIT。TLS/HTTP/1・2・3、イベント取得、上流プロキシを提供。現行 .NET 10 はLTSサポート中 | **現時点の第一候補**。74ENと同じ系統のAPIをローカルで試せ、上流設定をコードで表せる。C++ Qtとはプロセス分離が必要になり、.NET10ランタイムとバージョン更新管理が加わる。3.2.0から8.xへのAPI/挙動差を別途回帰試験する |
| LittleProxy + Netty (Java) | 航海日誌がLittleProxy 1.1.2とBrowserMob Proxy 2.1.4をバンドル。LittleProxyはApache-2.0。現行上流の状態と、旧バンドル依存の脆弱性評価は別に見る必要がある | 参考実装に近いがJavaランタイムと複数jarが必要。航海日誌のフィルターは変更可能フックであり、要求本文取得/外部送信を持つため、そのまま再利用しない |
| mitmproxy | MIT。2026-10-25に12.3.1。公式証明書手順では、クライアント端末がローカルCAを信頼する必要がある。公式Advisoriesに複数の版依存修正がある | 外部プロセスとして試作しやすいがPython配布物/依存が増え、C++ UIとの連携も必要。MITM観測用ツールとして有力でも常駐軽量性・製品組込・セキュリティ更新追従面で第一候補ではない |

ライセンス、リリース、アドバイザリ確認は参照日の公開情報に基づきます。特定のバージョンや依存ツリーについて完全なCVE監査を完了したとは扱いません。採用版を固定する直前に、依存パッケージ/ライセンス/セキュリティアドバイザリを再確認します。参考: [Titanium releases](https://github.com/justcoding121/Titanium-Web-Proxy/releases)、[Titanium library docs](https://github.com/justcoding121/Titanium-Web-Proxy/wiki/Home.md)、[LittleProxy](https://github.com/LittleProxy/LittleProxy)、[mitmproxy changelog](https://github.com/mitmproxy/mitmproxy/blob/main/CHANGELOG.md)、[mitmproxy advisories](https://github.com/mitmproxy/mitmproxy/security/advisories)、[Boost license](https://www.boost.org/LICENSE_1_0.txt)、[OpenSSL license](https://www.openssl.org/source/license.html)、[.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。

## 続行時の安全な実装境界

1. 捕捉プロキシはC++ UIと別プロセスにし、loopbackにのみbindする。システムプロキシ登録/復元は自動化せず、まず利用者がブラウザまたは74ENで手動設定する方式にする。
2. HTTPイベントではメソッド/宛先を選別し、`/kcsapi/api_port/port` の成功応答だけをコピーして計測側へ一方向通知する。要求本文、Cookie、Proxy-Authorizationは読み取らない。レスポンスやリクエストのsetter、再要求、キャンセル、遅延を行わない。
3. プロキシから宛先へのTLS検証はOS標準の証明書チェーンとホスト名検証を維持し、失敗時は接続失敗として扱う。検証を無効化するfallbackを用意しない。
4. MITMを行うブラウザ側CAが必要なら、用途と範囲を表示したうえで利用者が個別に明示同意した場合のみ、最小範囲へ登録する。既存証明書を消さず、本アプリが追加したものだけを追跡して解除できるようにする。利用者が拒否した場合、プロキシモードは起動せず74EN JSONモードへ戻す。
5. 通信エラー、証明書不一致、解析層停止、プロセス終了時は、リクエスト本文/レスポンスを編集・代替・再送せず、プロキシ停止またはトンネル失敗として安全に終了する。この挙動はテスト用通信で確認してから実機試験に進む。

## Phase 5 完了条件

| 条件 | 判定 | 補足 |
|---|---|---|
| 候補の比較と選定 | 条件付きで達成 | 追加検証の第一候補をTitanium.Web.Proxy分離プロセスとした。製品統合版は未実装 |
| ローカル試験でA/B/CのHTTPS中継・本文一致 | 達成 | Titanium 3.2.0同士の模擬プロキシでPASS |
| TLS検証を有効にしたローカル試験 | 達成 | オリジン指紋と試験CAのカスタムチェーンを使用 |
| 実ゲームの要求/応答を追加・変更しない | 実証未実施 | 実ゲーム通信に接続していない。プロダクション実装も未追加 |
| 74ENなしで実ゲームから経験値を取得 | 未達 | テストサーバーのみ。ゲームとの実通信試験は行わない |
| 実74ENを上流/下流にして動作 | 未達 | ソース調査とTitanium模擬プロキシのみ |
| エラー/終了時復旧、既存CSV互換、負荷 | 未達 | アプリ統合後の試験項目 |

## 変更ファイルと影響範囲

- `DEVELOPMENT_SPEC.md`: 今回の追加開発仕様を第21節に追加。
- `PHASE5_TECHNICAL_REVIEW.md`: 参考ソース調査、経路評価、エンジン比較と試験結果を更新。
- `phase5-prototype/`: ローカル限定のTitanium試験コードと実行説明を追加。
- `main.cpp`およびQt実行ファイル: 変更なし。今回の試験プロキシは製品コードに組み込んでいない。

この段階ではGitHubへのアップロード、ゲーム/74ENへの接続、証明書ストア登録、プロキシ設定変更を行っていない。

