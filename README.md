# reuse-win-cli

> **非公式のツールです。NVIDIA とは関係なく、NVIDIA が公認・推奨するものでもありません。**
> Unofficial. Not affiliated with or endorsed by NVIDIA.

NVIDIA の音声補正モデル [RE-USE](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)（NVIDIA NemotronLabs RE-USE）を、Windows で手軽に使うためのコマンドラインツールです。
録音のノイズ・残響・こもりなどを取り除き、声を聞き取りやすくします。

公式の実装は Linux と NVIDIA の GPU（CUDA）が必要ですが、このツールは推論を Direct3D 11 のコンピュートシェーダー（HLSL）で書き直しているので、
**NVIDIA・AMD・Intel のどの GPU でも動きます**。GPU が使えない PC でも、CPU で（時間はかかりますが）動きます。
モデルの重みは exe に入っているので、**exe 1 つをダウンロードするだけで使えます**。Python や .NET のインストールは要りません。

[English](#english)

## 使い方

### ドラッグ＆ドロップ

1. [Releases](../../releases) から `reuse-win-cli.exe` をダウンロードします。
2. 音声ファイルや動画ファイルを `reuse-win-cli.exe` のアイコンにドラッグ＆ドロップします（複数のファイルを一度に落としてもかまいません）。
3. 元のファイルと同じフォルダに `<元の名前>_enhanced.wav` ができます。

### 初めて起動するときの警告について

このツールの exe にはコード署名が付いていないので、初めて起動するときに Windows の SmartScreen が「Windows によって PC が保護されました」という画面を出して止めることがあります。
そのときは、画面の **「詳細情報」** をクリックし、表示される **「実行」** ボタンを押してください。

ダウンロードしたファイルが本物かどうかは、Releases にある `SHA256SUMS.txt` の値と、PowerShell の `Get-FileHash reuse-win-cli.exe` の結果を比べて確かめられます。

### コマンドライン

```powershell
reuse-win-cli <入力ファイル>... [オプション]
```

| オプション | 説明 |
|---|---|
| `-o`, `--output <パス>` | 出力先のファイル（入力が 1 つのときだけ） |
| `-s`, `--strength <0-100>` | 補正の強さ（%）。元の音と混ぜる割合。既定は 100 |
| `-y`, `--overwrite` | 出力先に同じ名前のファイルがあれば上書きする（指定しなければ、自動で決めた名前には `_enhanced (2).wav` のように番号を付け、`-o` で指定した名前ならエラーにする） |
| `--cpu` | GPU を使わずに処理する |
| `--model <パス>` | exe に入っている重みの代わりに使う RE-USE の重み（`.safetensors`） |
| `--license` | ライセンスを表示する |
| `-v`, `--version` | バージョンを表示する |
| `-h`, `--help` | 使い方を表示する |

例:

```powershell
reuse-win-cli 録音.m4a
reuse-win-cli 録音.mp4 -o きれいな声.wav -s 80
```

## 入力と出力

- **入力**: wav・mp3・m4a・aac・mp4・mov・wma・flac など、Windows（Media Foundation）が読める形式。動画は最初の音声トラックを使います。
- **出力**: 32bit float の WAV。サンプリングレートとチャンネル数は入力と同じです（入力が 8kHz〜48kHz の範囲外なら、範囲内に変換します）。
  動画の音声だけを書き出すので、動画に戻すときは動画編集ソフトで差し替えてください。
- WAV の大きさの上限（4GB）があるので、48kHz ステレオで約 3 時間を超える音声は書き出せません。
- ステレオなどの複数チャンネルは、チャンネルごとに補正します。

## 動作環境

- Windows 10 / 11（64bit）。N エディションでは「メディア機能パック」（Media Feature Pack）が必要です。
- Direct3D 11（機能レベル 11.0）に対応した GPU（NVIDIA・AMD・Intel のどれでも）。GPU が複数ある PC（内蔵 GPU と外付け GPU のノート PC など）では、いちばん性能の高い GPU を使います（Windows の「設定 > ディスプレイ > グラフィックス」でこのツールに「省電力」を選んでも、性能の高い GPU を使います）。
- GPU が使えない場合は CPU（WARP）で処理します。GPU に比べて大幅に遅く、音声の長さの 20 倍以上かかることがあります。

## 処理の中身

公式の推論（`inference_chunk.py`）と同じく、音声を一定の長さ（最大 5 秒）の区間に分けて、半分ずつ重ねて推論し、Hann 窓で混ぜます。
区間の長さは GPU のメモリーに合わせて短くします。区間の長さが変わると、区間の境目の結果がわずかに変わります。

推論は公式の PyTorch の実装と比べて検証しています（float64 の PyTorch の出力に対して 44.1kHz で 86.8dB、8kHz で 112dB の SNR。公式の fp32 の CUDA の実装自身は 55.8dB・68.7dB）。

## ライセンス

- このツールのソースコード: [MIT License](LICENSE)
- exe に入っている RE-USE の重み: NVIDIA の [OpenMDW License Agreement, version 1.1](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)
- そのほかのライブラリ: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

`reuse-win-cli --license` でも表示できます。

RE-USE のモデルの構造は [SEMamba](https://github.com/RoyChao19477/SEMamba)（MP-SENet・CMGAN をもとにしたもの）に基づいています。

## 開発

### ビルド

必要なもの:

- .NET 10 SDK
- Windows SDK（シェーダーをコンパイルする `fxc.exe`）
- Visual Studio の「C++ によるデスクトップ開発」（Native AOT でのリンクに使う）

```powershell
dotnet publish src/ReuseWinCli/ReuseWinCli.csproj -c Release -o publish
```

`publish/reuse-win-cli.exe` ができます。初回のビルドでは、RE-USE の重みを Hugging Face から `.cache/` にダウンロードします（リビジョンを固定し、SHA256 で中身を確かめます）。
手元の重みを使うときは `-p:ReuseModelPath=<絶対パス>` を付けます（相対パスはプロジェクトのフォルダ `src/ReuseWinCli` が基準になります）。

`'vswhere.exe' は、内部コマンドまたは外部コマンド…として認識されていません` というエラーでリンクに失敗するときは、`C:\Program Files (x86)\Microsoft Visual Studio\Installer` を `PATH` に足してからビルドしてください。

### テスト

```powershell
dotnet test
```

GPU の各カーネルを CPU の参照実装と比べるテストと、区間の分け方・混ぜ方のテストが走ります。

公式の PyTorch の出力と比べるテストは、環境変数 `REUSE_FIXTURE_DIR` に fixture のフォルダを指定したときだけ走ります。
fixture は、公式の実装で同じ入力を処理して書き出した生の float32 の列（リトルエンディアン）です。

| ファイル | 中身 |
|---|---|
| `input.bin` / `input_8000.bin` | 入力の音声（44.1kHz / 8kHz、モノラル） |
| `enhanced64.bin` / `enhanced64_8000.bin` | 公式の実装を float64 で動かした出力 |
| `enc_out.bin` / `block0_out.bin` / `stack_out.bin` | 44.1kHz の入力でのエンコーダーの出力・TF-Mamba の 1 段目の出力・30 段の出力（`[時間][周波数][チャンネル]` の並び。形は 257 × 442 × 64） |

---

## English

reuse-win-cli is an unofficial Windows command-line tool for NVIDIA's [RE-USE](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE) speech enhancement model.
It removes noise, reverberation and other degradations from speech recordings.

The official implementation requires Linux and an NVIDIA GPU (CUDA). This tool reimplements the inference with Direct3D 11 compute shaders (HLSL), so it **runs on any NVIDIA, AMD or Intel GPU**, and falls back to the CPU (slowly) when no GPU is available.
The model weights are embedded, so **it is a single exe** — no Python or .NET installation is needed.

### Usage

Download `reuse-win-cli.exe` from [Releases](../../releases) and drag and drop audio or video files onto it.
`<original name>_enhanced.wav` is written next to each input.

Windows SmartScreen may block the first launch because the exe is not code-signed. Click **"More info"** and then **"Run anyway"**.
You can verify the download by comparing `Get-FileHash reuse-win-cli.exe` with `SHA256SUMS.txt` in the release.

```powershell
reuse-win-cli <input file>... [options]
```

| Option | Description |
|---|---|
| `-o`, `--output <path>` | Output file (only with a single input) |
| `-s`, `--strength <0-100>` | Enhancement strength in percent (mix with the original). Default: 100 |
| `-y`, `--overwrite` | Overwrite an existing output file (otherwise a number is appended to automatic names, and an explicit `-o` path is an error) |
| `--cpu` | Process without the GPU |
| `--model <path>` | Use these RE-USE weights (`.safetensors`) instead of the built-in ones |
| `--license` | Show license information |
| `-v`, `--version` | Show the version |
| `-h`, `--help` | Show help |

- **Input**: anything Windows Media Foundation can decode (wav, mp3, m4a, aac, mp4, mov, wma, flac, ...). For videos, the first audio track is used.
- **Output**: 32-bit float WAV with the same sample rate and channel count as the input (rates outside 8–48 kHz are converted).
- WAV files are limited to 4 GB, so audio longer than about 3 hours (48 kHz stereo) cannot be written.
- **Requirements**: Windows 10/11 (64-bit; N editions need the Media Feature Pack) and a Direct3D 11 (feature level 11.0) GPU. On PCs with several GPUs (e.g. laptops with integrated and discrete GPUs), the highest-performance GPU is used, even if "Power saving" is selected for this tool in Windows graphics settings. Without a GPU, processing can take more than 20× the audio length.

### License

- Source code: [MIT License](LICENSE)
- Embedded RE-USE weights: NVIDIA [OpenMDW License Agreement, version 1.1](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)
- Other components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

The RE-USE architecture is based on [SEMamba](https://github.com/RoyChao19477/SEMamba) (derived from MP-SENet and CMGAN).
