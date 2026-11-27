# M-Phase 11 実施報告：艦これ専用化

実施日: 2026-11-27  
基準仕様: [`docs/NEXT_DEVELOPMENT_SPEC.md`](docs/NEXT_DEVELOPMENT_SPEC.md)  
M10基準コミット: `86741e812c881887aa6f49217b3f58854c6051f3`（GitHub `develop-csharp`）

## 1. 実装内容

- 添付された次期開発統合仕様書を全60節、2,430行のMarkdownとして`docs/NEXT_DEVELOPMENT_SPEC.md`へ保存した。
- 艦これの固定ページを`https://play.games.dmm.com/game/kancolle`、対象APIパスを`/kcsapi/api_port/port`に固定した。
- 20サーバーのID・名称・APIホストを`GameServerRegistry`へ固定定義した。
- 設定画面の任意ページURL・任意APIホスト欄を削除し、所属サーバーの選択欄へ置き換えた。CDPポート欄は現行接続方式のため残した。
- CDPはHTTPS、既定443番ポート、選択サーバーの完全一致ホスト、対象APIパスの全条件を満たす成功レスポンス本文だけを取得する。
- CDPのページ選択も固定ゲームページに一致するタブだけを候補とする。HTTP、他ホスト、別パス、URL userinfo、query/fragment等を含むページは拒否する。
- 旧`cdp_page_url`・`cdp_api_hosts`設定値は読み取り対象として使わず、新しい設定を保存した際に設定ファイルから取り除く。旧設定でCDPが有効でも所属サーバーが未選択なら監視を開始しない。
- `HttpClient`の環境プロキシ利用と自動リダイレクトを無効化し、CDP探索要求がloopbackから外へ転送されないようにした。WebSocket接続も直接接続だけに限定した。
- 接続プローブの引数と利用者向け資料を新しい固定仕様に合わせて更新した。

## 2. 作業環境・Git状態

- 現在のローカル作業フォルダには`.git`がなく、ローカルGitブランチ名および作業ツリー差分は確認できない。`git status`と`git branch`は「not a git repository」となった。
- GitHub上の`develop-csharp`先頭はM10 UIコミット`86741e812c881887aa6f49217b3f58854c6051f3`。ローカルM10ソースのうちCDP取得元、設定モデル、設定画面コードが同ブランチと内容一致することを確認した。このコミットをM10の復元基準として残す。
- GitHubの既定ブランチは`main`。本作業は`main`を変更していない。正式リリースやGitHub Releaseも作成していない。
- ChatGPTプロジェクトミラーの`sources/`にはファイルがなかったため、実際のソース調査は許可を得た作業コピーとGitHubのM10ブランチを用いた。

## 3. 現行構成と今回の調査結果

- `App`: WPF画面、設定、トレイ、Windows連携。
- `Core`: API本文パーサー、経験値スナップショット、計算、CSV、設定。
- `Capture`: `ICaptureSource`、74EN JSONファイル監視、CDP監視。
- `tests`: xUnit。
- CDPコマンドは`Network.enable`、`Network.getResponseBody`、`Network.disable`、`Target.setAutoAttach`に限定され、Fetch、Runtime、Cookie取得は実装されていない。
- CDPは`127.0.0.1`の`/json/list`からページを探し、返されたWebSocket URLもloopbackと指定ポートを検証する。本文は対象成功レスポンスの`Network.loadingFinished`後に取得する。
- CDPの現在のURL判定はM10ではHTTP/HTTPSと自由入力APIホストを許容していた。今回、ページ・サーバー・HTTPS条件を固定した。
- M10の`Target.setAutoAttach`によるiframe/OOPIF追跡、既存の解析・計算・CSV、74EN JSON監視は再利用した。

## 4. 変更ファイル

- `docs/NEXT_DEVELOPMENT_SPEC.md` — 添付された統合仕様書全文。
- `MIGRATION_M11_REPORT.md` — 本報告書。
- `src/SenkaSpeedMeasuringDevice.Core/GameServer.cs`
- `src/SenkaSpeedMeasuringDevice.Core/GameServerRegistry.cs`
- `src/SenkaSpeedMeasuringDevice.Core/KancolleCapturePolicy.cs`
- `src/SenkaSpeedMeasuringDevice.Core/AppSettings.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureOptions.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/AppSettingsTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCaptureOptionsTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/KancolleCapturePolicyTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/GameServerRegistryTests.cs`
- `tools/CdpCaptureIntegrationProbe/Program.cs`
- `tools/README.md`
- `README.md`
- `README_USER.md`

## 5. 再利用した既存実装

- `ICaptureSource`およびCDP/74EN取得元の選択構造。
- M9で追加した選択ページ配下のiframe/OOPIF監視。
- 受信済みレスポンス本文のメモリ内解析、経験値抽出、既存計算、CSV追記。
- M10のタブ行UI、監視状況ツールチップ、外観設定、トレイ、自動起動設定。
- 設定ファイルの既存外観・CDP有効化・ポート設定。

全面再実装やユーザーデータの削除は行っていない。既存CSVおよび配布ZIPも変更していない。

## 6. 設計判断・仕様との差分

### 採用した判定

- 固定ゲームページはHTTPS、host `play.games.dmm.com`、port 443、path `/game/kancolle`の完全一致とした。
- API本文の取得条件はHTTPS、port 443、選択したサーバーホスト、path `/kcsapi/api_port/port`の完全一致とした。
- 旧設定ファイルに任意URL/ホストが残っていても使用しない。所属サーバーが選ばれていないCDPモードは停止する。
- `json/list`のHTTP探索はloopbackへ直接接続し、環境プロキシを使わず、リダイレクトも追跡しない。

### 実装上の保留・仕様の不足

1. **正規ゲームページの遷移形**: 仕様書はquery、末尾スラッシュ、ログイン後の遷移形を実際の通常プレイで確認するよう求めている。開発側では実ゲームを開かずに厳格な完全一致を実装したため、末尾スラッシュ、query、fragmentを含むURLは現状拒否する。利用者の通常プレイで実際のCDP page URLを確認するまで、許可形を広げない。
2. **サーバー情報の権威性**: 20件の名称・ホストは仕様書の一覧と、公開されている[poiのserver.json](https://github.com/poooi/poi/blob/master/assets/data/server.json)を照合した。公開ツールのマスタであり、運営の公式な保証情報ではない。20サーバーへ独自接続・実通信確認は行っていない。
3. **74EN JSONのサーバー識別**: 仕様書はJSON監視ではホストを検証できない場合があるため所属サーバー選択を必須としない。現行設定はCDPサーバーIDのみを追加し、74EN JSON処理は従来どおり維持した。
4. **CDP接続先認証**: loopback、ポート一致、WebSocket URL検証、プロキシ無効化を実装したが、ポート所有プロセスの製品・署名確認や第三者接続防止は行っていない。CDPの残存リスク評価とFail-closedの拡充はM12/M13の範囲とする。
5. **外部通信の試験**: M10のCDP合成試験はHTTPだった。M11の固定HTTPS判定を含むローカルHTTPS fixture試験は未実施。CDP統合プローブを任意ローカルURL対応から固定ポリシー対応へ変更したため、M10のHTTP fixtureは現行製品取得元で使えない。HTTPS試験はM16で、正式版の許可制限を迂回しない試験設計にして実施する。
6. **CDP responseバッファ等**: M10で既存の8 MiB設定とpending上限256を使う。大容量イベント、イベントキュー上限、取得失敗統計、Generation IDは未実装でありM12/M15へ送る。

仕様書の必須条件を独自に緩和していない。実ブラウザの正規URL形式など証拠が不足する箇所は、許可範囲を拡大せず未確認として残した。

## 7. テスト結果

### Releaseビルド

- SDK: .NET 10.0.404。
- `dotnet build SenkaSpeedMeasuringDevice.sln -c Release -p:UseSharedCompilation=false`: 成功。
- 警告0、エラー0。

既定の制限実行ではWindows SDKレジストリ探索が拒否されたため、既存.NET 10 SDKを指定し、Windows SDK検出を許可された実行条件でビルドした。

### 自動テスト

- M10基準: 20件合格。
- M11後: 44件合格、失敗0、スキップ0。
- 追加・拡張した確認: 20サーバー一覧の一意性とID範囲、固定URLと類似URL/HTTP/userinfo/port/path/query/fragment拒否、HTTPSと選択サーバーホストおよびAPIパスの完全一致、未知のserver ID拒否、旧任意URL/ホスト設定を無視して再保存時に除去すること。

### 合成通信試験

- 今回、M11変更後の`CdpCaptureSource`を使ったHTTP/HTTPS合成ブラウザ通信試験は実施していない。
- ゲームAPI宛の独自通信は一切行っていない。
- M10以前のEdge/HTTP合成試験の結果は履歴上の基準値であり、M11の固定HTTPS経路の成功証拠として流用しない。

### 実機試験

- この作業中に実ゲーム、利用者の通常ブラウザ、74EN/WebView2へ接続していない。
- 設定画面の実表示、所属サーバー選択から実ブラウザの再接続までの手動確認は未実施。
- M10までに利用者から報告された通常ブラウザCDP、74EN JSON、74EN WebView2 CDP、CSVコピー互換、計測機終了後のゲーム継続結果はM10の報告として保持する。M11変更後の確認結果とは扱わない。

## 8. セキュリティ評価

- 任意ページURL/APIホストを製品設定から指定する経路を削除した。
- 対象API本文をHTTPS、選択サーバーホスト、完全一致pathで絞った。
- Fetch、Runtime、Cookie取得、ゲームAPI送信、JavaScript注入を追加していない。
- CDP探索で自動リダイレクトと環境プロキシ利用を無効にした。CDP WebSocketも直接接続へ限定した。
- 対象外ホストやHTTPレスポンス本文は`Network.getResponseBody`の対象にしない。
- 同一PC上の他プロセスからCDPポートを利用されるリスクは残る。loopback限定はローカル他プロセスに対する認証ではない。
- ブラウザのデバッグ状態を隠す処理、ブラウザセキュリティ機能の無効化、システム設定変更は追加していない。

## 9. 残存リスク・未解決事項

- 固定ページURLが通常プレイで実際に表示される正規URL形式と完全一致するか未確認。
- APIホスト一覧を通常ブラウザの受信済みメタデータと照合する実機確認は未実施。独自API通信による確認は禁止のままとする。
- CDP endpoint偽装、ポート所有プロセス、バッファ寿命、非同期ページ遷移競合はM12/M15で検証する。
- M11の更新版Self-contained配布ZIPは作成していない。既存`preview3` ZIPはM10版であり、更新コードを含まない。
- 実機UI、CSV回帰、DPI、ウィンドウ取り込み、自動起動実動作、長時間試験は未実施。

## 10. M11受入状況

| 条件 | 状況 |
|---|---|
| 正規ゲームページ・APIパスの固定 | 実装済み。通常プレイURLの完全一致は利用者確認待ち |
| 20サーバー内部マスタ | 実装済み。公開マスタとの照合、独自サーバー通信なし |
| サーバー選択UI | 実装済み。WPFビルド成功、画面操作は未実機確認 |
| 任意URL/APIホストを設定できない | 実装・設定移行テスト済み |
| 選択サーバーAPIだけ本文取得 | ポリシー単体テスト済み、実通信試験は未実施 |
| 既存計算/CSV/74ENを維持 | コード経路を再利用し、全44自動テスト合格。M11後実機回帰は未実施 |

M11の実装条件と自動テスト条件は満たした。通常プレイ時のURL形状、設定UI、M11後の実ゲーム/74ENは未検証のため、M11の実機確認まで含めた完了とは扱わない。

## 11. M-Phase 11～18の実装計画と依存関係

| Phase | 主な作業 | 依存関係 | 受入条件 |
|---|---|---|---|
| M11 艦これ専用化 | 固定URL/API、20サーバー台帳、サーバー選択、旧設定移行 | M10 | 任意URL/ホストが使えず、選択ホストだけが対象。既存計算・CSV・74EN回帰確認。通常プレイURLの許容形は実機観測後に確定。 |
| M12 CDPセキュリティ強化 | コマンド/引数許可表、endpoint検証、遷移・セッション、Fetch不存在、Fail-closed、ログ/認証情報点検 | M11 | 対象外本文なし、旧世代イベント記録なし、拒否条件は計測だけを止め、ゲーム通信に介入しないことを自動試験で示す。 |
| M13 接続方式技術検証 | Chrome承認型、Chromium差、Vivaldi、専用profile、動的port、待受、WebView2 | M12の接続先要件を使う | ブラウザ別の利用可否・利用者手順・待受範囲・残存リスク・確認バージョンを記録。未確認ブラウザは対応扱いしない。 |
| M14 マルチブラウザ/UI | 検出・能力管理、Chrome/Edge/Vivaldi/Brave/Opera、UI選択、起動補助、取得方式排他 | M13 | インストール済み検証済みブラウザだけを候補表示。ブラウザ+サーバー選択から利用者承認までの設定手順が完了し、他方式と二重起動しない。 |
| M15 観測欠損・安定性 | 取得統計、最終正常観測、サイズ/queue上限、Generation ID、欠損時計算、状態表示 | M12、M14のセッション仕様 | 取得失敗、欠損、長時間APIなしで架空データなし。次の正常レスポンスを取得。キュー上限と競合テスト合格。 |
| M16 統合試験 | HTTPS fixture、非干渉/非改変、外部通信、認証情報、異常終了、ブラウザ、CSV、DPI/取り込み、自動起動、24h+ | M11～M15 | 正式公開対象のすべての実施済み受入試験に合格。未実施を成功と記録せず、測定値・条件を保存。 |
| M17 正式配布準備 | 対応OS/ブラウザ、配布方式、依存/ライセンス、CI、ハッシュ、署名検討、README、移行案内 | M16 | 公開物とコミット・ハッシュを追跡可能、ライセンス・既知制約明記、開発者の公開承認取得。Releaseは承認後のみ。 |
| M18 Firefox | WebDriver BiDi正式仕様、Firefoxレスポンス取得、安全性、非干渉性、ICaptureSource統合 | M17後の追加候補 | CDP相当の安全性/ローカル完結/非干渉を合成・実機で示せる場合だけ採用。条件未達なら保留。 |

各Phaseで個別の報告書を作成し、Releaseビルド、自動テスト、必要な合成試験、実機確認、リスクと未解決事項を区別して記録する。

## 12. 次の手動確認

M11の実機確認を行う場合は、通常のゲームプレイ中に設定画面で所属サーバーを選択し、固定ゲームページを使う。期待結果はCDPが接続し、所属サーバー上の`api_port/port`を通常プレイで受信した後に計測が始まること。もし接続しない場合は、ツールチップに機密情報のない状態を確認し、ブラウザのCDP page URLのhost/path/query有無だけを報告する。APIを独自送信したり、任意URL/hostを一時的に許可したりしない。
