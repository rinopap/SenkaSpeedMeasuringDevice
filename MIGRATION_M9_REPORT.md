# M-Phase 9 CDP iframe監視修正

実施日: 2026-11-26

## 原因

利用者の通常ブラウザ試験で、CDP接続は成立するが経験値データが届かない事象が発生した。APIのパス、成功ステータス、APIホストの設定は利用者確認で一致していた。

従来の取得元は選択したトップレベルページにだけNetwork監視を有効化し、別プロセスのiframe targetを監視していなかった。仕様書8.4のiframe通信要件を満たしていない実装上の不足と判断した。

## 変更

- 選択したページに関連するiframe targetだけを`Target.setAutoAttach`で追跡する。
- 新規targetを一時停止せず (`waitForDebuggerOnStart=false`)、iframeのNetwork監視を有効化する。
- iframeの子iframeにも同様の監視を設定する。
- flattened session IDを使い、`Network.getResponseBody`を正しいiframeセッションに送る。
- requestIdをCDP session IDと組み合わせて管理し、複数frame間の衝突を防ぐ。
- リクエスト改変・停止・再送、JavaScript実行、Cookie取得は追加していない。本文取得は明示ホストと`/kcsapi/api_port/port`の成功レスポンスだけ。

## 検証

- Releaseビルド成功、警告0・エラー0。
- xUnit 20件成功。
- 一時Edgeプロファイルとloopback合成サーバーで、トップページ`127.0.0.1`から別オリジンiframe`localhost`内のAPIレスポンスを取得。
- 88 bytesの本文SHA-256が合成サーバーの期待値と一致: `419A55CD47C2F8D630C7AFD9C6E0E517672DD595AC44CBD6242346675F0D10AD`。
- 利用者の通常ブラウザ、ゲーム、Edgeプロファイル、74ENには触れていない。

## 残る確認

- 利用者環境で修正版ZIPを用い、通常ブラウザの母港遷移後に表示とCSVが更新されることを確認する。
- 実ゲーム/実74EN/HTTPS/長時間動作等のM7手動確認は引き続き未完了。正式公開候補ではない。

