# M-Phase 5 CDP技術検証

実施日: 2026-11-26

## ソース調査

- DevTools ProtocolのNetwork仕様は、`Network.responseReceived`、`Network.loadingFinished`、`Network.getResponseBody`で受信済みレスポンスを観測できる構成を定義している。[公式Network仕様](https://chromedevtools.github.io/devtools-protocol/tot/Network/)
- ElectronicObserverENの公開`main`ソースでは、`Browser/WebView2Browser/WebView2ViewModel.cs`が`CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`を使いWebView2を初期化することを確認した。取得した現在のソース上では`--remote-debugging-port`は追加されていない。これは公開ソースの確認であり、利用者の現行配布版や実機のCDP接続可否を確認した結果ではない。
- MicrosoftのWebView2デバッグ資料には`--remote-debugging-port`を指定する開発時手順がある。[Microsoft Learn](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/debug-visual-studio-code) この設定は本アプリから自動変更していない。74EN実機での検証には利用者の明示同意が必要。

## ローカルEdge検証

- WindowsにあるEdge 158.0.4370.45を使用し、一時ユーザーデータフォルダと`--remote-debugging-address=127.0.0.1`で起動した。通常のEdgeプロファイル、ユーザー設定、レジストリ、74ENは変更していない。
- 自前のloopback HTTPテストページからローカルの`/kcsapi/api_port/port`相当パスへ1回だけリクエストし、プローブを接続して対象本文を取得した。
- 取得本文は88 bytes。CDPプローブが計算したSHA-256とローカルテストサーバーの期待値が一致した: `419A55CD47C2F8D630C7AFD9C6E0E517672DD595AC44CBD6242346675F0D10AD`。
- プローブは`Network.enable`、`Network.getResponseBody`、`Network.disable`だけを送信する。`Network.responseReceived`から完全一致したAPI host/pathを選び、`Network.loadingFinished`後に本文を取得する。本文は表示・保存せず、サイズとハッシュだけを出力する。
- loopbackテストはHTTPのみ。TLS証明書を無効化せずに使える開発証明書がこの環境に見つからず、HTTPS試験は未実施。証明書を信頼ストアへ追加したり検証を無効化したりしていない。

## 成果物と検証

- `tools/CdpFeasibilityProbe/`: loopbackのCDP listenerに接続する小規模プローブ。
- `tools/LocalCdpTestServer/`: 合成ページと合成API本文だけを返すHTTPサーバー。
- 両ツールのReleaseビルド成功、警告0・エラー0。Edgeを使ったローカルHTTP試験で本文ハッシュ一致。

## 判定と制約

- 通常EdgeでのCDPレスポンス観測は、この環境のローカルHTTP試験で成立した。
- WebView2/74ENの外部接続互換性、HTTPS本文取得、iframe、複数タブ、ブラウザ再起動、実ゲーム通信は未確認。M5の全検証項目を完了したとは扱わない。
- `--remote-debugging-port`は強いローカル制御面を開くため、製品から自動設定しない。利用者が専用ゲーム用ブラウザをloopback限定で設定する場合だけを想定する。外部インターフェースへの公開を許さない。
- API本文はローカルHTTPの合成レスポンスのみを取得。Cookieや認証情報は取得せず、ゲームAPIへの通信を生成していない。

## 次工程

通常ブラウザ向けの読み取り専用`CdpCaptureSource`を実装し、74ENでは既存JSON監視を併用する。74ENのCDPは実機で成立するまで利用可能と表示せず、自動的に設定を変更しない。HTTPS/74ENの手動検証は後続の統合前に行う。

