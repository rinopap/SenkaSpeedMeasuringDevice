# SenkaSpeedDevice2.0

戦果時速計測機の2.0.0を公開します。

## 主な変更

- ElectronicObserverEN（74EN）が標準保存するSQLiteデータベースから計測できるようになりました。
- SQLite方式では74ENの個別JSON保存を有効にする必要がありません。
- 戦果時速に加え、通常艦隊戦闘・演習・遠征のAPI獲得Exp時速を表示します。
- 計測履歴と異常終了時の復旧を改善しました。
- GUI、フォント表示、ダークモードと外観プレビューを改善しました。
- 従来のJSON監視方式も互換機能として利用できます。

## 対応環境と制約

- Windows x64、ElectronicObserverEN 5.4.3ENを対象とします。
- 連合艦隊は対応対象外です。
- SQLite方式のExp時速はAPIが報告した艦隊獲得Exp、JSON方式は全所持艦の累積経験値差分です。両者は異なる指標で、値が一致する保証はありません。
- API記録の欠損や保存時刻のずれ、経験値上限等により、表示値とゲーム内の実加算量が異なる場合があります。
- 24時間連続動作試験は未実施です。長時間稼働やメモリ推移を検証済みとはしていません。
- 実行ファイルはコード署名されていません。Windowsの警告を避けるために保護機能を無効化しないでください。

## 使用方法

1. `SenkaSpeedDevice2.0-win-x64.zip`を展開します。
2. `SenkaSpeedMeasuringDevice.App.exe`を起動します。
3. 必要に応じて設定画面で74ENの`Record\ElectronicObserver.sqlite`または互換JSON保存先を指定します。

## ライセンス

本プロジェクトはMIT License（Copyright (c) 2026 rino_pap）です。第三者ライブラリには各ライセンスが適用されます。
