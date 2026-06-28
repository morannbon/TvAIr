TvAIr Alpha 1.0.0 README

1. TvAIr概要

TvAIr は、TVTest 環境を利用して番組表表示、録画予約、自動検索予約、プラグイン連携を行うアプリケーションです。

主な機能:
- 番組表表示
- 録画予約
- 自動検索予約
- プラグイン対応
- ライト / ダークテーマ

2. 必要動作環境

実行環境:
- Windows 10 / 11
- .NET 8 Desktop Runtime
- Microsoft Visual C++ 再頒布可能パッケージ
- TVTest 0.10.0以降で単体視聴・録画できる環境
- B25 デコーダーまたは復号に必要な環境
- 使用する BonDriver
- .ch2 ファイル
- ChSet.txt
- 録画保存先

ビルド環境:
- Visual Studio 2022 以降
- .NET 8 SDK
- .NET 8 SDK 対応の Visual Studio 構成
- C++によるデスクトップ開発

3. ビルド方法

Visual Studio 2022 以降で TvAIr.sln を開きます。

ビルド構成:
- Release | x86
- Release | x64

ビルド後の出力先:

Release | x86:
.\TvAIr\bin\x86\Release\net8.0-windows\

Release | x64:
.\TvAIr\bin\x64\Release\net8.0-windows\

4. インストール方法

1. ZIP を任意のフォルダに展開します。
2. B25Decoder.dll を TvAIr.exe と同じフォルダに配置します。
3. TvAIr.exe を起動します。
4. 設定画面で BonDriver、.ch2、ChSet.txt、録画保存先を指定します。
5. 番組表を取得します。

5. 同梱ファイル

- README.txt
- Regex_Manual.txt
- RELEASE_NOTES.txt

6. 免責事項

TvAIr Alpha 1.0.0 は開発中の試験版です。

本ソフトウェアの使用により発生した、録画失敗、録画データの欠損、設定ファイルやデータベースの破損、既存環境への影響、その他いかなる損害についても、作者は責任を負いません。

使用する場合は、利用者自身の責任で動作環境、設定内容、録画結果を確認してください。

7. ライセンス

TvAIr のライセンスは、配布元のライセンス表記に従います。

TVTest、BonDriver、B25 デコーダー、各プラグインなど、TvAIr 以外のソフトウェアについては、それぞれの配布元・権利者のライセンスに従ってください。
