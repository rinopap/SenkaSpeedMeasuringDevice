# M13 追補：ブラウザー認証互換性とCDP方式の調査

調査日：2026-11-28  
対象：M13追加技術調査項目 1～10  
調査対象コード：M14時点のC#版  
判定：**CDPの取得処理は認証ページをデータ取得対象から外す設計だが、ChromeをCDP付きで起動すること自体がページ側に自動制御のシグナルを公開する。Google認証との互換性はCDPなしで先にログインする運用で確認されたが、一般利用者向けUIは未整備。Chrome 144以降の利用者承認型CDPは公式資料上Chrome DevTools MCP向けの案内に限られ、既存C#クライアントでの利用は未検証。**

## 1. 要約

Google認証での警告について、次の事実を確認した。

- 現行のChrome/Edge起動補助は `--remote-debugging-port=0` でCDPを有効にする。
- Chromeでは、このポート指定により `navigator.webdriver` が `true` になると公開資料に記載されている。ページ内JavaScriptから読める値であり、サイト側が自動制御状態を判別する手掛かりになり得る。
- Googleは、自動化ソフトに制御されているブラウザーやアプリ内ブラウザーからのログインを拒否する場合があると案内している。
- 開発者による実サービスへの再ログイン試験はしていない。利用者からは、CDP付きChrome/EdgeでGoogle認証の警告が出た一方、CDPなしのEdgeで同じ専用プロファイルに認証後、CDP付きで計測できたとの報告がある。

GoogleはEdgeを対応ブラウザーとして列挙する一方、自動化ソフトに制御されるブラウザー等からのログインを拒否する場合があると案内している。[Googleの対応ブラウザーとログイン制限](https://support.google.com/accounts/answer/7675428?hl=ja)

このため、現象はCDP起動状態と強く関連しているが、Googleが `navigator.webdriver` を直接検査したことまでは確認できていない。認証をCDPなしで済ませる手順は実用上有効だが、ゲームプレイ中のCDP状態がサイトから識別されないことを意味しない。

## 2. 現行起動引数の全数調査

`src/SenkaSpeedMeasuringDevice.App/BrowserSupport.cs` の `BrowserLaunchHelper.LaunchAsync()` が以下を `ProcessStartInfo.ArgumentList` に追加する。

| 引数 | 現行値・作用 | 認証・プライバシーへの影響 |
|---|---|---|
| `--remote-debugging-address=127.0.0.1` | CDP待受アドレスをloopbackに限定する。 | LANから直接接続できる状態を避ける。ただし同じPC上の別プロセスを認証・排除する仕組みではない。 |
| `--remote-debugging-port=0` | OSに空きポートを割り当てさせ、CDP待受を開始する。ChromeはポートとWebSocket情報を `DevToolsActivePort` に書く。 | **Chromeでは `navigator.webdriver` がtrueになる条件に該当する。** Google認証警告の有力な説明候補。実際のGoogle判定根拠は未確認。 |
| `--user-data-dir=<LocalAppData>\SenkaSpeedMeasuringDevice\BrowserProfiles\<browserId>` | 日常利用プロファイルと別の専用プロファイルで起動する。プロファイルは永続化される。 | 日常プロファイルのCookie等を共有しない一方、この専用プロファイルにはブラウザー管理のログイン状態が残る。計測機はCookieやパスワードを読み取らない。 |
| `--no-first-run` | 初回起動の案内を省略する。 | Webページの認証を直接変更する目的ではない。実サービスとの比較試験はしていない。 |
| `--new-window` | 新しいウィンドウで開くよう要求する。 | 認証方式を変える目的ではない。プロセス・プロファイルの既存状態によって挙動が異なる可能性は残る。 |
| `https://play.games.dmm.com/game/kancolle` | 固定ゲームページを起動する。利用者入力のURLは使わない。 | 初回アクセスから認証ページへ遷移する場合でも、CDP起動引数はすでに有効。 |

プロセス起動設定は `UseShellExecute=false` とブラウザー実行ファイルのディレクトリをWorkingDirectoryに指定する。これらはブラウザー引数ではない。

## 3. 通常ブラウザーとCDP起動ブラウザーの差

通常起動と比較して、CDP起動では少なくとも以下が変わる。

- CDPのWebSocket待受が新規に開き、同じ端末上のプログラムが接続を試みられる。
- `--user-data-dir` により、日常プロファイルとは異なるCookie・ログイン状態・拡張設定を持つ専用プロファイルになる。
- ChromeではCDP動的ポート `0` により `navigator.webdriver` がtrueになると記載されている。[MDN WebDriver属性資料](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/webdriver)
- 通常の `--remote-debugging-port` 起動は、Chrome 144の承認型フローと異なり、接続前に利用者がChrome UIでクライアントごとに許可するフローではない。

Chromeの `navigator.webdriver` はページ側から参照できるため、ゲームページが検査する可能性は技術的にある。ただし、その値が自動的にHTTPヘッダーへ付加されるわけではない。ページのコードが値を読み取り、サーバーに送信するか、運営が検査しているかは調査していない。

EdgeはChromiumベースでCDPの互換性を提供するが、今回の資料調査ではEdgeの `--remote-debugging-port=0` が同じ `navigator.webdriver` 値を示すか独立確認していない。Chromeの記載をEdgeの確認済み事実として扱わない。

## 4. Google/DMM認証と通信観測処理の分離

### 確認できたコード上の分離

- CDPソースはトップページが固定URL `https://play.games.dmm.com/game/kancolle` の場合だけ、ゲームページとして許可する。
- API本文の取得は、選択サーバーのHTTPSホストと `/kcsapi/api_port/port` が一致し、応答・フレーム・loaderを照合した場合だけ `Network.getResponseBody` を呼ぶ。
- トップページが許可ページから外れると、ページ世代と保留中要求を無効化し、`Target.setAutoAttach(autoAttach=false)` と `Network.disable` を送る。
- `StopAsync()` はCDP WebSocketを閉じる。終了処理でも `Network.disable` を試みる。
- Google/DMM認証本文を抽出・保存・転送する処理や、認証操作を自動化する処理は確認されなかった。

### 分離できていない点

CDPソースが認証ページの本文を取らないことと、ブラウザーがCDP有効で起動していることは別問題である。認証ページへの通信観測を停止しても、ブラウザープロセスの `--remote-debugging-port=0` は残り、Chromeではページ側に自動制御状態が見える可能性が残る。したがって、`Network.disable` だけではGoogle認証互換性を保証できない。

認証画面と観測処理の運用上の分離には、同じ専用プロファイルを **CDPなしで開いて人が認証し、ブラウザーを閉じた後、CDP付きで再起動する**方法が成立する。利用者の実機報告ではこの順序で認証状態が引き継がれ、計測できた。ただし認証の有効期限切れ時には同じ手順が再度必要であり、現行UIにログイン準備モードはない。

## 5. Chrome 144以降の利用者承認型CDP

Chrome公式資料では、Chrome 144以降、利用者が `chrome://inspect/#remote-debugging` でリモートデバッグを有効化し、接続要求を許可する方法が説明されている。承認中はブラウザーに「Chrome is being controlled by automated test software」等の表示が出る。公式の自動接続手順はChrome DevTools MCPサーバーを接続主体としている。[Chrome公式説明](https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session)、[Chrome DevTools MCP接続手順](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/advanced-usage.md#automatically-connecting-to-a-running-chrome-instance)

今回確認した公式資料では、任意の第三者C#クライアントが同じ承認ハンドシェイクを利用する公開仕様は確認できなかった。Chrome DevTools MCPを追加するとNodeランタイム等が必要になり、追加ランタイムを必須としない本仕様には適合しない。また、承認後の接続が既存CDPクライアントのHTTP `/json/list` と同じ形で使えるか、ゲームタブに限定できるか、認証ページでの `navigator.webdriver` 状態がどうなるかは未検証。

CDP自体について、公式プロトコル資料は広い権限を持つデバッグ/自動化ターゲット向けのプロトコルと説明し、第三者の直接利用はサポートAPIではなく互換性を保証しないとしている。[Chrome DevTools Protocol Viewer](https://chromedevtools.github.io/devtools-protocol/)

**判定：Chrome 144承認型CDPは有望な調査候補だが、既存C#クライアントへの置き換えとして成立したとは判定しない。**

## 6. C#クライアントの接続可否

- 現行方式：ブラウザーをコマンドライン引数でCDP有効起動し、C#がloopbackの `/json/list` と対象ページのWebSocketに接続する。M13のEdge隔離試験、利用者によるChrome/Edge計測報告がある。
- 一般性：MicrosoftはEdgeのCDPに対し、実行時に `--remote-debugging-port` を与え、`/json/list` から選択したターゲットのWebSocketへ接続する手順を公式に説明している。[Microsoft Edge DevTools Protocol](https://learn.microsoft.com/en-us/microsoft-edge/devtools/protocol/)
- Chrome 144承認型：現行C#実装での接続は未確認。承認フローの存在だけでは、C#クライアントの接続可否・権限範囲・終了時の失効を保証しない。
- 既存C#接続の安全範囲：本アプリの `CdpCommandPolicy` は `Network.enable/disable`、対象要求に対する `Network.getResponseBody`、フレーム照合用の限定コマンドを許可し、それ以外の `Fetch` や `Runtime.evaluate` 等を拒否する。これは本アプリのクライアント制約であり、他のローカルプロセスからCDPポートへ直接接続することを制限しない。

## 7. ブラウザー別の対応状況

| ブラウザー | 公開仕様・実機情報 | 今回の認証評価 | 正式対応判断 |
|---|---|---|---|
| Chrome | 現行CDP起動と利用者による実ゲーム計測を確認。Chrome 144承認フローは公式資料あり。ただし公式の例はChrome DevTools MCP。 | CDP付きでGoogle認証警告が出たとの利用者報告。CDPなしでログイン後、同一専用プロファイルのCDP計測が通ったとの報告。`navigator.webdriver` のChrome条件と整合するが、原因確定ではない。 | 現行の初回認証導線には制限あり。CDPなし認証を案内するUIを追加するまで、初回認証対応済みとは表示しない。承認型方式は未検証。 |
| Microsoft Edge | Microsoft公式にCDPの起動引数、HTTP discovery、WebSocket接続が文書化されている。利用者による計測確認あり。 | CDP付きのGoogle警告とCDPなし認証成功の利用者報告。Edge内部の公開自動化属性は独立確認していない。 | 現行のCDP捕捉は試験済みだが、認証は事前ログイン手順に依存。 |
| Vivaldi | 組み込みDevToolsの資料は確認。外部CDPクライアントによる対象ページ/OOPIF/認証互換の公式資料・実機結果なし。 | 未検証。 | 保留。 |
| Brave | Chromium系だが、今回の公式資料でChrome 144型承認接続の同等サポートを確認できなかった。 | 未検証。 | 保留。 |
| Opera | 公式ヘルプにDevTools/リモートデバッグの一般説明はあるが、本アプリのCDP構成・認証制約と互換であることは確認できない。[Opera Advanced Help](https://help.opera.com/en/latest/advanced/) | 未検証。 | 保留。 |
| 74EN / WebView2 | 74ENのJSON監視はアプリ組み込みの監視経路。外部CDPはWebView2環境変数等でデバッグポートを追加して起動する別方式。MicrosoftはWebView2のCDPポートを外部デバッガーが利用できる点を説明している。[WebView2 CDP設定](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/webview2-idl) | 利用者はJSON監視とCDP計測の成功を報告。Google認証をWebView2内で行う場合は、埋め込みWebViewの認証制限が別途関係する可能性がある。Googleは埋め込みWebViewでのOAuthをブロックする方針を公開している。[Google OAuth embedded webviews](https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/) | JSON監視と外部CDPを同じ方式として扱わない。WebView2内でのGoogle認証可否は保留。 |

## 8. 認証時の安全な停止・CDP切断後

### 現行実装

- 認証URL等の非許可ページに移動すると、現行ポリシーはページを無効にし、API本文読み取り許可を失効させる。ネットワーク観測停止コマンドを発行する。
- アプリを停止するとWebSocketを閉じ、観測処理は終了する。ゲーム通信をプロキシ中継していないため、クライアント切断がゲーム通信経路を遮断する設計ではない。
- **CDPクライアントを切断しても、ブラウザー起動時のデバッグサーバーは停止しない。** `--remote-debugging-port=0` による待受とブラウザーの起動状態は、ブラウザーを終了するまで残る。再認証をCDPなしで行うには、CDP付きブラウザーを閉じ、同じ専用プロファイルをCDPなしで起動し直す必要がある。
- Chrome 144の承認型接続で、C#切断が利用者承認の失効・デバッグサーバー停止になるかは未確認。Chrome UI側で無効化する手順の製品試験も未実施。

### 74ENとの差

74ENのJSON監視は、既存アプリが扱うAPIログファイルを外部から読み取る方式で、Chrome/EdgeプロセスのCDPリモートデバッグサーバーを計測機が直接開く方式とは異なる。74ENのWebView2に外部CDPを有効化する場合は、ホストアプリ起動前にWebView2追加引数等を与えるため、CDP露出はWebView2ホスト起動設定に依存する。どちらも認証情報を取得しない実装は可能だが、規約上許可されるとの公式保証は確認していない。

## 9. 合成試験・ビルド

### 実施

- `CdpCommandPolicyTests` にGoogleログインURL、DMMログインURL、一般外部URLへの遷移ケースを追加。非許可ページへ遷移した後、API要求登録、`Network.enable`、`Network.getResponseBody` を許可しないことを確認する。
- Releaseの対象試験：**3 passed / 0 failed**。
- Releaseの全自動テスト：**83 passed / 2 failed**。
- 失敗1：`CdpCaptureSyntheticTests` の既存 `HttpListener` fixture がこの実行環境で `HttpListenerException: ハンドルが無効です` となり起動できない。
- 失敗2：既存 `CdpListenerProcessPolicyTests` のloopback待受プロセス照合がこの実行環境のプロセス/ソケット制限により期待値を満たさない。
- WPFアプリReleaseビルド：**未完了**。現在の制限環境ではWindows SDK確認時のレジストリアクセスが拒否され、`MSB4184 Requested registry access is not allowed` で停止した。アプリコードのコンパイルエラーとは判定しない。

### 未実施

- GoogleまたはDMMへの実ログイン、認証情報入力、認証通信の観測は行っていない。
- Chrome 144承認型CDPの実機ダイアログ、C#接続、接続拒否/解除を試験していない。
- 各ブラウザーの認証ページで属性値を読んだり、ページスクリプトを注入したりしていない。
- Vivaldi、Brave、Opera、74EN内WebView2の認証互換を試験していない。

## 10. 推奨方式と次の実装判断

### 運営公開告知と規約判断の限界

艦これ運営の公開告知には、マクロ・加速・BOT等の外部ツール使用を利用規約違反として扱う記載がある。[DMM GAMES上の運営告知](https://www.dmm.com/netgame/community/-/topic/detail/=/tid=922/) 今回確認できた資料からは、手動プレイ中にゲーム応答を読み取るだけのCDP計測ツールが許可される、または一律に禁止されるという明示判断は確認できなかった。したがって、読み取り専用・通信非改変という設計事実は規約上の許可を意味しない。正式配布時に規約上の許可を保証する表現は避ける。

### 直近の認証互換性

現行実装を維持する場合は、専用プロファイルをCDPなしで起動して利用者が認証し、ブラウザーを閉じてからCDP付きで再起動する段階方式が確認済みの運用となる。一般利用者にはM14 UIへ「ログイン準備（CDPなし）」と「監視開始（同じ専用プロファイルをCDP付きで起動）」を追加し、認証が失効した場合に戻れるようにする。認証情報の自動入力や移送は行わない。

この段階方式はGoogleログインをCDPなしで行う解決策であり、**ゲームページをCDP付きで開くことが運営側から識別不能になる方法ではない**。識別状態を隠す変更は行わない。

### 中長期の方式比較

| 方式 | 認証時の環境 | 同意・利用負担 | C#本体/追加依存 | 推奨判断 |
|---|---|---|---|---|
| 現行の専用プロファイルをCDP付きで起動 | 初回Google認証が拒否され得る。 | 初回は簡単だが認証で詰まる可能性。 | 追加依存なし。 | 認証互換性の既定導線としては不十分。 |
| CDPなしで認証後、同じプロファイルをCDP起動 | Google認証時にデバッグポートを開かない。実機報告あり。ゲーム観測中はCDP状態のまま。 | ブラウザーを一度閉じて再起動する操作が必要。UIで補助可能。 | C#本体のみ。 | **短期推奨。** 認証情報を扱わず現行CDPを再利用できる。運営側の検知・規約リスクは解決しない。 |
| Chrome 144承認型CDPを通常起動Chromeへ接続 | 通常ブラウザーとして起動し、利用者が接続ごとに承認する。承認中に自動制御表示がある。認証後の属性値は未確認。 | 明示同意が最も分かりやすい。起動済みプロファイルを対象にするため、対象タブ限定とアカウント分離を厳密に設計する必要。 | 現在の公式案内はChrome DevTools MCP向け。C#直接接続は未検証。 | **次の技術検証候補。成立確認までは採用しない。** 認証互換とC#接続の両方を合成ローカル環境で確認する。 |
| 74EN JSON監視 | WebView2/CDPなしの既存ログ監視が可能。WebView内Google OAuthは別制約。 | 74ENを既に使う利用者には低負担。 | 既存74ENが必要。 | 現在の代替手段。規約上の安全性を保証するものではない。 |

### 判定

- 初回Google認証を含めた一般ユーザー向け接続として、現行M14の単一「CDP付きで起動」導線は未完成とする。
- 直近の改善は専用プロファイルでのCDPなし認証モードと、CDP計測モードの二段階UIを追加すること。
- Chrome 144承認型CDPは、Chrome DevTools MCPを製品必須にせず、C#だけで接続できるかの調査を続ける。
- Chrome/Edgeゲームセッションの規約上の受容性について公式な確認が得られない場合、技術的に取得可能なことを許可の証拠として扱わない。
- Vivaldi、Brave、Opera、およびWebView2内認証は、認証互換性と同意付き接続を検証するまで正式対応を保留する。

## 11. 確認事実・推測・未検証の区別

| 区分 | 内容 |
|---|---|
| コードで確認 | CDP起動引数、ゲームURL/API許可条件、非許可ページ時のNetwork停止、response body取得条件、認証データの保存処理がないこと。 |
| 公式資料で確認 | Googleが自動制御ブラウザー/埋め込みブラウザーからのログインを制限し得ること。Chrome port 0時のWebDriver属性。Chrome 144の承認型接続がChrome DevTools MCP向けに案内されていること。EdgeのCDP標準接続。 |
| 利用者報告 | CDP付きChrome/EdgeでGoogle認証警告、CDPなしEdgeで認証後にCDP計測成功。 |
| 有力な推測 | ChromeのCDPポート0による自動制御シグナルがGoogle警告の原因または一因になった可能性。 |
| 未検証 | Googleの拒否判定の内部条件、DMM側が `navigator.webdriver` を調べるか、CDP付きゲームプレイの規約評価、Edgeの属性値、Chrome 144承認型のC#直接接続。 |

作業コピーに`.git`メタデータが見つからず、ブランチ/作業ツリー状態は確認できていない。アプリ本体は変更していない。変更は認証ページ遷移のポリシー回帰テストと本報告書に限る。
