# EO-Phase 0 調査報告書

策定日：2026-11-28  
対象：SenkaSpeedMeasuringDevice 74EN専用版 / EO-Phase 0  
判定：**設計調査とGit履歴保全は完了。EO-Phase 1の実装開始には、対象74EN実機データベースの読み取り確認が先行条件として残る。**

## 1. 調査範囲と資料

- ユーザー提供「SenkaSpeedMeasuringDevice for ElectronicObserverEN 74EN専用版 開発統合仕様書」全文。
- 今後の作業コピーとして保存した [`docs/EO_DEVELOPMENT_SPEC.txt`](./docs/EO_DEVELOPMENT_SPEC.txt)。
- 74EN公式リポジトリ `ElectronicObserverEN/ElectronicObserver` の指定コミット `8657bea3c6db3efaf37de45a6aba91ebd5c4e709`。
- SenkaSpeedMeasuringDevice C#成果物スナップショットと `PROJECT_HISTORY_AND_TEST_SUMMARY.md`、M1～M14.1の報告書。読み取り専用で参照。
- SenkaSpeedMeasuringDevice GitHub公開ブランチ一覧。

74EN調査は公開ソースコード調査である。74EN 5.4.3EN実機のSQLiteファイルを今回読み取ったわけではない。ユーザーが以前に確認した動作結果は履歴上の報告として扱い、今回の実機検証結果とは区別する。

## 2. 現在の作業環境とGit状態

当初の作業ディレクトリはGit管理されていないChatGPTプロジェクトのローカルミラーで、`sources/`は読み取り専用だった。既存のC#成果物も`.git`メタデータのない出力スナップショットだったため、そのままでは履歴や作業差分を確認できなかった。

GitHub上では現時点で以下のブランチが確認できた。

| ブランチ | 先端コミット |
|---|---|
| `develop-cpp` | `6f8547ce62fee9656e29b598b5f73b22ce546b58` |
| `develop-csharp` | `8a9e4c7bcd7422f5f47bccb460a917cff352df53` |
| `main` | `17c104967db9e71e67f904b9939561e278ba2522` |

公開`develop-csharp`にはM14.1の成果物が含まれていなかったため、このブランチを直接開発起点にはせず、同ブランチを親に持つ新しい作業コピーを作り、手元のM14.1スナップショットを取り込んだ。`develop-csharp-eo`を`develop-csharp`先端`8a9e4c7bcd7422f5f47bccb460a917cff352df53`から作成し、必要なソース・テスト・資料をコミットした。2026-11-28にGitHubへpushし、リモート先端がコミット`21f47201978b51133d1fcdbc2374b87b78cf1ac0`と一致すること、作業ツリーがcleanであることを確認した。既存ブランチや履歴の書き換え、強制push、正式リリースは行っていない。

## 3. 現行C#版の確認と再利用候補

M10までのC#版は .NET 10 / WPF のApp、Core、Capture、xUnitテストの分割構成で、`ICaptureSource`を用いた取得元抽象化、戦果・艦娘経験値計算、時速表示、CSV互換、設定・タブUI、74EN JSON監視、CDP監視を持つ。過去報告には通常ブラウザCDPと74EN WebView2 CDPの実機計測報告がある。M14.1では認証準備とCDP開始操作を分離している。

74EN専用版で再利用候補とするもの：

- 戦果計算、時速算出、CSV読書き、時刻窓管理など、通信方式に依存しないCore。
- 既存GUIの表示・設定・時速更新機能。ただし配布UIからCDP・ブラウザ操作を外す。
- `ICaptureSource`契約と既存JSON監視を互換・比較用の別取得元として維持する設計。
- xUnitの計算・CSV・状態管理テスト。

CDP実装やブラウザ起動コードは、専用配布物へ持ち込まない。ソース履歴自体は保全する。SQLite取得元は別クラスとして追加し、DBアクセスと経験値集計を既存の計算・表示経路から分離する方針が適切。

M14.1報告書ではWindows x64 Release発行および95テスト成功と記録されているが、本EO-0ではC#成果物が読み取り専用の非Gitスナップショットであり、ビルド・テストを再実行していない。これらは前報告からの引用であって、EO-0で再確認した結果ではない。

## 4. 74EN公式ソースで確認したSQLite仕様

指定コミットの主な参照箇所：

- [ElectronicObserverContext.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Database/ElectronicObserverContext.cs)
- [ApiFile.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Database/KancolleApi/ApiFile.cs)
- [ApiFileService.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Services/ApiFileService/ApiFileService.cs)
- [ApiFileType.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Database/KancolleApi/ApiFileType.cs)
- [ApiFiles作成migration](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Database/Migrations/20220824161118_AddApiLogging.cs)
- [ApiFiles Version追加migration](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Database/Migrations/20221101121658_AddVersionToApiFiles.cs)

### 確認済みの事実

1. `ElectronicObserverContext`はDBを相対パス `Record/ElectronicObserver.sqlite` で開く。したがってDB位置はインストール先だけでなく、74ENの実行時カレントディレクトリに依存する。配布版のショートカットや起動方法によっては単純な実行ファイル隣接パス推測が外れる。
2. EFモデルの`ApiFile.Content`はC#側では文字列だが、SQLiteの`ApiFiles.Content`は`BLOB`。migrationは `Id INTEGER PRIMARY KEY AUTOINCREMENT`、`ApiFileType INTEGER`、`Name TEXT`、`Content BLOB`、`TimeStamp TEXT` を作成し、後のmigrationで`Version INTEGER`が追加されている。
3. 74ENは`Content`をUTF-8バイト列にして`BrotliStream`で圧縮してからDBへ格納し、読み出し時はBrotli展開後UTF-8文字列に戻す。単なるTEXT JSONではない。
4. `ApiFileType.Request=0`、`Response=1`。保存処理は通常、リクエストとレスポンスを別々の行として追加する。
5. `SaveApiData`は`svdata=`を外し、`api_port/port`では`api_ship`、`api_deck_port`、`api_log`、`api_ndock`だけを空配列に置換する。`api_data.api_basic`はこの処理で削除されない。したがって仕様書記載の提督経験値取得候補`api_data.api_basic.api_experience`は残る見込みが高い。ただし対象実機DBでJSON内容を確認するまでは、実データの存在・型を確定扱いにしない。
6. 保存処理はリクエストをフォームパラメータからJSON化し、レスポンスは`svdata=`除去・port応答の一部トリム後に保存する。対象APIだけ読む設計とし、既存行を一括展開・複製保存しないこと。
7. API記録は無視対象リストがあり、指定APIが必ず保存されるとは限らない。調査した版の除外リストには`api_port/port`や戦闘結果API、`api_req_mission/result`は含まれない。

### 注意・未確定

- DBの相対パスは、アプリ側で固定パスを想定せず、74ENユーザーが選択したDBを読み取り専用で検査する導線を設けるべきである。自動検出を追加する場合も、候補の存在確認のみとし、任意ファイル走査やプロセス注入をしない。
- 実行時に74ENがDBを書き込む。読み取り専用接続側は短い読取処理、共有違反・ロック・BLOB破損時の再試行制限を設け、DB・WAL・SHMファイルへ一切書き込まないことを合成DBと実機コピーで検証する。
- `ApiFile`クラスに`SortieRecordId`があるが、SQLite上の実列や外部キー構成はmigrationと現行DBの両方で確定させる必要がある。APIログ取得では関連テーブルを更新しない。
- 調査コミットの`ApiFiles.Version`は保存処理で1。古い履歴や将来版の形式差を想定し、未知のVersionを黙って展開・解析しない。

## 5. APIと経験値フィールドの調査

### 5.1 母港・提督経験値

`api_port/port`応答の`api_data.api_basic.api_experience`を累積値として読む方針は、port応答トリム実装と整合する。提督経験値の新規差分は、順序づけた母港応答の累積値差から算出する。初回値は基準値として扱い、負差分、同時刻・重複行、DB再走査による二重適用、提督レベル変更境界を個別に処理する。

従来の提督戦果時速計算は既存ロジックを維持し、DB取得を経験値ソースに接続する。差分の7/10000換算等は現行コアの既存仕様を流用し、EO-1で既存テストを基準に回帰させる。

### 5.2 API種別・フィールド

指定コミットの生成済みAPI型で以下を確認した。

| イベント | 型で確認できるフィールド | 確認できる内容 |
|---|---|---|
| `api_req_sortie/battleresult` | `api_get_exp`, `api_get_base_exp`, `api_get_ship_exp: List<int>`, `api_get_exp_lvup`, `api_mvp`, `api_ship_id` | 各フィールドの型と存在 |
| `api_req_combined_battle/battleresult` | 上記に加え`api_get_ship_exp_combined`, `api_get_exp_lvup_combined`, `api_mvp_combined` | 連合艦隊向け配列が独立して定義 |
| `api_req_practice/battle_result` | `api_get_exp`, `api_get_base_exp`, `api_get_ship_exp`, `api_get_exp_lvup`, `api_mvp`, `api_ship_id` | 演習結果に同様の通常艦隊配列 |
| `api_req_mission/result` | 動的応答から`api_get_exp`, `api_get_ship_exp`, `api_get_exp_lvup`を利用 | 遠征の提督・艦娘経験値を処理 |

参照ソース：

- [通常戦闘結果型](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver.KancolleApi.Types/ApiReqSortie/Battleresult/APIReqSortieBattleresultResponse.cs)
- [連合戦闘結果型](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver.KancolleApi.Types/ApiReqCombinedBattle/Battleresult/APIReqCombinedBattleBattleresultResponse.cs)
- [演習結果型](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver.KancolleApi.Types/ApiReqPractice/BattleResult/APIReqPracticeBattleResultResponse.cs)
- [通常・連合戦闘の共通型](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver.KancolleApi.Types/Interfaces/ISortieBattleResultApi.cs)
- [遠征結果ハンドラ](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/8657bea3c6db3efaf37de45a6aba91ebd5c4e709/ElectronicObserver/Observer/kcsapi/api_req_mission/result.cs)

### 5.3 確認済みと推測の切り分け

**確認済み：** 74ENの型定義は経験値候補配列、MVP値、レベルアップ情報、艦ID候補を区別して保持している。連合艦隊結果には第二艦隊分の`*_combined`フィールドが存在する。74EN遠征ハンドラは`api_get_exp`を提督経験値、`api_get_ship_exp`の最小値を表示用艦娘経験値として扱い、配列要素ごとに`api_get_exp_lvup`と照合している。

**推測（実レスポンス確認が必要）：** `api_get_ship_exp`の各要素が最終的な各艦の獲得量であれば、旗艦・MVP倍率はすでに反映済みで、計測側で重ねて倍率を掛けるべきでない。フィールド名と74ENの利用方法だけでは、全戦闘条件における値が倍率適用後か、艦隊のスロット順と完全に一致するか、空枠・退避艦・轟沈艦・上限艦を含むかまでは確定できない。

`api_get_base_exp`は基礎経験値として別フィールド、`api_get_exp`は提督経験値として存在するため、艦娘総獲得量へこれらを足す根拠はない。`api_mvp`は数値型のMVP情報であり、獲得量を補正する用途と決めつけない。初期実装では各イベントの`api_get_ship_exp`配列（連合は対応する両配列）を候補とし、実データの前後差と照合してから採用する。

### 5.4 編成・イベント重複・上限

- 通常艦隊は通常配列、連合艦隊は通常艦隊用・随伴艦隊用の配列を別々に読み、実データの長さ・スロット順・隊区分を確かめる。
- 演習は`api_req_practice/battle_result`だけをイベント源として扱う。
- 遠征は`api_req_mission/result`だけをイベント源として扱う。`api_get_ship_exp`は複数要素で返る可能性があるため、min値一つだけを艦娘総量へ加算する仕様にはしない。艦別帰属が解決できない段階では合計値と艦別値を混同しない。
- 戦闘では戦闘APIが複数回あるため、経験値を返さない中間戦闘APIではなく、結果APIに限定する。通常と連合の両方が記録されたケース、結果受信後の重複行、DBポーリングの重複を防ぐ一意キーが必要。
- 74ENの`api_port/port`保存で個別艦・艦隊配列が消去される。従ってportスナップショットのみで艦IDと経験値を対応づける設計は危険。結果APIの`api_ship_id`と経験値配列の意味、または保存済み別APIから編成を復元できるかを対象バージョンのDBで確認する。
- 艦娘経験値上限到達時にAPIの獲得量がゼロ化・クリップされるか、経験値配列が理論獲得量を返すかは、指定コミット内の型定義や表示処理からは確定できなかった。累積経験値差分方式は上限到達艦の加算を捉えられない可能性がある。イベント獲得方式でも「サーバーが返した数値」を「実際に累積へ加算された量」と同一視せず、上限艦と通常艦の実データ対比を必須とする。
- 艦娘別の新方式はイベント獲得量、従来方式は母港時点の累積総経験値差分であり、意味が異なる。別系列・別CSVとして管理する。イベント欠損、母港不在、編成対応不明の値を従来CSVへ混ぜない。

**EO-1で必要な実証データ：** 個人情報・トークン・不要なAPIを除いたローカル合成応答、または利用者が明示的に選んだDBコピーから、通常戦闘、連合、演習、遠征、レベルアップ、上限艦の各ケースを抽出し、フィールド数・配列長・対象艦・累積値差を照合する。実ゲームサーバーへの試験リクエストは送らない。

## 6. セキュリティ・プライバシー評価

- SQLiteは必ず読み取り専用URI/接続モードを使う。読み込み目的でもDB自動作成、EF migration、WAL checkpoint、journal変更、VACUUM、最適化、書き込みトランザクションは禁止。
- 接続先DBをユーザーに選ばせ、開く前にファイル存在とDBヘッダーを検査する。DBパスやAPI本文を外部へ送らず、計測機から外部ネットワーク通信を行わない。
- `Content`のBrotli展開はサイズ上限とエラー扱いを設ける。許可API名と`ApiFileType=Response`だけをクエリし、API本文全体やリクエストパラメータをログへ出さない。
- `api_port/port`は艦隊・艦船情報を除去するが、他API記録には認証や個人情報に接続し得る内容がある。全件を復号・保存・表示せず、許可リストで必要な対象APIだけ処理する。
- 74ENのDBライターと競合するため、読取は短いページングクエリで行い、データベースロック時は限定回数で再試行する。長時間トランザクションを避ける。
- DBは利用者のローカルデータであり、CSVへの書き込みはアプリ自身の既存出力仕様に限定する。74EN DB内の読み取り位置を更新する必要がある場合も、状態はアプリ側設定に保持し、74EN DBに書き込まない。

## 7. 仕様不足・技術リスク

1. 対象74ENバージョンが固定されていない。今回のユーザー環境は履歴上5.4.3ENだが、指定調査SHAはmainのソース時点であり、同一とは未確認。2026-11-28時点の公開リリース一覧には5.4.3安定版、5.4.4 preview1/preview2がある。初期正式対応版を5.4.3に固定し、5.4.4 previewは別検証対象とする案をEO-1で確定する。
2. DBの相対配置はカレントディレクトリ依存で、ユーザーが示す74EN実行フォルダとデータ保存先が異なる可能性がある。自動検出と手動DB選択の優先順位、複数DB候補時の挙動を仕様化する必要がある。
3. 経験値配列の艦スロット対応、MVP/旗艦補正、イベント後の累積差、上限処理は型だけで確定できない。新方式CSVの正確性の主要リスク。
4. SQLite schemaは更新され得る。対応する`ApiFiles`列と型、Versionを起動時に読み取り検査し、未知バージョン・欠落列は明確なエラーにする。
5. SQLite providerを追加する場合のライセンス・脆弱性・single-file配布サイズ、Brotli展開のサイズ制限、ファイルロック挙動をEO-1で確認する。
6. 現在の作業環境で真の独立Gitブランチを作成できない。ブランチ作成前の実装を開始すると、既存CDP開発状態を保全する要件を満たせない。

## 8. EO-1以降の推奨計画

| Phase | 範囲 | 主な受入条件 |
|---|---|---|
| EO-1: Git保全・SQLite読取基盤 | M14.1を含むGit作業コピーを復元/確認し、CDPルートからEO専用ブランチを作成。SQLite接続方式・DB選択・readonly schema検査を実装。 | 元ブランチ/HEAD/差分を記録。テストDBをReadOnlyで開き、アプリ動作中でも読み取り可能。終了後DB/WAL/SHMのhash・サイズ・mtimeが変化しない。未知schema、ロック、壊れたDBを安全に拒否。外部ネットワーク通信なし。 |
| EO-2: ApiFilesパーサー | Brotli BLOB展開、UTF-8 JSON、Response/API allowlist、時刻・Id順ページング、再起動後の重複防止。 | 圧縮破損・巨大BLOB・空値・不正JSON・unknown Versionをテスト。対象行以外を復号しない。DBは無改変。 |
| EO-3: 提督経験値 | `api_port/port`の`api_basic.api_experience`差分と既存戦果Coreを接続。 | 累積・重複・逆行・初回基準・日付境界を試験し、既存戦果時速/CSV回帰を維持。 |
| EO-4: 艦娘獲得経験値 | 通常戦闘、連合、演習、遠征結果イベントを取り込む。新CSVを従来累積差分CSVから分離する。 | スロットと艦IDの結合を実レスポンス/累積差で証明。MVP等二重補正なし。上限艦、欠損イベント、重複読み込み時の定義を明記。 |
| EO-5: UI・JSON互換 | DB選択/接続状態UI、既存JSON監視の比較運用と切替。CDP/ブラウザ機能は正式版UI・配布から除外。 | JSONとDBを同時に読んでも二重計上しない。設定・旧CSV互換。CDP機能が配布構成に入らないことを確認。 |
| EO-6: 統合検証・配布候補 | 合成SQLite、ロック/終了/再起動、読み取り専用、CSV互換、配布・依存ライセンス確認。 | Releaseビルド、自動テスト、ローカル合成DB試験、DB無改変証跡、既存機能回帰を実施。実機試験が未実施なら明確に未完了と報告。 |

上記は仕様書のPhase名が実装に必要な責務へ分解される場合の提案であり、ユーザー仕様の正式なEO-1～EO-6の順序を置き換えるものではない。Phase境界は添付仕様の明記を優先する。

## 9. EO-0の試験・実施結果

- 74EN公開ソース：指定コミットで読み取り確認済み。
- GitHub SenkaSpeedブランチ：API参照で確認済み。
- 既存C#成果物：ファイル構成・前Phase報告書を確認し、M14.1までの必要ファイルをGit管理下へ取り込み済み。
- 74EN実機SQLite：未検証。今回アクセス可能な対象DBが提供されていない。
- 合成SQLite/Brotli試験：未実施。EO-0では製品実装・検証コードを追加していない。
- Releaseビルド・自動テスト：未実施。EO-0は調査Phase。M14.1以前の結果は過去報告の引用。
- CSV互換・経験値集計回帰：未実施。
- 独立ブランチ：`develop-csharp-eo`を作成しpush済み。親は既存`develop-csharp`の先端。取り込みコミットは`21f47201978b51133d1fcdbc2374b87b78cf1ac0`。

## 10. 結論と次の前提

指定コミットで、SQLite`ApiFiles`、Brotli BLOB、Response行の識別、portの`api_basic`保持、戦闘・連合・演習・遠征の経験値候補フィールドを確認した。M14.1成果を含むEO専用ブランチも履歴を維持して作成・公開した。SQLite読み取り方式は技術的に成立する見込みがある。一方、イベント獲得経験値を艦娘別・実加算量として正しく扱う核心部分は、公式型定義だけでは確定しない。特に上限、配列と艦IDの対応、補正済みか否かを対象バージョンの保存データで照合する必要がある。

EO-1着手前の必須作業は、(1)対象とする74EN版を固定する、(2)個人情報を避けた明示選択DBコピーでschemaと実際の応答形をread-only確認する、(3)SQLite provider候補の保守状況・ライセンス・配布条件を確認する、の3点である。Gitの分岐起点は`develop-csharp-eo`として保全済みである。これらを確認した後、SQLite基盤から段階的に実装できる。

## 参考資料

- [ElectronicObserverEN / ElectronicObserver](https://github.com/ElectronicObserverEN/ElectronicObserver)
- [調査対象コミット 8657bea3](https://github.com/ElectronicObserverEN/ElectronicObserver/commit/8657bea3c6db3efaf37de45a6aba91ebd5c4e709)
- [SenkaSpeedMeasuringDevice remote branches](https://github.com/rinopap/SenkaSpeedMeasuringDevice/branches)
- [74EN 5.4.3 release](https://github.com/ElectronicObserverEN/ElectronicObserver/releases/tag/5.4.3)
