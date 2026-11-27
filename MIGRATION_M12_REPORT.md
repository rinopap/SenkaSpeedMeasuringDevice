# M-Phase 12 実施報告：CDPセキュリティ強化

実施日: 2026-11-27  
基準仕様: [`docs/NEXT_DEVELOPMENT_SPEC.md`](docs/NEXT_DEVELOPMENT_SPEC.md)  
前Phase: [`MIGRATION_M11_REPORT.md`](MIGRATION_M11_REPORT.md)

## 1. 実装内容

- CDPコマンドを固定許可方式に変更し、引数・セッション・requestIdを個別検証するようにした。
- 許可コマンドは`Page.enable`、`Page.getFrameTree`、`Page.disable`、`Network.enable`、`Network.disable`、`Network.getResponseBody`、`Target.setAutoAttach`に限定した。
- `Network.enable`の受信バッファを8 MiBに固定し、追加引数を拒否する。
- `Network.getResponseBody`は、選択サーバーの固定API URL、2xx応答、現行ページ世代、既知の有効セッション、登録済みrequestIdをすべて満たす場合だけ許可する。取得完了、失敗、セッション切断、ページ遷移で登録を破棄する。
- CDP受信イベントを受信時点の世代番号と結びつけた。ページ遷移と本文取得が競合した場合、古い世代の本文を発行しない。本文の最終発行と世代無効化は同じロックで直列化する。
- `Page.frameNavigated`および`Page.navigatedWithinDocument`を監視する。選択ゲームページから離れた場合、iframeの自動接続とNetwork観測を停止し、正規URLへ戻った場合に再開する。CDP監視の停止はゲーム通信を停止・変更しない。
- `Page.getFrameTree`と`Page.frameAttached`からフレーム関係を記録し、選択ページ配下として確認できるiframe/OOPIFセッションだけを許可する。親フレーム切断時は子セッションのrequestIdを無効化する。保持するiframeセッション数は最大64。
- CDP接続先はloopback、設定ポート、`/devtools/page/{targetId}`、一覧に記載されたtarget IDの一致を検査する。接続先探索とWebSocket通信は直接接続とし、HTTPリダイレクトおよび環境プロキシを使用しない。
- CDPのtarget一覧は256 KiB、WebSocketメッセージは12 MiB、イベントキューは512件、未完了API requestは256件までに制限した。上限超過時は計測接続を切断して再接続し、ゲーム通信に介入しない。
- 接続・コマンド・終了時の待ち時間に上限を設定し、通信失敗時の待機・再接続および停止処理を整理した。
- `Fetch`、`Runtime.evaluate`、Cookie取得、ヘッダー変更、キャッシュ変更、再送、リロード、入力操作等は実装していない。

## 2. 変更ファイル

- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCommandPolicy.cs`（新規）
- `src/SenkaSpeedMeasuringDevice.Capture/CdpEndpointPolicy.cs`（新規）
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCommandPolicyTests.cs`（新規）
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpEndpointPolicyTests.cs`（新規）
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCaptureSyntheticTests.cs`（新規）
- `MIGRATION_M12_REPORT.md`（本報告書）

## 3. 再利用した既存実装

- M11の固定ゲームページ、所属サーバー台帳、APIホスト・パス・HTTPS判定。
- M9/M11の`ICaptureSource`、CDPページ選択、iframe/OOPIF対応、受信本文解析、計算、CSV追記。
- 74EN JSON監視と既存設定・UI。アプリ側の計算仕様、CSV形式、設定UI、保存先は変更していない。

## 4. 設計判断と理由

- 汎用CDPクライアントは作らず、アプリ内の固定用途に必要なメソッドだけを許可した。
- Target自動接続のフィルターは`iframe`のみを含め、`waitForDebuggerOnStart=false`を固定した。ページの実行を停止する引数は受け付けない。
- ページ離脱時はCDPのNetwork観測だけを停止する。計測処理がゲーム通信経路にない構成を維持する。
- CDPイベント受信時に世代を割り当てることで、イベントキュー処理が遅れた後に古いページのデータを新しいセッションのデータとして扱わない。
- 不明な引数、URL、フレーム関係、セッション、応答本文は計測対象から除外する。補完リクエストや再取得は行わない。
- CDPは高権限のデバッグプロトコルであり、Chromeでは公開・安定APIとしての互換性保証がない。対応ブラウザの拡大と承認型接続方式の調査はM13で別途扱う。[公式CDP仕様](https://chromedevtools.github.io/devtools-protocol/tot/Network/)、[Target仕様](https://chromedevtools.github.io/devtools-protocol/tot/Target/)、[Page仕様](https://chromedevtools.github.io/devtools-protocol/tot/Page/)

## 5. 仕様書との差分

- M12に列挙されたコマンド許可、引数検証、接続先検証、ページ遷移監視、iframe/OOPIFのセッション制御、Fail-closed、異常データ上限を実装した。
- 既存仕様の緩和、任意URL/APIホスト設定の再導入、Fetch等の禁止機能追加は行っていない。
- M12の実機ブラウザ条件は本作業では検証していない。M11から引き継いだ正規URL形状の実機未確認も継続する。
- 「localhost上のCDP接続元プロセスが正規ブラウザであること」の完全な認証は行っていない。loopback、ポート、target ID、ページURLの検査は行うが、同じPC上の悪意あるプロセスを完全に識別する手段とはみなさない。

## 6. Releaseビルド結果

- SDK: .NET 10.0.404。
- `dotnet build SenkaSpeedMeasuringDevice.sln -c Release --no-restore -p:UseSharedCompilation=false`: 成功。
- 警告0件、エラー0件。
- WPFアプリ、Core、Capture、自動テスト、CDP合成プローブを含むソリューションをビルドした。
- Self-contained配布物のpublishおよびZIP生成は行っていない。

## 7. 自動テスト・回帰試験結果

- Release自動テスト: **61件合格、失敗0件、スキップ0件**。
- M11時点の44件を維持し、CDP許可ポリシー、endpoint制約、遷移時の旧世代無効化、フレーム祖先、切断済みiframe、loopback合成CDPの試験を追加した。
- CSV、計算、設定、74EN JSON監視の既存テストもソリューション全体で実行した。CSV形式・計算・App側コードの変更はない。
- 合成CDP fixtureでは、離脱ページのAPIイベントと選択外APIホストに対して`Network.getResponseBody`が送られないこと、正規ページ復帰後の選択サーバーAPIと有効iframeセッションだけが取得対象になること、切断後のiframeセッションから本文を取得しないことを確認した。
- 記録されたCDPコマンドに、許可リスト外のコマンドがないことを確認した。

## 8. 合成通信試験

- Windowsローカルloopback上の合成HTTP `/json/list`とCDP WebSocketサーバーを使用した。外部サーバーおよびゲームサーバーには接続していない。
- 合成イベント上で正規ページからの離脱、復帰、選択外APIホスト、選択サーバーAPI、iframe attach/detachを順に検証した。
- API宛の独自リクエストは一切送信していない。合成API URLはCDPイベント本文のメタデータとして与え、本文はローカルWebSocket応答で返した。
- 中継前後のゲーム通信本文一致試験は対象外。CDP方式はゲーム通信を中継しないためである。
- HTTPSのローカルTLSサーバーを使った試験は未実施。実ブラウザのTLSを置き換えたり、証明書検証を無効化した試験はしていない。

## 9. 実機試験結果

- 今回の作業中、実ゲーム、通常ブラウザ、74EN/WebView2へ接続していない。
- M12変更後の画面表示、CDP接続、通常プレイ中の経験値計測、ページ遷移、OOPIF挙動、終了後のゲーム継続は未検証。
- 前PhaseおよびM10までに利用者が確認した実機結果は履歴として保持し、今回の試験成功とは扱わない。

## 10. セキュリティ・プライバシー評価

- CDP探索は`127.0.0.1`に固定し、プロキシ無効・自動リダイレクト無効。WebSocketのhost、port、scheme、path、target ID、userinfo/query/fragmentを検証する。
- 許可対象外、遷移旧世代、無効セッション、未登録requestId、2xx以外、選択外APIホストからレスポンス本文を要求しない。
- リクエストヘッダー・リクエスト本文・Cookie・認証情報は解析・CSV保存・ログ出力していない。CDPの生イベントやAPIレスポンス全文をファイルへ保存しない。
- ページ離脱、上限超過、 malformed message、本文取得失敗、接続切断は計測取得だけを中止・再接続させる。ゲーム通信の再送やブラウザ操作は行わない。
- `Network.enable`は対象ページ/iframeのNetworkイベントをCDPメモリ上で受け取る。イベントには対象外通信のメタデータやヘッダーが含まれる場合があるが、製品処理では許可APIのURLとstatus以外を解析・保存しない。CDP自体の高権限性とローカルプロセスからの接続リスクは残る。

## 11. 残存リスク

- loopbackはローカルプロセス認証ではない。悪意ある同一PCプロセスがブラウザを偽装した場合の完全防御は保証できない。
- Chromium/Chrome/WebView2および将来バージョンのCDP互換性は保証されない。公式仕様もtip-of-treeであり、変更される可能性がある。
- Frame tree/Target attachmentの実ブラウザ差、複数タブ、ページ再生成、OOPIFの長時間動作は未実機検証。
- イベントキューが512件を超えると観測接続を切断し、対象レスポンスを欠損する可能性がある。計測機が通信を止めないことを優先する。
- 64を超えるiframe/OOPIFや、8 MiBを超える対象本文は取り込まない。再送や上限拡大はしない。
- M11報告で未解決の正規ゲームURL形状、サーバーAPIホスト一覧の実機照合、実画面の確認は継続課題。

## 12. 未解決事項・未検証事項

- 実ブラウザでの`Page.enable`、`Page.getFrameTree`、`Target.setAutoAttach`の互換性と実際のイベント順序。
- 実ゲーム中のページURL形状と、M11固定ポリシーとの一致。
- 74EN内蔵WebView2のM12変更後の動作、ウィンドウ取り込みとの併用。
- CDP endpoint所有プロセス/署名の検証可能性。これはM13の安全な接続方式調査へ引き継ぐ。
- 24時間以上の連続動作、CPU/メモリ実測、DPI、自動起動、終了・異常終了後の実機再開。
- HTTPS/TLSローカル合成試験と既存CSVの利用者環境での手動回帰。

## 13. M12受入状況

| 条件 | 状況 |
|---|---|
| 許可対象以外の本文を取得しない | 実装・loopback合成試験済み |
| 通信を変更・停止・再送するCDP機能を追加しない | 許可コマンド監査・合成試験済み。禁止コマンドなし |
| ページ遷移と旧世代イベントを遮断 | 実装・世代競合単体/合成試験済み |
| iframe/OOPIFセッション・親フレーム・切断を検証 | 実装・単体/合成試験済み。実機未検証 |
| 計測機異常がゲーム通信に波及しない | 非仲介の接続構造とローカル試験で確認。実ゲーム異常終了試験は未実施 |
| Releaseビルド・自動テスト | 成功、警告0/エラー0、61件合格 |

コード・合成試験に関するM12完了条件は満たした。ブラウザ実機、74EN実機、異常終了時の実ゲーム継続は検証していないため、その範囲を成功とは記録しない。

## 14. 利用者による確認が必要な項目

M12更新版を利用できる状態になった後、通常の対応ブラウザまたは74EN環境で次を確認する。

1. ゲームを通常どおり起動し、設定済みのCDPポートと所属サーバーで計測機を起動する。
2. タブ行の状態が「CDP接続中」になり、通常プレイで母港APIを受信した後に計測値が更新されることを確認する。
3. 計測機を終了し、ゲーム通信が続くことを確認する。強制終了試験は、通常終了確認後に必要であれば行う。
4. 74ENでは既存JSON監視および内蔵ブラウザCDPの選択を切り替え、二重監視やCSV重複が発生しないことを確認する。

独自APIリクエスト、任意URL設定、証明書変更、システムプロキシ変更は不要。失敗時はURL全体やAPIデータを共有せず、表示状態とブラウザ種別だけを記録する。

## 15. 次Phaseへの影響

- M13は本報告の確認後に別途着手する。
- M13ではChrome承認型接続、ブラウザ別CDP差、待受とローカルプロセス脅威、74EN/WebView2の対象互換性を調査する。
- M12ではブラウザ起動方式、利用可能ブラウザ範囲、許可URL形状、ポート所有者の安全性を確定していない。
- M15では取得欠損統計・長時間安定性・キュー満杯時の運用表示を追加検討する。

本作業ではM13以降の実装、配布ZIP作成、正式リリース、Git履歴操作は行っていない。

