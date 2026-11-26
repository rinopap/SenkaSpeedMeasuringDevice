# Phase 6 手動確認手順

## 現在の確認範囲

アプリ本体とサイドカーはビルド済みです。ローカルHTTP/HTTPS試験では中継前後のメソッド、宛先、要求本文、応答本文が一致し、オリジン受信数が1、解析出力が経験値数値だけであることを確認しました。Qt画面からの起動・停止・CSV反映と、実74ENとの併用はまだ手元で確認できていません。

ここではゲームや74ENへ接続しないHTTPループバック試験を先に行います。試験データは `api_port/port` と同じ形式の架空値です。

## Qtアプリとローカルプローブ

1. `SenkaSpeedMeasuringDevice.exe` と `Phase6ProxyEngine.exe` が同じフォルダにあることを確認し、Qtアプリを起動します。
2. 既存CSVを残す場合は、事前に `exp_log.csv` と `ship_exp_log.csv` のバックアップを取ります。
3. アプリの「通信プロキシ」を開きます。待受ポートを `8877`、上流プロキシを「使用しない」にします。CA公開証明書の出力先を指定し、「同意して開始」を選びます。試験はHTTPなのでCAをWindowsへ登録する必要はありません。
4. 別配布の `SenkaSpeedPhase6-QtValidationProbe-win-x64.zip` を解凍し、その中の `Phase6QtIntegrationProbe.exe` を実行します。コマンドプロンプトまたはPowerShellで、解凍先から次を実行します。

   ```powershell
   .\Phase6QtIntegrationProbe.exe --proxy-port 8877
   ```

5. 次が出ることを確認します。

   ```text
   request_method_target_body_unchanged=True
   response_body_unchanged=True
   origin_request_count=1 (expected 1)
   PASS=True
   ```

6. アプリのフォルダにある両CSVの末尾を確認します。提督CSVに `234567`、艦娘CSVに `3210000` がそれぞれ1行ずつ追加されます。同じプローブをもう一度実行した場合は、別の通信イベントとしてそれぞれ1行ずつ追加されます。
7. 「通信プロキシ」から停止します。停止後は74EN JSON監視へ戻ります。プロキシ停止中にローカルプローブを再実行すると接続エラーになり、オリジンへ到達しないのが正常です。
8. アプリを再起動して公開CAを同じパスへ再出力し、前回と拇印が同じことを確認します。PowerShellでは次で拇印を表示できます。

   ```powershell
   $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new('.\senka-proxy-root.cer')
   $cert.Thumbprint
   ```

9. 手動でCAを信頼させた場合、作業後はWindowsのユーザー証明書ストアから一致する拇印の証明書を削除し、出力した `.cer` とユーザー別アプリデータ内の `proxy-root.pfx` も削除します。PFXを削除すると次の起動時に別のCAが生成されます。

## 実74EN試験へ進む前に

現在のサイドカーはHTTP/1.1限定です。HTTP/2有効時にローカルHTTPS経路が停止する問題を確認したため、実74EN/ゲームを本プロキシへ接続しないでください。HTTP/2経路の調査と実74ENの上流・下流互換性が未完了です。

実74EN確認が可能になった段階では、CA公開証明書の拇印確認、利用者が明示的に証明書を信頼させる操作、利用後の証明書削除、74ENでのプロキシ設定復旧を一項目ずつ案内します。本アプリは証明書ストア、システムプロキシ、74EN設定を自動変更しません。
# 旧プロキシ方式の手動検証手順（新仕様では中断）

2026-11-26に通信非仲介型取得を優先する仕様へ変更されたため、この資料の手順8以降および実74ENへのプロキシ設定は実行しないでください。ここに記載された作業は、旧Phase 6プロキシ試作の履歴です。CDP実現性の検討・確認手順は別途作成します。


