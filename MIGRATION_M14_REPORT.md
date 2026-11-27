# M-Phase 14 実施報告：マルチブラウザ対応およびUI簡略化

作成日: 2026-11-27  
対象: M-Phase 14  
判定: **Chrome/Edge/74EN JSON向けの設定UI、ブラウザ検出、起動補助、選択したChrome/EdgeのCDP待受プロセス照合を実装。Releaseビルド、83件の自動テスト、設定画面の実表示を確認した。ブラウザ起動補助とVivaldi/Brave/Operaのゲーム実機対応は未検証のため、正式対応範囲はChrome/Edgeに限定する。M15には着手していない。**

## 1. 前Phaseの完了条件・移行判断

初回のM13報告ではChromeとWebView2が未確認、M13.1/M13.2も74EN再試験待ちだった。その後、M13.3のOOPIF修正を利用者が74EN 5.4.3ENで確認し、経験値計測、両CSVへの追記、正常/強制終了後の再起動を完了した。今回の作業開始時に利用者からChromeとEdgeも同様に動作したとの追加報告を受けた。

ブラウザ別の対応方式・安全策・正式対応可否を整理し、未検証ブラウザを選択肢に出さないM14方針を明確にできたため、M13からの移行は可能と判断した。根拠と残件は [M13完了判定追補](MIGRATION_M13_COMPLETION_ADDENDUM.md) に記録した。Chrome/Edge/74ENの実機確認は利用者報告であり、開発側による独立実機試験とは区別する。

## 2. 実装内容

- 設定画面を「ブラウザから直接取得」「74ENのJSONファイルから取得」の排他チェックボックス方式に変更。両方OFFでは監視停止となる。
- Chrome/EdgeをWindowsの既定ブラウザ情報、App Paths、一般的なインストール先から検出し、WinVerifyTrustと署名発行者名で確認したインストールだけを候補に表示する。
- Windows既定ブラウザがChrome/Edgeなら初期選択に利用する。実行中の74ENが検出された場合は、既存WebView2接続先として候補に追加する設計とした。
- 未検証ブラウザは候補に表示せず、既知のVivaldi等が見つかった場合は未検証であることを説明する。
- 明示操作と確認ダイアログの後に、署名確認済みChrome/Edgeを艦これ専用の永続プロファイルで起動する補助を追加した。起動先URLは固定、デバッグ待受はloopback、ポートはブラウザに動的割当させる。`DevToolsActivePort`とloopback上の`/json/version`でポートを確認し、設定欄へ反映する。
- 新規設定でChrome/Edgeを選択した場合、CDP接続前にWindows TCP listenerの所有PIDと実行ファイルパスを照合する。不一致、ポート無し、OSの待受情報を読めない場合は接続しない。ブラウザ識別子がない既存設定は後方互換のため従来のCDP接続検証を維持する。
- 74ENやブラウザを計測機から起動/終了しない。74EN選択時は既存起動済みアプリを使う案内を表示する。
- 詳細情報を折りたたみ、固定ゲームURL、対象APIパス、選択APIホスト、手動CDPポートを表示する。URL/APIホストは編集できない。
- 新規設定は監視停止で開始する。`capture_mode`がない既存設定は従来の`cdp_enabled`からCDP/JSONを復元し、既存利用者の設定を維持する。

## 3. 変更ファイル

- `src/SenkaSpeedMeasuringDevice.App/BrowserSupport.cs`（新規）
- `src/SenkaSpeedMeasuringDevice.Capture/CdpListenerProcessPolicy.cs`（新規）
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureOptions.cs`
- `src/SenkaSpeedMeasuringDevice.Capture/CdpCaptureSource.cs`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml`
- `src/SenkaSpeedMeasuringDevice.App/SettingsWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.App/MainWindow.xaml.cs`
- `src/SenkaSpeedMeasuringDevice.Core/AppSettings.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/AppSettingsTests.cs`
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpListenerProcessPolicyTests.cs`（新規）
- `MIGRATION_M13_COMPLETION_ADDENDUM.md`（新規）
- `MIGRATION_M14_REPORT.md`（新規）

## 4. 既存実装から再利用した内容

- `CdpCaptureSource`、`CdpCaptureOptions`、CDP endpoint検証、API対象ポリシー。
- `ICaptureSource`、74EN JSONファイル監視、計測キューと世代管理。
- `GameServerRegistry`の20サーバー定義。
- 既存の経験値解析、時速計算、CSV読み込み/追記、テーマ/フォント/タスクトレイ設定。
- M10のタブ行にある状態表示と歯車設定ボタン。

CDP受信処理、APIフィルター、計算式、CSV形式は変更していない。

## 5. 設計上の判断と理由

- 現在の実機確認があるChrome/Edgeだけを正式候補にし、Vivaldi/Brave/Operaは未確認のまま正式対応と表示しない。Chromiumベースという理由だけで互換性を推定しない。
- 起動補助はユーザーのボタン操作と警告同意の後だけ実行する。通常プロファイルを指定/変更せず、専用プロファイルとブラウザの動的ポートを用いる。
- 起動補助の専用プロファイルはログイン負担を減らすため永続化する。ブラウザ自身がログイン状態を保存する可能性を画面で説明し、計測機はCookieやパスワードを読み取らない。
- 旧設定ファイルとデータ形式を壊さないよう、設定モード移行を後方互換にした。初回設定画面の両取得方式OFFは仕様書の監視停止方針に合わせた。
- 一般ユーザーにCDP用語を常時見せず、手動ポート等は折りたたみ詳細に置いた。

## 6. 仕様書との差分

- M14の利用者向け設定、取得方式排他、Chrome/Edge検出、サーバー選択、詳細情報、起動補助を実装した。
- ブラウザ直接取得では選択ブラウザの署名に加え、指定CDPポートの待受所有プロセスも一致確認する。
- M14の候補にあるVivaldi/Brave/Operaは、ゲーム実機を含む互換性確認がないため選択肢に含めていない。VivaldiはPCへのインストールとAuthenticode署名発行者を確認したが、CDP接続/ゲーム取得は試験していない。
- `74EN内蔵WebView2`は実行中プロセスを検出したときだけ候補に表示する設計だが、今回のM14画面確認時には74ENを起動していなかったため、その候補表示は確認していない。74EN JSON取得は選択可能。
- ブラウザ起動補助は実ゲームURLを開くため、利用者ログインが必要な実機起動までは今回実行していない。

## 7. Releaseビルド結果

- SDK: .NET 10.0.404。
- `dotnet publish` Release、`win-x64`、self-contained: **成功**。
- 出力先: `artifacts/M14-multibrowser-win-x64/`。
- 最終発行: **警告0、エラー0**。
- 実行ファイル: `artifacts/M14-multibrowser-win-x64/SenkaSpeedMeasuringDevice.App.exe`。

途中の発行で、画面確認用に起動していた検証アプリがDLLをロックし失敗した。対象が今回起動したM14プレビューであることを実行パスで確認して終了後、最終発行を成功させた。ユーザー側の74ENや別アプリは終了していない。

## 8. 自動テスト・回帰試験

- Release xUnit: **83 passed / 0 failed / 0 skipped**。
- 既存の計算、CSV/設定、JSON監視、CDP許可ポリシー、OOPIF、合成CDP取得テストを維持。
- 追加テストで新規設定の監視停止、旧`cdp_enabled`設定の移行、新設定モードの不正値フォールバック、ブラウザID/サーバーIDの保存を確認。
- ローカル合成TCP試験で、loopback listenerの所有実行ファイルが一致する場合に許可し、別の実行ファイルパスを拒否することを確認。
- WPF/XAMLを含む最終Release publishを成功させた。

## 9. 合成通信試験

- 81件のテストには既存のローカル合成CDP fixtureによる対象API取得・ポリシー試験が含まれ、すべて合格した。
- M14ではCDP中継や通信解析を変更しておらず、外部ゲームサーバーへ独自リクエストを送信していない。
- 起動補助が読み取る`/json/version`はloopback上のデバッグメタデータだけで、API本文は取得しない。
- 起動補助を実行したブラウザのCDPポート/解放状態は今回未試験。

## 10. 実機/UI試験

### 開発環境で確認

- 最終発行アプリのメイン画面を起動し、2つの計測タブ、タブ行の監視状態、歯車アイコンを確認した。
- 設定画面を開き、初期状態が両取得方式OFFであることを確認した。
- 直接取得をONにするとJSON監視がOFFになり、JSON監視をONにすると直接取得がOFFになることを画面で確認した。
- ブラウザ候補に、署名を確認したMicrosoft Edge 158.0.4370.45とGoogle Chrome 158.0.8127.37を確認した。Vivaldiは未検証の説明に出て候補には含まれない。
- 詳細欄に固定ゲームURL、`/kcsapi/api_port/port`、所属サーバー別APIホスト、手動ポートを表示することを確認した。
- 設定を適用していないため、ユーザー設定ファイル、CSV、起動レジストリは変更していない。

### 利用者からの前Phase実機確認報告

- ChromeとEdgeでゲーム経験値の取得と時速計測を確認したとの報告。
- 74EN 5.4.3EN/WebView2のM13.3版で経験値時速、両CSVへの追記、正常終了/強制終了後の再起動を確認したとの報告。
- 上記はM14の専用プロファイル起動補助を使った試験ではない。

## 11. セキュリティ上の評価

- ブラウザ実行ファイルの署名と発行者を検証し、起動時にも再確認する。
- CDP待受は`127.0.0.1`に限定し、任意ホスト/任意URL/任意コマンド入力を追加していない。起動先は固定URL。
- 通常ブラウザプロファイルの変更、システムプロキシ/証明書の変更、ブラウザの終了、JavaScript注入、Fetchインターセプト、ゲームAPI送信を行わない。
- 専用プロファイルにブラウザ自身がログイン状態を保存するリスクは残る。画面で利用者へ事前説明する。
- CDPは同一PC上の別プロセスからアクセスされ得る。loopbackや動的ポートは認証ではない。
- 新規設定のChrome/EdgeではOSのlistener所有PID/実行パスを照合するが、ブラウザ識別子のない旧設定と74EN WebView2には同じ照合を適用していない。
- 起動補助が未試験のため、実ブラウザ各版で動的ポートと対象ゲームページの起動挙動は確定していない。

## 12. CDP取得成功・失敗の確認結果

- 既存のCDP synthetic tests、対象API許可、選択サーバー、OOPIFフレーム連携の回帰試験は全合格。
- 利用者によるChrome/Edgeおよび74ENの経験値取得成功はM13実機確認として記録し、M14の自動起動試験とは区別した。
- M14起動補助からのCDP接続、動的ポートの再起動後再発見、ブラウザ終了後のポート閉鎖は未確認。

## 13. 残存リスク・未確認事項

- Vivaldi、Brave、OperaのCDP/ゲーム取得は未検証で、正式対応ではない。
- 74EN起動中の候補表示と、専用プロファイル起動補助での74ENとの併用はM14画面から未確認。
- 専用プロファイルを用いたChrome/Edge起動、初回ログイン、計測機再起動、ブラウザ終了後のポート閉鎖を実機確認していない。
- `WinVerifyTrust`の署名検証はReleaseビルドで確認したが、署名期限切れ/失効/破損ファイルの画面動作を専用テストしていない。
- M13から引き継いだ待受範囲/所有プロセスの独立OS確認、Chrome承認型CDPの直接クライアント利用、Vivaldi互換性が残る。
- 24時間連続動作、DPI差、ログオン自動起動、配布ライセンスはM14で未試験。

## 14. 発見された不具合と次Phaseへの影響

- 設定画面に、作業領域の高さに合わせた最大高さと縦スクロールを追加した。最終版は画面に全設定を表示でき、XAML/Releaseビルドも合格した。
- 新たな計算/CSV/CDP取得の不具合は確認していない。
- M14の実装結果と受入条件は報告済み。**M15の安定性対策には着手していない。**
- M15開始前に、利用者が専用プロファイル起動補助を確認する。Chrome/Edgeどちらかで、同意後の起動と動的ポート反映、適用後の計測、計測機終了後もゲーム継続、ブラウザ終了後のポート閉鎖を確認する。Vivaldi等は確認が済むまで選択肢に追加しない。

