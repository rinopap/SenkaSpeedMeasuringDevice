# M-Phase 1 実施報告

実施日: 2026-11-26

## 実装内容

- .NET 10.0.404 SDKを使うWPFアプリ、Coreライブラリ、Captureライブラリ、xUnitテストプロジェクトを作成。
- `ApiResponse`を`ReadOnlyMemory<byte>`本文とし、`ICaptureSource`に開始/停止/状態/受信イベント/非同期破棄契約を定義。
- 基本画面に「戦果時速」「艦娘Exp時速」の2タブを配置。設定ボタンは未実装で無効化し、通信機能を誤って示さない。
- `.gitignore`でビルド出力を除外。`bin/obj`はGitHubへ追加しない。

## 変更ファイル

- `SenkaSpeedMeasuringDevice.sln`
- `src/SenkaSpeedMeasuringDevice.App/`
- `src/SenkaSpeedMeasuringDevice.Core/`
- `src/SenkaSpeedMeasuringDevice.Capture/`
- `tests/SenkaSpeedMeasuringDevice.Tests/`
- `.gitignore`, `README.md`

## 再利用した設計

既存C++/Qt版の二つの計測タブと共通取得元の考えを引き継いだ。計算・保存処理はM-Phase 2以降で移植するため、この段階ではまだ入れていない。CDPやプロキシ通信コードも含めていない。

## 検証結果

- SDK: .NET 10.0.404（作業用ローカルSDK）。xUnit依存関係はNuGetから復元。
- `dotnet build SenkaSpeedMeasuringDevice.sln -c Release --no-restore -p:UseSharedCompilation=false`: 成功、警告0、エラー0。
- `dotnet test SenkaSpeedMeasuringDevice.sln -c Release --no-restore -p:UseSharedCompilation=false`: 合格1、失敗0、スキップ0。
- WPFウィンドウをWindows上で起動し、タイトル、2タブ、「データなし」表示を画面とアクセシビリティツリーで確認。
- UI確認のためDebug実行ファイルを起動したままDebug再ビルドを試み、実行ファイルのロックで失敗した。Release出力は別フォルダのため、Releaseビルド/テストは成功した。
- ビルドしたWPFアプリをWindows上で起動し、2タブと「データなし」表示を画面で確認。

## 未実装・未検証

- 計算処理、CSV互換、設定ファイル、74EN JSON監視、CDP接続、WPFウィンドウ取り込み、トレイ、自動起動。
- Windows 10の最小対応ビルド、Release/Self-contained配布とサイズ。
- CDPの通常Chromeおよび74EN互換性。

## 次の工程

M-Phase 2で経験値抽出、戦果/艦娘経験値計算、CSV互換処理とC++/Python版の基準ケースをテストへ移植する。
