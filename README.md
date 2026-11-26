# SenkaSpeedMeasuringDevice C#移行版

このフォルダは.NET 10 / WPFを使う新しい実装です。C++/Qt版のソースと検証資料は[develop-cppブランチ](https://github.com/rinopap/SenkaSpeedMeasuringDevice/tree/develop-cpp/cpp)にスナップショットとして保全しています。

- 仕様: [C_SHARP_MIGRATION_SPEC.md](docs/C_SHARP_MIGRATION_SPEC.md)
- M-Phase 0 調査報告: [MIGRATION_M0_ASSESSMENT.md](docs/MIGRATION_M0_ASSESSMENT.md)

M-Phase 1～8の実装と検証用配布候補の作成を完了しました。M5/M6/M7のローカル検証では、Edge CDPによる本文取得と、取得プロセスの正常・強制終了後もブラウザが合成API要求を継続することを確認しました。実ゲーム、実74EN、HTTPS、ウィンドウ取り込み、長時間動作は未確認です。正式公開前に[MIGRATION_M8_REPORT.md](MIGRATION_M8_REPORT.md)の残項目とライセンス方針を確認してください。

## SDK

開発時点のLTS候補として.NET 10を使います。Windows 10の対応下限は配布対象を確定する前に確認します。

```powershell
dotnet build SenkaSpeedMeasuringDevice.sln
dotnet test SenkaSpeedMeasuringDevice.sln
```

ビルド結果と現段階の制約は[MIGRATION_M1_REPORT.md](MIGRATION_M1_REPORT.md)を参照してください。

M2の詳細とテスト結果は[MIGRATION_M2_REPORT.md](MIGRATION_M2_REPORT.md)を参照してください。

M3の実装状況、未検証事項と74EN手動確認手順は[MIGRATION_M3_REPORT.md](MIGRATION_M3_REPORT.md)を参照してください。

M4の設定・トレイ・Windows自動起動と手動確認項目は[MIGRATION_M4_REPORT.md](MIGRATION_M4_REPORT.md)を参照してください。

CDP検証の範囲と制約は[MIGRATION_M5_REPORT.md](MIGRATION_M5_REPORT.md)を参照してください。

CDP取得元の統合範囲とテスト結果は[MIGRATION_M6_REPORT.md](MIGRATION_M6_REPORT.md)を参照してください。

統合試験の結果と残る手動確認は[MIGRATION_M7_REPORT.md](MIGRATION_M7_REPORT.md)を参照してください。

検証用ZIPの使い方は[README_USER.md](README_USER.md)を参照してください。自己完結型Release候補は[artifacts](artifacts/)にあります。

M8配布候補の構成確認と制約は[MIGRATION_M8_REPORT.md](MIGRATION_M8_REPORT.md)を参照してください。

