# M-Phase 0 調査・保全結果

調査日: 2026-11-26  
対象指示書: [`C_SHARP_MIGRATION_SPEC.md`](C_SHARP_MIGRATION_SPEC.md)

## 1. GitHubとローカル作業状態

- 対象GitHubリポジトリは公開リポジトリ`rinopap/SenkaSpeedMeasuringDevice`。既定ブランチは`main`、GitHub上で確認できたブランチは`main`だけだった。
- 調査開始時点のGitHubブランチは`main`だけだった。`main`から`develop-csharp`と`develop-cpp`を作成し、`main`は変更していない。C#移植とC++保全を別ブランチに分離した。
- リポジトリの現在の`main`はPython/Tkinter版で、READMEは74ENのJSON保存先を入力として案内している。ソースは`SenkaSpeedMeasuringDevice.py`。
- 現在のCodex作業フォルダと親の`Documents\Codex`はいずれもGit checkoutではなく、`.git`がない。したがって、このフォルダ内C++成果物についてローカルブランチ、コミット、未コミット差分をGitで識別できない。現ファイルをそのまま残し、変更の破棄・履歴書換えは行っていない。
- 元の作業フォルダには`.git`がなく、過去のC++コミット履歴を復元できない。そのため`develop-cpp`には2026-11-26時点のソース・仕様・検証記録を`cpp/`以下へスナップショットとして保全した。これは履歴復元ではない。Phase 6は未完了なので`cpp-phase6-baseline`タグは作成しない。
- 作業フォルダ内にはソースのほか、`bin/obj`を含むC#プローブ生成物、C++ビルド出力、配布ZIPの旧版が混在する。これらをC#製品へそのまま取り込まない。

## 2. 現行C++/Qt版の機能

保全した主なソースは[`cpp/main.cpp`](https://github.com/rinopap/SenkaSpeedMeasuringDevice/blob/develop-cpp/cpp/main.cpp)、ビルド設定は[`cpp/CMakeLists.txt`](https://github.com/rinopap/SenkaSpeedMeasuringDevice/blob/develop-cpp/cpp/CMakeLists.txt)。単一のC++20/Qt 6 Widgets実行ファイルで、以下を持つ。

- 74ENが保存する`api_port@port` JSONファイルの監視、書き込み途中ファイルの再試行、イベント重複抑止。
- JSON/`svdata=`/URLエンコード形式の解析、提督経験値・所持艦経験値合計の抽出。
- 時間帯別の戦果時速・艦娘経験値時速の計算、CSV読込・追記。
- 2タブGUI、設定ダイアログ、テーマ・フォント設定、サイズ自動調整、最前面表示、タスクトレイ、自動起動、古いレスポンスJSONの定期削除。
- 現在のソースには`ICaptureSource`とその74EN JSON監視実装がある。CDPの接続実装はまだない。

保存CSVは`timestamp,admiral_exp`と`timestamp,ship_total_exp`のヘッダーを使う。設定ファイルの主なキーは`dark_mode`、`font_family`、`font_size`、`always_on_top`。これらをC#版の互換基準にする。

この作業中の直近変更でQt本体から通信プロキシ起動UI/子プロセス制御は除去され、プロキシサイドカーはソース内の過去試験資料として残った。ただし、以前作成したPhase 6/CompletePackage ZIPの実行ファイルは旧UIを含む。現行ソースをビルドした配布物ではない。

## 3. Phase 0～6の達成状況

| Phase | 調査判定 | 根拠と未達 |
|---|---|---|
| 0: 既存コード調査 | 主要機能の調査済み | Git管理されたC++ブランチと正確なベースコミットを特定できない。 |
| 1: C++基盤 | 構築済み | Qt 6 Widgets/C++20のプロジェクトとビルドツリー、実行ファイルが残る。今回の最新差分を含めた再ビルドは未実施。 |
| 2: 計算・保存 | 実装済み、過去検証あり | 計算・CSV処理はソースに存在。すべての入力ケースを網羅する自動テストスイートは確認できない。 |
| 3: 74EN JSON互換 | 実装済み | `74en_responses`ファイル監視を実装。利用者からCSV読み込み確認の報告あり。実74ENからの長期的な実データ試験は限定的。 |
| 4: GUI/Windows連携 | 実装済み | 利用者はPhase 4まで問題なく完了したと報告済み。 |
| 5: 独立通信取得 | 未完了 | ローカルHTTP/HTTPSプロキシ試験は本文一致等PASSの記録があるが、実ゲーム/74ENでは未検証。旧方式の手動検証手順は新仕様で停止。 |
| 6: 74EN併用 | 未完了 | ローカル二段プロキシ/終了・再起動試験はPASSの記録がある。実74EN配布版、実ブラウザ、実通信、ウィンドウ取り込みの組合せは未検証。利用者は旧プローブをStep 7まで試したと報告。 |

詳細記録: [`PHASE5_TECHNICAL_REVIEW.md`](https://github.com/rinopap/SenkaSpeedMeasuringDevice/blob/develop-cpp/cpp/PHASE5_TECHNICAL_REVIEW.md)、[`PHASE6_COEXISTENCE_REVIEW.md`](https://github.com/rinopap/SenkaSpeedMeasuringDevice/blob/develop-cpp/cpp/PHASE6_COEXISTENCE_REVIEW.md)。これらはローカル試験記録であり、実74EN互換性を示さない。

## 4. 移植対象・再利用できる成果

| 領域 | C#版への移行 |
|---|---|
| JSON解析・経験値抽出 | `System.Text.Json`へ移植。異常・部分JSONを落とさず扱う。 |
| 戦果/艦娘EXP計算 | 現行数式・時刻境界仕様をそのまま再現し、C#単体テストを追加。 |
| CSV | 既存ヘッダー、時刻形式、既存データの読み込み互換を維持。過去CSVを変更・削除しない。 |
| UI設定 | `ui_config.json`キーと既定値を維持。破損設定時の既定値復帰を再現。 |
| 74ENモード | `FileSystemWatcher`、非同期チャネル、安定ファイル読込と再試行へ移植。 |
| GUI/Windows | タブ、外観設定、最前面、トレイ、自動起動、74ENウィンドウ取り込み運用をWPFで再実装。 |
| テスト資料 | Phase 5/6のローカルプローブと観測結果を履歴として参照。HTTP MITM試験をCDP成功の証拠に流用しない。 |
| プロキシ | C++版の履歴として保存。C#版へ移植せず、利用者が明示承認しない限り代替主要方式にしない。 |

実装前に、時刻のタイムゾーン・時間境界、同値経験値の別イベント記録、文字コード、読み込み途中のCSV、艦娘配列が空の場合の扱いを既存仕様とテストデータから確定する。

## 5. C#技術構成とソリューション案

- Windows 10/11 x64、WPF、簡易MVVM、`ClientWebSocket`、`HttpClient`、`System.Text.Json`、`FileSystemWatcher`、`System.Threading.Channels`。
- 現ローカル環境ではグローバル.NET SDK 8.0.200に加え、この作業用に.NET SDK 10.0.404が存在する。指示書の「現行LTS」に沿って.NET 10を初期候補にできるが、対応するWindows 10の最小ビルドとサポート期間をM-Phase 1で一次資料により確定する。
- ソリューション案は`SenkaSpeedMeasuringDevice.sln`、`src/SenkaSpeedMeasuringDevice.App`（WPF）、`src/SenkaSpeedMeasuringDevice.Core`（計算・型・保存・抽象化）、`src/SenkaSpeedMeasuringDevice.Capture`（CDP/JSON取得）、`tests/SenkaSpeedMeasuringDevice.Tests`（xUnit）。規模に対して過剰ならCoreとCaptureを統合する。
- Self-contained Releaseを目標とする。Single-fileはWPF/NotifyIconおよび配布方法の互換確認後に判断。外部ライブラリ追加は最小化する。

## 6. CDP/74ENの技術課題

- Chrome 136以降、既定ユーザーデータディレクトリへの`--remote-debugging-port`利用は無効で、別プロファイルが必要。既存の普段用プロフィールをそのまま安全に捕捉できるとは限らない。
- WebView2はホストの起動引数/環境設定でCDPポートを公開できる。74EN公開ソースでは`CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`を設定する実装を確認したが、配布版がCDPポートを公開しているか、外部クライアントとの接続が可能か、既存引数へ追加できるかは未検証。
- CDPは閲覧・応答取得だけのAPIではなく、ページ操作も可能な高権限インターフェース。許可コマンドを固定し、`Network.enable`、対象レスポンスイベント、`Network.getResponseBody`、終了時の`Network.disable`に絞る必要がある。`Fetch`、`Runtime.evaluate`、Cookie取得、再送・変更・ブラウザ操作は使用しない。
- `/kcsapi/api_port/port`の正確なscheme/host、リダイレクト、iframe/OOPIF、requestIdのスコープ、本文バッファ保持期限、Base64・圧縮本文、複数タブの選択をローカルブラウザで先行確認する。
- リモートデバッグポートはloopback限定でも同一端末内プロセスからアクセス可能。接続対象の絞り込みだけでなく、ブラウザ側の明示同意、デバッグポートの短時間化/閉鎖、異常終了時の残留確認を設計する。
- CDPが不成立の場合もプロキシへ自動で戻さない。74EN JSON監視を残し、ブラウザ拡張方式等を再評価する。

## 7. リスク・次工程

| リスク | 対応 |
|---|---|
| C++資産がGitで管理されておらず、厳密な差分・履歴がない | 既存フォルダと旧ZIPを保持。C#ブランチではC++版を削除しない。必要なら後日、利用者レビュー用のC++スナップショットを別途コミットするが、Phase 6完了扱いにはしない。 |
| C#移行でCSV/計算仕様が変わる | 既存サンプルをxUnitテストに移植し、CSVはコピーしたテストデータで検証する。 |
| CDP有効化がブラウザ/74ENの再起動や設定変更を要する | 自動変更せず、対象・範囲・復元手順を示して利用者の同意を得る。 |
| CDP target管理が複雑（iframe、複数タブ、ブラウザ再起動） | M-Phase 5で小規模プローブにより成立条件を先行確認。実機接続前にローカル模擬サーバーで異常終了を確認。 |
| Self-contained/WPFの配布サイズが大きい | 性能・配布サイズを測定して報告し、推測で軽量と断定しない。 |
| 旧Phase 6パッケージにプロキシUIが残る | [`../../README_CURRENT_STATUS.md`](../../README_CURRENT_STATUS.md)で旧版と明示。C#版成果物として配布しない。 |

### M-Phase 0完了判定

調査、既存成果の分類、C#移行設計、未検証事項の記録、C#ブランチ準備を完了した。M-Phase 1のコード作成・プロジェクト作成にはまだ着手していない。本報告後、指示書どおりM-Phase 1へ進む。
