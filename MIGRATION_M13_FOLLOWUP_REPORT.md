# M-Phase 13 Follow-up: 74EN CDP Capture Gap

作業日：2026年11月27日  
対象：M-Phase 13 実機確認後の修正  
判定：**CDPのWorker経由リクエストを扱う修正を実装し、自動テストとReleaseビルドに合格。修正版の74EN実機確認は利用者による再試験待ち。M13は未完了のまま。**

## 1. 利用者実機試験で確認した事実

- 74ENで所属サーバー「リンガ泊地」を選択。
- 計測機の状態表示は「CDP接続中」。
- CDP監視中、74EN側の対象JSONファイルの更新日時は変わるが、計測機のCSVに新しい行が追加されない。
- 取得方式を74EN JSON監視にすると記録される。
- 利用者が74ENの開発者ツールで確認した`api_port/port`は、選択したAPIホストとパスに一致し、HTTP 200 OK。

これらは、74ENが対象データを受け取っている一方、CDP取得から計測機への受け渡しで記録されていないことを示す。人が確認した対象API URLとステータスは選択設定に合致するため、ホスト/パス不一致は現状の主因候補から外す。ただし、計測機が受け取ったCDPイベント内の値や本文は調べていない。更新日時や通信本文は取得・保存していない。

## 2. 調査結果と修正

CDP仕様では、Workerから取得された通信で`Network.requestWillBeSent`および`Network.responseReceived`の`loaderId`が空文字になる場合がある。[CDP Network仕様](https://chromedevtools.github.io/devtools-protocol/tot/Network/)

従来実装は空の`loaderId`を要求登録時に拒否し、さらにページフレームの現行`loaderId`との一致を常に要求していた。これはWorker経由の正規API応答を捕捉できない実装上の欠陥である。ただし、今回の実機イベントの`loaderId`そのものは取得していないため、利用者環境での欠落原因がこのケースだったと断定はしない。

修正では、空文字の`loaderId`をCDP仕様上の値として許可した。安全境界は維持し、リクエストID、既知のフレームとセッション、現行ページ世代、選択サーバーのAPIホストとパス、成功応答、リクエストと応答のフレーム・`loaderId`一致を引き続き確認する。通信の停止、変更、再送、Fetchインターセプトは追加していない。利用者が今回確認したURL/200応答とCSV未生成を踏まえ、この修正単独で問題を解消したとはまだ判断しない。

## 3. 変更ファイル

- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCommandPolicy.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCaptureSyntheticTests.cs`

合成CDP試験に、空の`loaderId`を持つWorker経由リクエスト・応答の受け入れ確認を追加した。
また、応答本文が「待機中」「要求検出」「応答照合済み」「本文取得済み」「経験値解析不可」のどこまで進んだかを、アドレスや本文を含めず状態ツールチップに表示する診断を追加した。CDPイベントやAPI本文をログ・ファイルへ保存しない。

## 4. 検証結果

- .NET SDK 10.0.404 を用いたRelease自動テスト：**73 passed / 0 failed / 0 skipped**。
- CaptureライブラリReleaseビルド：成功、警告0、エラー0。
- WPFアプリReleaseビルド：成功、警告0、エラー0。
- ローカル合成CDP試験でWorker経由の空`loaderId`捕捉と、段階状態表示を確認。
- 修正版を使った74EN/WebView2実機再試験：**未実施**。
- 外部ゲームサーバーへの試験リクエスト：送信していない。

## 5. 残存事項

利用者は更新されたRelease版で、同じポート・リンガ設定のまま74EN CDPを再確認する。状態テキストにカーソルを置くと、CDP応答取得の段階が詳細表示される。期待結果は、母港などで対象データが発生した後に経験値表示とCSV行が更新されること。実機再試験に合格するまで、この修正による74EN対応完了とは扱わない。

Chrome、Vivaldi、WebView2待受PIDの独立検証など、M13報告書に記載した残りの確認項目も引き続き未完了であり、M14には着手していない。
