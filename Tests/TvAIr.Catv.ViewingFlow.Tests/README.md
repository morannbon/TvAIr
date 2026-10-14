# CATV視聴全経路試験
TvAIr本体を選択PlatformのReleaseでビルドした後、AIrCon 1.0.8の同じPlatformのDLLを指定して本プロジェクトをビルドしてください。
`dotnet build Tests/TvAIr.Catv.ViewingFlow.Tests -c Release -p:Platform=x64 -p:BuildProjectReferences=false -p:AIrConAssemblyPath="<AIrCon.BasicPlugin.dllの絶対パス>" -warnaserror`
生成されたEXEを実行し、終了を待ってExitCodeとsimulation-results-HYBRID.json / simulation-results-CATV.jsonを確認します。
x86はPlatformとDLLのアーキテクチャを揃えてください。
専用Windowsデスクトップを作るため、通常の対話セッションとデスクトップ作成権限が必要です。ユーザーの入力デスクトップへは切り替えません。
TVTestの代わりに、このEXEをネイティブTVTestクラスの受信窓として起動します。実TVTest・BonDriver・受信機・カード・映像/音声を使いません。
チャンネルAPI、Host Viewer API、Runtime変換、AIrCon行描画とアクションハンドラー、チューナー確保、プロセス所有権、起動・PID指定再選局、セッション、通常停止は実装コードです。
ContextのAPI参照とログ保存先は試験用に接続します。ブラウザーの実マウス入力とHTTP認証は試験対象外です。
CATV開始→別CATV局→不明/無効局の拒否→地上波→BS→停止・解放を複合/CATV専用の両構成で検証します。
試験のfixtureディレクトリとJSON/HTML記録は生成EXEの出力先に作成されます。