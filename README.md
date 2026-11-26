# SenkaSpeedMeasuringDevice C#移行版

このフォルダは.NET 10 / WPFを使う新しい実装です。C++/Qt版は削除せず、`../SenkaSpeedMeasuringDeviceCpp`に保全しています。

- 仕様: [C_SHARP_MIGRATION_SPEC.md](docs/C_SHARP_MIGRATION_SPEC.md)
- M-Phase 0 調査報告: [MIGRATION_M0_ASSESSMENT.md](docs/MIGRATION_M0_ASSESSMENT.md)

M-Phase 1を完了しました。WPFの基本ウィンドウ、Coreの共通型/`ICaptureSource`、Captureモジュール、xUnitプロジェクトを作成し、ビルドと初回テストを確認しています。通信取得、計算、CSV保存、設定、タスクトレイはまだ実装されていません。

## SDK

開発時点のLTS候補として.NET 10を使います。Windows 10の対応下限は配布対象を確定する前に確認します。

```powershell
dotnet build SenkaSpeedMeasuringDevice.sln
dotnet test SenkaSpeedMeasuringDevice.sln
```

ビルド結果と現段階の制約は[MIGRATION_M1_REPORT.md](MIGRATION_M1_REPORT.md)を参照してください。
