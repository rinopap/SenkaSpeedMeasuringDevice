# EO専用ブランチ作成追補

更新日：2026-11-28

EO-Phase 0報告書作成後、ユーザーからGit管理の許可を受けて実施した履歴保全作業を記録する。

## 実施内容

- 公開`develop-csharp`をGitHubから作業領域へcloneした。
- `develop-csharp-eo`を`develop-csharp`先端`8a9e4c7bcd7422f5f47bccb460a917cff352df53`から作成した。これにより公開履歴を親として保つ。
- `.git`のないローカルC#出力スナップショットから、M14.1までのソース・テスト・開発報告・既存仕様資料を取り込んだ。
- `docs/EO_DEVELOPMENT_SPEC.txt`と`docs/EO_PHASE_0_RESEARCH_REPORT.md`を追加した。
- `.temp-dotnet10*`、`.vs`、`artifacts`、各`bin`/`obj`、テスト結果フォルダとダンプは取り込まなかった。
- 既存ブランチおよびGit履歴は変更・書き換えしていない。

## 状態

この追補はブランチ初回コミットと合わせて保存する。Releaseビルド・テストをこの移行作業中に実行したものではない。M14.1報告書に記録された試験結果を、この追補の再試験結果として扱わない。

