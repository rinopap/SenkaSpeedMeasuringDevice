# CDP方式の実現性調査

調査日: 2026-11-26

## 現時点の結論

CDP Networkドメインは受信済み応答の観測に利用できる。ただし、ブラウザのデバッグ接続口を有効にする必要があり、通常のChrome/Edge既存プロフィールと74EN配布版の双方で、利用者の明示的な設定なしに接続できる状態ではない。現時点では製品機能として成立したとは判定しない。

Chrome 136以降は既定のユーザーデータディレクトリに対する`--remote-debugging-port`を無視する。Chromeの公式案内は別のユーザーデータディレクトリを使う方法を示す。これは既存の普段使いプロフィールをそのまま観測する方法ではなく、ログイン状態・拡張機能・Cookieを共有しない分離プロフィールによる検証となる。

WebView2は`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`環境変数またはホストアプリのWebView2環境オプション等により、リモートデバッグポートを設定する仕組みがある。公開中の74ENソースではWebView2環境オプションの`AdditionalBrowserArguments`を組み立てていることを確認した。一方、配布版で外部CDPクライアントから対象WebViewへ到達できるか、74ENの既存引数との共存やポート設定を利用者がどのように同意・解除できるかは未確認である。

## 観測に使うCDPメソッド

実装候補は次の読み取り系に限定する。

1. `Network.enable`
2. `Network.responseReceived`イベントから対象レスポンスのrequestIdとURLを照合
3. `Network.loadingFinished`後に`Network.getResponseBody`
4. base64フラグを確認し、メモリ内で本文を復号して経験値解析へ渡す

対象は許可したゲームホスト上の`/kcsapi/api_port/port`のみとし、処理直後に本文バッファを解放する。`Fetch`ドメイン、Cookie/リクエスト本文取得メソッド、リプレイ、ブロック、オーバーライド系メソッドは呼び出さない。通信本文、ヘッダー、URL全般、Cookie等をログへ出さない。既定では接続先一覧にURLを表示せず、対象タブ選択後にゲームホストとタブ名だけを確認できる設計にする。

`getResponseBody`は応答バッファの消去前に呼ぶ必要があり、長時間負荷・大きな応答・複数の対象タブ・iframe/OOPIFで本文取得のタイミングとターゲット管理を検証する。取得失敗はそのレスポンスを欠測として扱い、ゲーム通信に対する再送・介入はしない。

## 検証段階

### 1. 通常ブラウザの分離テスト環境

- 新規の空ユーザーデータディレクトリを使う試験用ChromeまたはEdgeを別プロセスで起動する。
- デバッグポートは`127.0.0.1`に限定し、LANへ公開しない。
- ローカル模擬APIで、対象URLの成功応答と対象外応答を作る。
- 試験クライアントでCDPのNetworkイベントから対象API本文だけを受け取れることを確認する。
- ブラウザが送った要求数とローカルサーバーが受けた要求数が一致することを確認し、アプリ側が追加要求を作らないことを確認する。
- 計測アプリの正常終了・強制終了中もブラウザからテストサーバーへ要求できることを確認する。

### 2. 74EN配布版との接続可否

- 実ゲームではなく、アカウント情報のない試験環境で始める。
- 74ENの配布版・WebView2 Runtimeバージョン・WebView2の起動引数・デバッグポートの有無を記録する。
- 利用者が明示同意した設定変更のみで、74EN内蔵WebView2の対象タブを列挙してCDP接続できるか確認する。
- 74ENの設定変更を戻して再起動し、ポートが閉じたことを確認する。
- 74EN終了後もCDPクライアントが残存ポートを開放すること、計測アプリ終了が74ENの通信や終了に影響しないことを確認する。

現時点では段階1・2とも未実施。過去のプロキシ試験・ローカルHTTP確認器はCDPの検証には使用していない。

## 使用する一次資料

- [Chrome DevTools Protocol: Network](https://chromedevtools.github.io/devtools-protocol/tot/Network/)
- [Chrome 136のリモートデバッグスイッチ変更](https://developer.chrome.com/blog/remote-debugging-port)
- [Chromeのリモートデバッグを使う](https://developer.chrome.com/docs/devtools/remote-debugging/local-server/)
- [Microsoft WebView2でアプリをデバッグする](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/debug-visual-studio-code)
- [74EN WebView2ViewModel.cs](https://github.com/ElectronicObserverEN/ElectronicObserver/blob/main/Browser/WebView2Browser/WebView2ViewModel.cs)

