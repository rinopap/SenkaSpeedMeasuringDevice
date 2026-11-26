# 戦果時速計測機 C#移行版（検証用）

本ZIPはWindows x64向けの.NET self-contained Release候補です。.NETの事前インストールは不要です。

## 起動

1. ZIPを展開します。
2. 書込み可能なフォルダへ置きます。CSV、設定、74EN JSON監視フォルダをアプリ実行フォルダの下に作るため、`Program Files`配下は避けてください。
3. `SenkaSpeedMeasuringDevice.App.exe`を起動します。

保存先:

- `exp_log.csv`
- `ship_exp_log.csv`
- `ui_config.json`
- `74en_responses/`

## 74EN JSONモード

既定は「74EN JSON監視」です。画面下部に表示される`74en_responses`のフルパスを、74ENの通信内容保存設定でresponseの保存先に指定してください。`api_port@port*.json`を監視します。

## 通常ブラウザCDPモード

CDPを使う場合は専用のゲーム用プロファイルを使い、Chrome/Edgeのremote debuggingをloopback (`127.0.0.1`) に限定してください。通常利用中のブラウザプロファイルを使わないでください。この設定は強いローカル制御面を開くため、設定画面からCDPを明示的に有効にした場合だけ接続を試みます。本アプリはブラウザ設定を変更しません。

設定画面では以下を入力します。

- CDPポート
- 対象タブの完全なページURL（同じURLのタブが複数あると接続しません）
- APIホストの完全なホスト名/IP（カンマ区切り可）

CDPは`/kcsapi/api_port/port`の指定host/pathだけを読み取ります。Cookieやリクエスト本文を取得せず、ページへのコード注入やゲームAPI送信をしません。

## 既知の検証範囲

- 計算、CSV、JSON、CDP optionのxUnit 20件成功。
- 通常Edgeのloopback合成HTTPサーバーで本文ハッシュ一致を確認。
- 計測プロセスの正常/強制終了後に、テスト用ブラウザが後続の合成リクエストを完了することを確認。
- 実ゲーム、実74EN、HTTPS、74ENウィンドウ取り込み、DPI、ログオン起動、長時間動作は未確認です。このZIPは検証用候補であり、正式公開版ではありません。

## ライセンス

元のGitHub `main`ブランチにLICENSEファイルが見当たらないため、本ZIPにも独自のライセンス許諾を追加していません。公開・再配布前にリポジトリのライセンス方針を決めてください。

