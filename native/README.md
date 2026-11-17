# native/ — ネイティブ版（C# / .NET 10）

PC 版（`pc/`，Python + PowerShell）をネイティブアプリとして作り直す場所．計画と構成は [docs/native-app-plan.md](../docs/native-app-plan.md)，仕様は [docs/pc-spec.md](../docs/pc-spec.md) を正とする．

## 構成

| プロジェクト | 役割 | 段階 |
|---|---|---|
| `shiftlock.Core` | ロジック層（逆算・フェーズ・意図判定・睡眠推定・レポート生成・設定の読み書き）．Windows API を呼ばない | v12-α |
| `shl` | CLI（`shl.exe`，NativeAOT）．Claude Code hook・コマンド・ダブルクリック用 | v12-α |
| `shiftlock.Core.Tests` | Core のテスト．`docs/test-vectors/*.json` を読んで Python 版と同じ結果になることを確かめる | v12-α |
| `shiftlock.Agent` | トレイ常駐（見張り・通知・強制・同期）．まだ無い | v12-β |
| `shiftlock.App` | ウィンドウ（WinUI 3，非パッケージ）．実行ファイル名は `shiftlock.exe`．同時に動くのは 1 つだけ．いまは殻だけ（中身は状態と記録の画面から作る） | 区切り 1 |

- フォルダ名・ソリューション名は計画書どおり小文字（`shiftlock.Core`），名前空間とアセンブリ名は C# の慣習で `Shiftlock.Core`．`shl` の名前空間は `Shiftlock.Cli`，実行ファイル名は `shl.exe`．`shiftlock.App` の名前空間は `Shiftlock.App`，実行ファイル名は `shiftlock.exe`．
- 共通設定（`net10.0`・Nullable・警告をエラー扱い）は `Directory.Build.props`．
- データは Python 版と同じ `%USERPROFILE%\shift-lock`，形式は [docs/data-format.md](../docs/data-format.md)．`shiftlock.App` は環境変数 `SHIFTLOCK_DIR` があればそのフォルダを使う（本番のコピーで確かめるとき用）．
- `shiftlock.App` は `dotnet new winui`（`--packaging Unpackaged -tpmv 10.0.17763.0 -tfm net10.0`）で作って直したもの．パッケージの版はテンプレートが固定したまま: `Microsoft.WindowsAppSDK` 2.6.5，`Microsoft.Windows.SDK.BuildTools` 10.0.28000.2957，`Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.261102100．ターゲットは `net10.0-windows10.0.26100.0`（最低 10.0.17763.0）．

## 前提

- .NET 10 SDK
- `shiftlock.App` を作り直すときだけ: `dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`（ビルドには不要）
- Visual Studio 2026 の「C++ によるデスクトップ開発」ワークロード（`shl` の NativeAOT 発行でリンカ `link.exe` が要る．ビルドとテストだけなら不要）

## コマンド

```powershell
cd native
dotnet build
dotnet test
dotnet publish shl -c Release    # → shl\bin\Release\net10.0\win-x64\publish\shl.exe

# ウィンドウを起動する（本番のフォルダに向けて試さない．コピーか空のフォルダを SHIFTLOCK_DIR に入れる）
$env:SHIFTLOCK_DIR = "<確認用のフォルダ>"
dotnet build shiftlock.App -r win-x64
.\shiftlock.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\shiftlock.exe
```

### 発行時の注意

- コマンドラインから `dotnet publish` するときは，PATH に `C:\Program Files (x86)\Microsoft Visual Studio\Installer` を足しておく．VS の `VsDevCmd.bat` が `vswhere.exe` をパス無しで呼び，見つからないときのエラー文が NativeAOT のリンカ探索の出力に混ざってリンクが失敗する．Visual Studio から発行する場合は不要．
- `shl.csproj` の `KnownILCompilerPack` は，SDK 10.0.401 が暗黙参照する ILCompiler 10.0.12 の脆弱性警告（NU1903）を避けるための上書き．SDK を更新したら外してよい．
- hook（UserPromptSubmit）から呼ばれる `shl.exe` は失敗時に exit 2 を返してはいけない（プロンプトが消える）．
