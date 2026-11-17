# native/（Windows ネイティブ版・v12-α 作業中）

C# / .NET 10。計画は [../docs/native-app-plan.md](../docs/native-app-plan.md)（§4.2 構成、§8 段階計画）。
Python 版（[../pc/](../pc/)）が今の本番で、こっちはまだどこからも使われていない。

## いま入っているもの

| プロジェクト | 中身 |
|---|---|
| `shiftlock.Core` | 依存なしのクラスライブラリ（Windows API を呼ばない。NativeAOT 対応の書き方に限る）。`Planner`（乗務日・時刻の逆算・フェーズ判定 = Python 版 `plan.py`）、`PlanConfig`（その計算に使う設定の読み込み）、`TimeUtil` |
| `shiftlock.Core.Tests` | xunit。[../docs/test-vectors/](../docs/test-vectors/) を読んで Python 版と同じ結果になるか確かめる |

まだ無いもの: 設定・状態ファイルの読み書き（`config.merged_days`・`store`）、意図判定、睡眠推定、レポート、`shl.exe`、Agent、App。

## テスト

```bash
cd native && dotnet test
```

.NET 10 SDK が要る（Ubuntu 24.04 / WSL なら `sudo apt-get install -y dotnet-sdk-10.0`）。Linux でも Windows でも同じに通る。

## Python 版との一致の確かめ方

- `docs/test-vectors/*.json` は `pc/tools/make_test_vectors.py` が Python 版から作る（手で直さない）。形はそのスクリプトの先頭に書いてある。
- Python 版のロジックを変えたら `cd pc && ../.venv/bin/python tools/make_test_vectors.py` で作り直す。古いままだと `pc/tests/test_vectors.py` が落ちる。
- 作り直したベクタで `dotnet test` が落ちたら、C# 側を Python 版に合わせて直す（Python 版が「正」）。

## 移植で Python 版と違うところ

- 設定の値が `null`（例: `"start_target": null`）のとき、Python 版は落ちるが、こっちは「書いていない」と同じ扱い（既定値）。
- `no_sleep` の `from` / `to` が無いと `FormatException`（Python 版は `KeyError`）。
