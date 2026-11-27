# M-Phase 12.1 作業報告

作業日：2026年11月27日  
対象：M-Phase 12完了後、M-Phase 13着手前  
判定：**コード実装および合成テスト完了。実機受入は保留。M13には着手していない。**

## 1. M12実装の再調査

M12報告書が記録する基準コミットは `54787cb187fd63f128be50a99bd8e9b90239364d`。同報告書にはReleaseビルド成功、既存61テスト成功、実ブラウザ未試験と記録されている。

今回調査した作業フォルダーには `.git` メタデータがなく、GitのHEAD、作業ツリー差分、および基準コミットとの完全なファイル単位比較は実施できなかった。したがって、基準コミットの識別はM12報告書の記録に基づく。ローカルソースコード自体は読み、以下の問題を確認した。

- APIレスポンス解析で `api_result` を検証せず、本文中の任意の `{` より前を切り捨てていた。
- CDP初期ポリシーがページ許可状態で始まり、個々の要求のframeId/loaderIdを結び付けていなかった。
- Network応答イベントだけで本文取得許可を作れる構造で、requestId再利用時に旧許可が残り得た。
- 子iframeセッションは、所属するフレームツリーを再確認する前にNetwork監視を有効化していた。
- CDP応答はIDのみで対応付け、生応答エラーを例外文として保持していた。応答セッションの照合もなかった。
- CDPイベント全体を `JsonElement.Clone()` し、最大512件のキューに保持していた。
- HTTP target-listはヘッダー後の本文読み込みに独立タイムアウトがなく、WebSocket Binaryメッセージもテキスト同様に解析していた。
- CDP受信イベントのハンドラーが同期的にJSON解析、CSV追記、画面更新へ進み得た。

## 2. 修正内容

### APIレスポンス解析

- 対象APIパスを固定値と照合。
- JSONルートをオブジェクトに限定し、数値 `api_result == 1` とオブジェクト型 `api_data` を必須化。
- `api_basic.api_experience` および各 `api_ship[].api_exp[0]` の非負64bit整数を検証。
- ship合計はchecked演算し、合計オーバーフロー時は艦娘合計だけを無効化する。妥当な提督経験値は保持する。
- 通常JSON、明示的な `svdata=`、既存試験で扱う明示的なpercent-encoded JSONのみを受け入れ、任意の前置文字列を切り捨てない。
- 8 MiBを超える本文、空本文、不正JSON、数値異常は観測値にしない。
- 解析対象の経験値上限を新設していない。

### CDPページ・フレーム・要求の照合

- 初期ページ許可をfalseに変更。正規のゲームページ確認後に限りNetwork監視を有効化。
- トップフレーム遷移で監視世代、子セッション、フレーム、loader、要求許可をリセット。
- `Network.requestWillBeSent` の要求URL、frameId、loaderId、セッションおよび世代を照合して仮許可を登録。
- requestIdの再利用ごとに旧許可を破棄。レスポンスURL、整数のHTTP 2xx、frameId、loaderIdが要求時と一致した場合だけ本文取得へ進む。
- 不明なフレーム、loader不一致、未登録の子セッションからは本文を取得しない。
- iframe/OOPIFの子セッションではPageを有効化し、そのセッションから取得したフレームツリーを親子関係へ照合してからNetworkを有効化。
- フレーム上限256、未完了要求上限256、子セッション上限65を維持または設定。上限超過時は監視をFail-closedにする。
- `Network.getResponseBody` はWebSocket送信ロック取得後にも許可状態を再検証。応答もコマンドIDと期待セッションを照合し、遅延・不明・セッション違いの応答を破棄。

### リソースと受信処理

- WebSocketメッセージ上限は12 MiB。本文取得バッファ上限8 MiBに対応するBase64結果を受信できる単一メッセージ上限として維持。
- WebSocketから取り出した対象イベントだけを許可リスト化し、ID、URL、フレーム、loader、ステータス等の必要項目だけを最大4 KiBの投影JSONへ変換してキューへ渡す。対象外イベントはキューに複製しない。URLは対象APIまたは許可ゲームページの場合のみクエリを除いた形で保持する。
- 投影済みイベントのキューは512件固定容量。容量超過時は当該CDP接続を破棄し再接続する。理論上の保持量はイベントJSON約2 MiB以下（512×4 KiB。オブジェクト等の管理領域を除く）。
- WebSocket Binaryメッセージは拒否。CDP応答エラーの生文字列を例外や画面へ出さない。
- `/json/list` 本文には独立3秒タイムアウトを設定。既存のloopback限定、HTTPリダイレクト禁止、環境プロキシ無効、WebSocket接続先検証を維持。
- 経験値解析・CSV追記・画面更新をCDP受信経路から分離し、128件および総保持16 MiBの計測キューへ移動。個々の本文は最大8 MiB。取得方式切り替え時に世代が変わった旧キュー項目は保存前に破棄。

## 3. 変更ファイル

- `src/SenkaSpeedMeasuringDevice.Core/ApiResponseParser.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCommandPolicy.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/ApiResponseParserTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCaptureSyntheticTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCommandPolicyTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/FileCaptureSourceTests.cs`
- `MIGRATION_M12_1_REPORT.md`

## 4. 既存仕様・互換性

- WPF/.NET 10、`ICaptureSource`、CDPと74EN JSONの取得方式、GUI、計算ロジック、CSVヘッダー・保存形式は変更していない。
- 74EN JSON監視テストを `api_result: 1` を含む実API形式に合わせて更新した。実際の利用者環境の保存ファイルとの照合は未実施。
- エラー応答・不正応答は新たに拒否される。これはM12.1のAPI成功状態要件による意図した差分。
- CDPの許可条件を厳しくしたため、ブラウザが必須frameId/loaderIdを省く、またはOOPIFのPageフレームツリーを返さない場合は取得欠損となる可能性がある。通信を変えず、該当セッションをFail-closedにする。

## 5. ビルドと自動テスト

- Release自動テスト：**73 passed / 0 failed / 0 skipped**。
- Release WPFアプリビルド：**成功、警告0、エラー0**。
- テスト実行にはWindows SDKのレジストリ参照が必要だったため、許可されたビルド実行環境を使用した。
- 73件にはM12報告の既存61件と、今回追加・更新したテストが含まれる。

主な追加・更新試験：

- 成功・異常・欠落 `api_result`、ルート配列、`api_data`欠落/null、負数、null/文字列の艦娘要素、合計overflow、任意prefix、対象外APIパス。
- API成功状態を含む74EN JSON監視テスト。
- 初期Fail-closed、frameId/loaderId不一致、新しいトップフレーム、旧世代無効化。
- 合成CDP上の要求前提、APIホスト、要求と応答のframe/loader一致、OOPIFセッションのPageフレームツリー照合、requestId再利用・対象外遷移、切断時の本文拒否。

## 6. 合成通信試験

ローカルのHTTP/WebSocket合成CDPテストは成功。合成サーバーはローカルでのみ動作し、ゲームAPIへ接続しない。APIリクエストの生成、改変、再送、インターセプト機能は追加していない。`Network.getResponseBody` は受信済み応答を読む読み取り専用コマンドに限定している。

HTTP本文停止時の独立タイムアウト、バイナリWebSocket拒否、上限超過後の再接続、および応答セッション不一致については専用の自動試験を追加できていない。実装側の上限・拒否経路は存在するが、これらを合成試験成功として数えない。

## 7. 実機回帰試験

本作業環境では通常ブラウザ、実ゲーム、74EN/WebView2を起動していない。したがって次の項目は**未検証**：

- 実ゲームの母港API、経験値・戦果時速、CSV追記。
- 通常ブラウザでのページ離脱・復帰・再読込。
- 74EN JSON監視、74EN内蔵WebView2 CDP、ウィンドウ取り込み、取得方式切り替え、二重記録の有無。
- renderer swapを含む実ブラウザOOPIFのフレームツリー形式。
- アプリ正常終了・強制終了中のゲーム通信継続。

### 利用者向け確認手順

1. 既存データのバックアップを取る。M12.1テストでは既存CSVを初期化しない。
2. 更新後Releaseアプリで通常ブラウザ接続を行い、艦これの正規ページで「CDP接続中」を確認する。
3. 母港画面へ移動または通常プレイで経験値が増える操作を行い、提督・艦娘経験値、時速、CSV追記が従来どおり更新されることを確認する。
4. 艦これページから別ページへ離れた時に対象ページ外となり、別ページの通信が記録されないことを確認する。正規ページへ戻った後、接続と計測が再開することを確認する。
5. ゲームページを再読み込みし、旧画面の許可が残らず、再認識後に計測が再開することを確認する。
6. 74EN JSON監視を選んで従来のJSON保存経由で計測し、既存CSVへ正しい形式で追記されることを確認する。
7. 74EN内蔵WebView2のCDP接続を行い、母港API計測、ウィンドウ取り込み、設定での取得方式切り替えを確認する。1応答につきCSV記録が重複しないことを確認する。
8. 計測機を正常終了した後、必要ならタスクマネージャーから強制終了した後も、ゲームプレイが継続することを確認する。再起動後は再接続することを確認する。
9. CSVは列構成・行形式が従来どおりであり、経験値以外のAPI本文や認証情報が追加保存されていないことを確認する。

利用者試験で経験値が更新されない場合は、時刻、選択した取得方式、表示された短い状態名、ブラウザ種別のみを共有する。Cookie、トークン、レスポンス本文、CDP WebSocket URLは共有しない。

## 8. セキュリティ評価と残存リスク

- ゲーム通信の経路に計測機を挿入せず、CDPの観測専用コマンドだけを利用する。
- JavaScript注入、Fetchインターセプト、認証情報の取得・保存、ゲーム通信の再送・改変を追加していない。
- 計測キューはプロセス内だけで、CSVには既存の経験値時系列だけを出力する。
- CDPは強いデバッグ権限を持つ。loopback待受のみでは同一端末の他プロセスによるアクセスを完全に防止できない。この評価・承認型接続方式・プロセス署名・動的ポート等はM13へ引き継ぐ。
- 12 MiBの単一WebSocket受信バッファは有界だが、単一の大きなイベント受信時にはその規模の一時メモリが必要になり得る。
- 実ブラウザのOOPIFイベント順序・各派生ブラウザ互換性は合成試験だけでは保証できない。

## 9. 未解決事項とM13への引き継ぎ

- 実機受入試験が未完了のため、M12.1の完全受入は保留。
- Gitメタデータがない作業フォルダーのため、基準コミットとの完全diff、ブランチ状態、変更コミットの作成は未実施。
- 実際の74EN保存JSONサンプルとの比較は未実施。更新済み合成テストは形式互換の確認に限る。
- CDP応答セッション不一致、target-list遅延本文、キュー超過、大量フレーム、連続切断の専用テストを追加する余地がある。
- Chrome承認型CDP、Vivaldi、ポート所有プロセス、ブラウザ署名、専用プロファイル、動的ポート、第三者ローカルプロセス、WebView2固有評価はM13以降。
- サーバー/アカウントごとの履歴分離はM14、観測時刻・イベントID・欠損指標はM15、HTTPS合成通信を含む統合試験はM16の範囲であり、今回変更していない。

M13には着手していない。実機確認および本報告のレビュー後に次工程を決定する。

## 10. 参考資料

- [Chrome DevTools Protocol: Network](https://chromedevtools.github.io/devtools-protocol/tot/Network/)
- [Chrome DevTools Protocol: Page](https://chromedevtools.github.io/devtools-protocol/tot/Page/)
- [MIGRATION_M12_REPORT.md](MIGRATION_M12_REPORT.md)
