# TvAIr Alpha 1.0.7

TvAIr は、TVTest 環境を利用して、番組表表示、録画予約、自動検索予約、プラグイン連携を行うアプリケーションです。

Alpha 1.0.7 は公開用の更新版です。使用する場合は、利用者自身の責任で動作環境、設定内容、録画結果を確認してください。

## 主な機能

- 番組表表示
- 録画予約
- 自動検索予約
- プラグイン対応
- ライト / ダークテーマ

## 必要動作環境

### 実行環境

- Windows 10 / 11
- .NET 8 Desktop Runtime
- Microsoft Visual C++ 再頒布可能パッケージ
- TVTest 0.10.0 以降で単体視聴・録画できる環境
- B25 デコーダーまたは復号に必要な環境
- 使用する BonDriver
- `.ch2` ファイル
- `ChSet.txt`
- 録画保存先

### ビルド環境

- Visual Studio 2022 以降
- .NET 8 SDK
- .NET 8 SDK 対応の Visual Studio 構成
- C++ によるデスクトップ開発

## ビルド方法

Visual Studio 2022 以降で `TvAIr.sln` を開きます。

対応するビルド構成は次の通りです。

- `Release | x86`
- `Release | x64`

ビルド後の出力先は次の通りです。

```text
TvAIr/bin/x86/Release/net8.0-windows/
TvAIr/bin/x64/Release/net8.0-windows/
```

## 初期設定

1. ZIP を任意のフォルダに展開します。
2. `B25Decoder.dll` を `TvAIr.exe` と同じフォルダに配置します。
3. `TvAIr.exe` を起動します。
4. 設定画面で BonDriver、`.ch2`、`ChSet.txt`、録画保存先を指定します。
5. 番組表を取得します。

## appsettings.example.json について

`TvAIr/appsettings.example.json` は公開用の設定例です。

実際の環境設定は、必要に応じて `TvAIr/appsettings.json` を作成して行います。ただし、`TvAIr/appsettings.json` には利用者環境の値が入る可能性があるため、Git 管理対象にしません。

公開リポジトリでは、設定例の正本を `appsettings.example.json` とします。

## 同梱ファイル

- `README.txt` — 配布ZIP向け説明
- `README.md` — GitHub表示向け説明
- `Regex_Manual.txt` — 自動検索予約向け正規表現マニュアル
- `RELEASE_NOTES.txt` — リリースノート
- `LICENSE` — ライセンス

## 既知の制限と注意

- Alpha 版のため、仕様や画面構成は今後変更される場合があります。
- TvAIr は TVTest、BonDriver、復号環境、チャンネル設定に依存します。
- 録画前には、利用者自身の環境で視聴・録画できることを確認してください。
- 録画結果、設定内容、既存環境への影響について、作者は責任を負いません。

## ライセンス

このリポジトリのライセンスは `LICENSE` を確認してください。

TVTest、BonDriver、B25 デコーダー、各プラグインなど、TvAIr 以外のソフトウェアについては、それぞれの配布元・権利者のライセンスに従ってください。
