# 戦果時速計測機

**SenkaSpeedDevice2.0** は、ElectronicObserverEN（74EN）が標準保存するデータを読み取り、戦果時速と艦娘Exp時速を表示するWindows向け非公式補助ツールです。

## 主な機能

- 74EN標準SQLiteから提督経験値を読み取り、戦果時速を計測。
- 通常艦隊戦闘、演習、遠征のAPI記録から艦隊の獲得Exp時速を集計。
- 従来の74EN JSON監視方式にも対応。
- 計測履歴と計測区間を管理し、再起動後も継続。
- コンパクトなGUI、ダーク/ライト表示、フォント設定とプレビュー。

## 対応環境

- Windows x64
- ElectronicObserverEN 5.4.3EN
- .NET 10ランタイムは配布物に含まれます。

## 使用方法

1. [Releases](../../releases)から`SenkaSpeedDevice2.0-win-x64.zip`をダウンロードして展開します。
2. `SenkaSpeedMeasuringDevice.App.exe`を起動します。
3. 必要に応じて設定画面で74ENの`Record\ElectronicObserver.sqlite`を指定します。

SQLite方式では74ENの個別JSON保存設定は不要です。74ENの起動・終了は本ツールから操作しません。

## 計測値と制約

- **戦果時速**は母港APIの提督累積経験値差分を使います。
- **SQLite方式のExp時速**は対象結果APIが報告する艦隊獲得経験値を集計します。
- **JSON方式のExp時速**は全所持艦の累積経験値差分を使います。
- SQLite方式とJSON方式のExp時速は異なる指標で、同じ値になるとは限りません。
- 対象は通常艦隊戦闘、演習、遠征です。連合艦隊は対応対象外です。
- API記録欠損、保存時刻の差、経験値上限等により表示値が実際の加算量と異なる場合があります。
- 24時間連続試験は未実施です。長時間稼働の安定性を保証しません。

## データとセキュリティ

設定とCSVは実行ファイルのフォルダに、計測履歴は`%LOCALAPPDATA%\SenkaSpeedMeasuringDevice`に保存します。更新前に既存ファイルをバックアップしてください。

74ENのSQLiteは読み取り専用で開きます。ゲーム通信を仲介・変更せず、API送信、ゲーム操作自動化、認証情報の取得・保存を行いません。計測機の停止がゲーム通信を止めない構成です。SQLiteの標準共有ロック管理に伴い補助ファイル状態が変化する場合があります。

本ツールは74ENや艦これ運営が配布・承認する製品ではありません。実行ファイルは未署名で、Windows SmartScreen等の警告が出る場合があります。保護機能を無効化せず、Releaseページに掲載するSHA-256と照合してください。SHA-256だけで配布元の真正性が保証されるわけではありません。

## ライセンス

MIT License — Copyright (c) 2026 rino_pap。依存ライブラリには各ライセンスが適用されます。詳細は[`LICENSE`](LICENSE)と配布物の第三者通知を参照してください。

## 開発

```powershell
dotnet build SenkaSpeedMeasuringDevice.sln --configuration Release
dotnet test SenkaSpeedMeasuringDevice.sln --configuration Release
```

旧Python版と過去の配布物は既存のv1.1.0/v1.2.0 Releaseを参照してください。CDP、C++等の開発成果は各開発ブランチに保全します。
