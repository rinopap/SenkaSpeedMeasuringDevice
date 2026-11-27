# M-Phase 2 実施報告

実施日: 2026-11-26

## 実装内容

- `api_port/port`の受信本文から`api_basic.api_experience`と`api_ship[*].api_exp[0]`合計を抽出するCore解析器を追加。UTF-8 BOM、`svdata=`、URLエンコード形式を扱い、経験値は64bit整数で読む。
- 経験値時系列、1時間枠の最初の観測値、時間当たりの増加量、戦果換算（経験値時速×7/10000）、3時間分の表示文を実装。
- 既存`timestamp,admiral_exp`と`timestamp,ship_total_exp` CSVの読み込み・追記を実装。UTF-8、秒精度のローカル時刻形式、長整数を維持する。未知のヘッダーや不正行は取り込まない。
- C++版の`ui_config.json`で使う`dark_mode`、`font_family`、`font_size`、`always_on_top`キーとの読み書き互換を実装。フォントサイズは8～24へ制限。
- JSON解析、長整数、CSV既存ファイル、計算基準値、設定互換を対象にxUnitを追加。

## 変更ファイル

- `src/SenkaSpeedMeasuringDevice.Core/ApiResponseParser.cs`
- `src/SenkaSpeedMeasuringDevice.Core/ExperienceSnapshot.cs`
- `src/SenkaSpeedMeasuringDevice.Core/SenkaCalculator.cs`
- `src/SenkaSpeedMeasuringDevice.Core/CsvStorage.cs`
- `src/SenkaSpeedMeasuringDevice.Core/AppSettings.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/`（解析、計算、CSV、設定テスト）
- `README.md`

## 再利用した既存仕様

- C++版`main.cpp`の`api_experience` / `api_exp[0]`抽出、戦果換算、時間枠の先頭観測、CSV列名と日時形式、snake_case設定キーを基準にした。
- CSV履歴に同じ経験値が続いても追記し、通信を観測していない時間のサンプルを補わない。

## 検証結果

- .NET SDK 10.0.404、Release構成のソリューションビルドとxUnitを実行。
- 12件成功、失敗0件、スキップ0件。
- 試験した内容はローカルxUnitでの確認。実利用者の既存CSVファイルや74EN実環境との照合は未実施。

## 未実装・未検証

- 解析・計算・保存をWPF画面や取得元へ接続していない。74EN JSON監視はM3で実装する。
- 既存CSVの利用者データを使った手動互換確認、設定ファイルをC++版と共有する運用の確認は未実施。
- CDP、ブラウザ/WebView2、ウィンドウ取り込み、実機での長時間動作は未検証。
- 不正JSONに対する解析器の単体テストはあるが、異常入力を含む大規模なファジングは実施していない。

## セキュリティ上の確認

- 解析器は渡された本文をメモリ内で読み取るだけで、通信を発生させない。
- CSVには時刻と経験値のみを書く。API本文、Cookie、トークンは保存しない。

## 次工程

M3では`FileSystemWatcher`と`Channels`を使う74EN JSON互換取得元を実装し、部分書込み、イベント重複、再起動、古いファイルの掃除を検証する。
