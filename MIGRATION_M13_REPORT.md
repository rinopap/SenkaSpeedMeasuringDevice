# M-Phase 13 実施報告：安全なブラウザ接続方式の技術検証

作業日：2026年11月27日  
対象：M-Phase 13  
判定：**技術調査とEdge隔離接続試験を実施。Chrome/Vivaldi/WebView2の実機確認および待受範囲の独立確認が残るため、M13受入は未完了。M14には着手していない。**

## 1. 前Phaseの移行判断

確認資料：

- `docs/NEXT_DEVELOPMENT_SPEC.md`
- `MIGRATION_M12_REPORT.md`
- `MIGRATION_M12_1_REPORT.md`
- `PROJECT_HISTORY_AND_TEST_SUMMARY.md`

M12.1報告は、実装と合成試験を完了し、Releaseビルドと73件のテストに合格した一方、通常ブラウザ・実ゲーム・74EN/WebView2での回帰確認を保留としている。M12.1仕様は、実機試験ができない場合に未完了を明示して次工程へ進むかを開発者が判断する方針であり、今回の依頼で報告の精査とM13への移行指示を受けたため、M13の技術調査に着手した。

この判断はM12.1の実機受入完了を意味しない。M12.1の実機保留は継続し、M13のブラウザ実機試験で確認できた範囲とも区別する。

作業コピーに`.git`メタデータがないため、Gitブランチと作業ツリー状態は確認できず、GitHubへのコミットも行っていない。アプリのソースコードはM13調査のために変更していない。

## 2. 調査した公開仕様

### Chrome

- Chrome 136以降、既定のChromeデータディレクトリに対する`--remote-debugging-port`および`--remote-debugging-pipe`は無効化され、非標準の`--user-data-dir`が必要と公式に説明されている。
- Chrome 144から、`chrome://inspect/#remote-debugging`で利用者が機能を有効にし、接続要求ごとに許可・拒否を選ぶ承認フローがChrome DevTools MCP向けに提供されている。公式資料が示す接続主体はChrome DevTools MCPサーバーであり、既存の任意C# CDPクライアントが同じ承認経路を直接利用できるとは確認できない。
- Chrome DevTools MCPの公開設定資料は`--autoConnect`、`DevToolsActivePort`およびCDP WebSocketによる接続を説明している。ただし、MCPサーバーを依存物として必須化することは本製品仕様に合わないため、製品方式としては採用していない。
- したがって、Chrome承認型CDPは**C#独立クライアントでの直接利用未確認**と判断した。承認画面があるというだけでC#接続が成立する、安全になる、または同一端末の第三者アクセスを防げるとは扱わない。

### Edge

- 現行アプリが使うCDP HTTP/WebSocket経路は、専用の`CdpCaptureSource`からloopbackに限定され、WebSocketのscheme/host/port/path/targetIdを検査する。
- ただし、利用者が指定したポートの待受プロセス、実行ファイル、署名はアプリで検証していない。
- Edgeの通常CDP用コマンドライン設定は実機の隔離プロファイル試験で確認した。承認型CDPは調査資料上で確認できなかったため、利用可能としていない。

### Vivaldi

- 本環境の標準インストール場所にVivaldi実行ファイルがなく、接続試験を実施できなかった。
- Chromium系であることのみから、CDPの起動引数、バージョン互換、待受範囲、ゲームページ/OOPIF対応を推定して正式対応とは判定しない。

### WebView2 / 74EN

- Microsoftの公式資料は、WebView2起動前に`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`環境変数またはアプリ別ポリシー設定で`--remote-debugging-port`を渡す方法を説明している。
- 同資料では、複数WebView2コントロールがCDPポートを共有し、同時に個別デバッグできない制約、および外部デバッガーへWebViewコンテンツが露出し得る点が明記されている。
- 現在の開発環境では74ENを起動していないため、M13変更後のWebView2接続・所有プロセス・74EN併用の再試験は未実施。以前の利用者確認は履歴として保持し、今回の独立試験結果に数えていない。

## 3. 隔離ブラウザ試験

実ゲームを開かず、各ブラウザの一時プロファイルで`about:blank`だけを対象とした。アプリ側のゲーム取得機能、ゲームAPI送信、ログイン情報、通常プロファイルは使っていない。一時プロファイルは試験後に削除した。

| ブラウザ | 確認結果 | 判定 |
|---|---|---|
| Microsoft Edge 158.0.4370.45 | `--user-data-dir`と`--remote-debugging-port=0`で`DevToolsActivePort`を取得。loopbackの`/json/list`から`about:blank`ページを選択し、.NET `ClientWebSocket`でCDP接続、`Page.getFrameTree`の応答を確認。 | **隔離CDP接続成功**。ゲームページ、アプリ統合、実ポート所有PIDは未確認。 |
| Google Chrome 158.0.8127.37 | 一時プロファイルで動的port起動を試したが`DevToolsActivePort`が生成されず、固定port試験ではブラウザプロセスが終了した（終了コード`-2147483645`）。原因を特定できず、承認型フローも試していない。 | **この環境で接続成立せず**。Chrome全般の非対応とは判定しない。 |
| Vivaldi | 標準インストール場所に実行ファイルが見つからず。 | **未試験**。 |
| 74EN内蔵WebView2 | 74ENが起動していない。 | **未試験**。 |

Edge試験はブラウザの分離プロファイルとCDPの基本互換を確認したもので、通常の画面表示、ゲームURL、ゲームAPI、OOPIF、利用者プロファイル、終了後のゲーム継続を検証したものではない。

### 待受アドレスとプロセス所有者

Edge試験は`--remote-debugging-address=127.0.0.1`で起動し、CDP discoveryおよびWebSocket接続先のhostがloopbackであることを確認した。一方、WindowsのTCP待受一覧を読む`Get-NetTCPConnection`、`netstat`および.NETの`GetActiveTcpListeners()`は、この環境のアクセス制限または情報非表示により独立した待受アドレス列挙に失敗した。このため、実際のlistenerがloopback以外にbindしていないことをOSのTCP表で証明していない。

ポート所有PID、ブラウザ実行ファイルの署名、ユーザーが選択したブラウザとの一致も未検証。現行アプリ自身にもこれらの確認は実装されていない。

## 4. 現行アプリへの影響と実装判断

M13は接続方式の技術検証Phaseであり、今回、ブラウザ自動起動・プロファイル作成・システム設定変更をアプリへ実装していない。ユーザーの明示操作なしに既存ブラウザプロファイル、レジストリ、環境変数、システム設定を変更しない。

現行の`CdpCaptureOptions`は固定ポート番号を要求する。Edgeの動的portはブラウザ側で利用できたが、アプリが`DevToolsActivePort`を安全に発見・検証して利用する仕組みはない。動的portを認証機構と見なさない。専用プロファイルの場所・寿命、利用者の同意、ブラウザ終了後のポート閉鎖、既存プロセスとの共存が決まるまでは自動起動統合を行わない。

M12.1で実装したloopback endpoint検証は有効な防御だが、CDPを開いた同一端末プロセスへの認証にはならない。Chrome/Edgeとも、loopback限定と動的portだけで第三者ローカルプロセスを排除できるとは評価しない。

## 5. ビルド・自動テスト・回帰

- M13調査後Release自動テスト：**73 passed / 0 failed / 0 skipped**。
- WPF Releaseビルド：**成功、警告0、エラー0**。
- M13ではアプリのソースコードを変更していないため、回帰テストはM12.1既存コード一式を再実行したもの。
- Edgeの隔離ブラウザ試験は一時的なローカル検証で、リポジトリへ自動テストとして追加していない。
- 外部ゲームサーバーへの独自リクエスト、ゲームAPI本文取得、ゲーム操作、認証情報の取得・保存は行っていない。
- HTTPS、実ゲーム、Chrome承認UI、Vivaldi、74EN/WebView2、プロセス終了時のゲーム継続は今回試験していない。

## 6. M13の判定

| M13条件 | 状況 |
|---|---|
| Chrome承認型CDPの公式方式と制約 | 資料調査済み。C#独立クライアントでの直接承認接続は未確認 |
| C#独自クライアントの接続 | Edge隔離プロファイルの`Page.getFrameTree`成功。Chrome実機は失敗・原因未特定 |
| Edge Chromium接続 | 隔離CDP基本接続を確認。ゲーム実機は未確認 |
| Vivaldi接続 | 未試験（本環境にインストールを確認できず） |
| 専用プロファイル・動的port | Edge一時profileで動的port成立。製品の発見/ライフサイクル管理は未実装 |
| 待受範囲・プロセス所有者・署名 | 未検証。OSの接続表を本環境から列挙できなかった |
| WebView2/74ENセキュリティ再確認 | 公式資料調査済み。現行74EN実機は未検証 |
| Releaseビルド・自動テスト | 成功、0警告/0エラー、73件合格 |

以上より、調査結果はブラウザごとに整理したが、**M13の全完了条件は満たしていない**。Chrome、Vivaldi、WebView2と待受/所有プロセス検証の空欄を成功扱いしない。M14の正式対応ブラウザ集合や自動起動機能は、これらの確認前に確定しない。

## 7. 残存リスクと利用者確認

追加実機検証が必要な項目：

1. Chromeの隔離profileでの通常CDP起動可否と、Chrome 144以降の`chrome://inspect/#remote-debugging`利用者承認フロー。普段使いのChrome profileは使用しない。
2. Vivaldiを利用する場合、その実行ファイルとバージョンを確認した上で一時profileを使うCDP接続試験。現時点で正式対応とはしない。
3. 74EN/WebView2の現在の配布版で、M12.1後の接続、ポート共有、74EN終了後のゲーム継続を再確認。
4. 管理者権限を要求せずに待受アドレスとPIDを検証できる実装・試験方式の調査。利用者の同意なくポート走査を広げない。

Chrome承認テストでは、利用者が`chrome://inspect/#remote-debugging`を明示的に有効化し、接続許可を判断する必要がある。開発者ツール接続を自身の通常ゲームプロファイルで有効にするかどうかは利用者判断とし、この作業では変更していない。

## 8. 参考資料

- [Chrome: remote-debugging-portのセキュリティ変更](https://developer.chrome.com/blog/remote-debugging-port)
- [Chrome: Chrome DevTools MCPからの利用者承認型接続](https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session)
- [Chrome DevTools MCP: 接続方式とオプション](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/configuration.md)
- [Chrome Releases: 2026年11月のStable更新](https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop.html)
- [Microsoft: WebView2のCDPデバッグ設定と制約](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/debug-visual-studio-code)
- [M12.1作業報告](MIGRATION_M12_1_REPORT.md)
- [次期開発統合仕様](docs/NEXT_DEVELOPMENT_SPEC.md)
