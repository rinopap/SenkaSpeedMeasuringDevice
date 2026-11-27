# SenkaSpeedMeasuringDevice C#移行版

このフォルダは.NET 10 / WPFを使う新しい実装です。C++/Qt版のソースと検証資料は[develop-cppブランチ](https://github.com/rinopap/SenkaSpeedMeasuringDevice/tree/develop-cpp/cpp)にスナップショットとして保全しています。

- 仕様: [C_SHARP_MIGRATION_SPEC.md](docs/C_SHARP_MIGRATION_SPEC.md)
- 次期統合仕様（M11以降の優先仕様）: [NEXT_DEVELOPMENT_SPEC.md](docs/NEXT_DEVELOPMENT_SPEC.md)
- M-Phase 0 調査報告: [MIGRATION_M0_ASSESSMENT.md](docs/MIGRATION_M0_ASSESSMENT.md)

M-Phase 1～10は実装済み、M-Phase 11（艦これ専用化）の実装・自動試験は完了しています。M11後の実ブラウザURL形式と設定画面は未確認です。[MIGRATION_M11_REPORT.md](MIGRATION_M11_REPORT.md)に変更と制約を記録しています。正式公開前に残項目とライセンス方針を確認してください。

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

iframe通信の監視修正と合成試験結果は[MIGRATION_M9_REPORT.md](MIGRATION_M9_REPORT.md)を参照してください。

タブ列UIと通常ブラウザ起動の操作改善案は[MIGRATION_M10_REPORT.md](MIGRATION_M10_REPORT.md)を参照してください。
