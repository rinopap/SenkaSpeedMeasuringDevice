# M-Phase 4 実施報告

実施日: 2026-11-26

## 実装内容

- 設定ウィンドウでダークモード、フォント、8～24pt、最前面表示、Windowsログオン時の起動を変更できるようにした。
- 既存`ui_config.json`のsnake_case設定を読み書きし、ログオン起動は利用者が設定画面で適用した場合だけHKCU Run値を変更する。
- 通知領域にアイコンと「ウィンドウを開く」「設定」「終了」を追加した。ウィンドウの閉じる操作はタスクトレイへ格納し、明示的な「終了」で監視を停止してアプリを終了する。
- 画面はCSV読込、74EN JSON監視、経験値表示を継続し、1分ごとに時間枠表示を再計算する。
- WPFウィンドウは最前面表示を切り替えられる。74ENウィンドウ取り込み時の表示スタイルやDPI/フォーカス互換は実機で未検証。

## 変更ファイル

- `src/SenkaSpeedMeasuringDevice.App/App.xaml`
- `src/SenkaSpeedMeasuringDevice.App/App.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/WindowsStartup.cs`
- `src/SenkaSpeedMeasuringDevice.App/SenkaSpeedMeasuringDevice.App.csproj`
- `README.md`

## 検証結果

- .NET SDK 10.0.404でReleaseソリューションビルド成功、警告0・エラー0。
- xUnit 15件成功、失敗0件、スキップ0件。
- 設定UI、通知領域操作、レジストリ自動起動、DPI、74ENウィンドウ取り込みの実機操作はまだ実施していない。

## 手動確認項目

1. 設定画面で配色とフォントを変更して適用し、アプリ再起動後も保持されること。
2. 「常に最前面」を切り替えて表示に反映されること。74ENに取り込む場合はOFFで試す。
3. ×でウィンドウが隠れ、通知領域のダブルクリックまたは「ウィンドウを開く」で戻ること。
4. 通知領域の「終了」でアプリが終了すること。
5. 「Windowsログオン時に自動起動」を明示的にON/OFFし、HKCU Runに`SenkaKeisoku`値が追加/削除されること。
6. M3報告の手順に沿って74ENのJSON監視・CSV追記を確認する。

## 未実装・未検証

- 74EN内蔵ブラウザまたは通常ブラウザへのCDP接続。
- 実74ENのウィンドウ取り込みでの最前面/解除/DPI動作。
- 複数ディスプレイ、DPI変更、長時間常駐、起動時自動実行の実機確認。
- 設定保存先・CSV保存先をアプリ配置フォルダとしているため、その場所への書込み権限がない環境では保存に失敗する。後続の配置方法確認時に保存先の扱いを決める。

## セキュリティ上の確認

- 自動起動レジストリは設定画面で利用者が適用した場合にのみ変更する。
- タスクトレイや設定はゲームAPIやブラウザの制御を行わない。

## 次工程

M5でCDPの通常ブラウザ接続とWebView2接続条件をプローブで検証し、接続対象の限定と許可コマンドを確認する。独自プロキシは移植しない。

