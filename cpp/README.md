# 戦果時速計測機 Ver.2（Qt 6 / C++20）

Windows 10/11向けQt Widgets版です。Qt 6.8以降、C++20、CMake 3.21以降、MSVC 2022を使います。

## 現在の通信取得方針

追加仕様により、戦果時速計測機をゲーム通信経路へ入れない方式を優先します。現在のアプリは74ENが保存した`api_port/port`レスポンスJSONをローカル監視し、提督経験値と艦娘総経験値を既存CSVへ記録します。アプリの起動・終了はゲーム通信経路を変更しません。

CDP（Chrome DevTools Protocol）方式は第一候補として実現可能性を調査中です。接続先の列挙、対象タブの限定、Networkイベントとレスポンス本文取得、通常ブラウザおよび74EN内蔵WebView2との互換性は、まだ実装・実機確認が完了していません。CDPの接続設定は明示的な同意を得て行い、外部ネットワークへデバッグインターフェースを公開しません。

アプリ本体の「通信プロキシ」操作は削除しました。`phase6-proxy-engine/`以下のコードとローカル試験記録は、過去の検証資料として残していますが、配布アプリの取得方式ではありません。CDPの成立性を確認するまでプロキシを主要方式として再導入しません。

## ビルド

Qt 6のMSVC 2022 x64開発パッケージをインストールし、Developer PowerShellで実行します。

```powershell
cmake -S . -B build -A x64 -DCMAKE_PREFIX_PATH="C:/Qt/6.10.3/msvc2022_64"
cmake --build build --config Release
```

Qtの場所とバージョンに応じて`CMAKE_PREFIX_PATH`を変更してください。生成先は`build\Release\SenkaSpeedMeasuringDevice.exe`です。配布する場合はQtの`windeployqt`で必要なQt DLLとplatform pluginをアプリの隣へ展開してください。

## 使い方

1. 74ENの「ファイル → 設定 → 通信」で「通信内容を保存する」と「response」を有効にし、保存先にexeと同じフォルダの`74en_responses`を指定します。
2. アプリを起動し、「戦果時速」と「艦娘Exp時速」をタブで切り替えます。
3. ×ボタンで隠すとタスクトレイに常駐します。終了はトレイメニューから行います。

## 非改変性と保存データ

- 現行のJSON監視はローカルファイルを読み取り専用で開きます。通信を中継したり、ゲームへリクエストを送信したりしません。
- 対象は`api_port/port`の成功レスポンスJSONです。生レスポンスは永続保存せず、解析後に破棄します。
- Cookie、APIトークン、認証情報は取得・保存しません。外部データベースへ通信データを送りません。
- 保存するのは`exp_log.csv`と`ship_exp_log.csv`の経験値・観測時刻です。
- `74en_responses`内の48時間超のJSONは1時間ごとの確認で削除されます。監視元のJSONは74ENが保存したローカルファイルに限ります。

## 計測と互換性

- 提督経験値は`api_data.api_basic.api_experience`から取得します。
- 艦娘経験値は`api_data.api_ship[*].api_exp[0]`の合計です。
- 戦果時速は経験値増分×7/10000を実測経過時間で割ります。艦娘Exp時速は総経験値の増分を実測時間で割ります。
- CSV形式、初回設定案内、タスクトレイ、ダークモード、フォント設定、最前面表示、自動起動を移植しています。
- ウィンドウはフォント設定適用時に表示文字列を計測して自動リサイズします。

## 資料

- 詳しい設計・進捗・追加仕様は[`DEVELOPMENT_SPEC.md`](DEVELOPMENT_SPEC.md)を参照してください。
- CDP方式の検討で優先する要件は、仕様書の第23節です。
- CDP接続条件、Chromeプロフィール制約、未実施の検証項目は[`CDP_FEASIBILITY.md`](CDP_FEASIBILITY.md)を参照してください。
- 過去のプロキシ検証結果は[`PHASE6_COEXISTENCE_REVIEW.md`](PHASE6_COEXISTENCE_REVIEW.md)にあります。これはローカル模擬試験の記録であり、CDPや実74EN互換性の検証結果ではありません。

