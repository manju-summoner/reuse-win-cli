# reuse-win-cli

> **非公式のツールです。NVIDIA とは無関係で、NVIDIA が公認・推奨するものでもありません。**
> Unofficial. Not affiliated with or endorsed by NVIDIA.

NVIDIA の音声補正モデル [RE-USE](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)（NVIDIA NemotronLabs RE-USE）を、Windows で手軽に使うためのコマンドラインツールです。
録音のノイズ・残響・こもりなどを除去し、声を聞き取りやすくします。

公式の実装は Linux と NVIDIA の GPU（CUDA）が必要ですが、このツールは推論を Direct3D 11 のコンピュートシェーダー（HLSL）で再実装しているので、
**NVIDIA・AMD・Intel のどの GPU でも動作します**。GPU が使えない PC でも、CPU で（時間はかかりますが）動作します。
モデルの重みは exe に同梱されているので、**exe 1 つをダウンロードするだけで使えます**。Python や .NET のインストールは不要です。

[English](#english)

## 使い方

### ドラッグ＆ドロップ

1. [Releases](../../releases) から `reuse-win-cli.exe` をダウンロードします。
2. 音声ファイルや動画ファイルを `reuse-win-cli.exe` のアイコンにドラッグ＆ドロップします（複数のファイルをまとめてドラッグ＆ドロップすることもできます）。
3. 元のファイルと同じフォルダに `<元のファイル名>_enhanced.wav` が出力されます。

### 初回起動時の警告について

このツールの exe にはコード署名がないため、初回起動時に Windows の SmartScreen が「Windows によって PC が保護されました」という画面を表示して、実行をブロックすることがあります。
その場合は、画面の **「詳細情報」** をクリックし、表示される **「実行」** ボタンを押してください。

ダウンロードしたファイルが改ざんされていないかは、Releases にある `SHA256SUMS.txt` の値と、PowerShell の `Get-FileHash reuse-win-cli.exe` の結果を比較して確認できます。

### コマンドライン

```powershell
reuse-win-cli <入力ファイル>... [オプション]
```

| オプション | 説明 |
|---|---|
| `-o`, `--output <パス>` | 出力先のファイル（入力が 1 つの場合のみ）。`-` で標準出力 |
| `-s`, `--strength <0-100>` | 補正の強さ（%）。元の音声と混合する割合。既定値は 100 |
| `-y`, `--overwrite` | 出力先に同名のファイルがあれば上書きする（指定しない場合、自動で決めたファイル名には `_enhanced (2).wav` のように連番を付与し、`-o` で指定したファイル名ならエラーにする） |
| `--cpu` | GPU を使わずに処理する |
| `--model <パス>` | exe に同梱されている重みの代わりに使用する RE-USE の重み（`.safetensors`） |
| `--license` | ライセンスを表示する |
| `-v`, `--version` | バージョンを表示する |
| `-h`, `--help` | 使い方を表示する |

例:

```powershell
reuse-win-cli 録音.m4a
reuse-win-cli 録音.mp4 -o きれいな声.wav -s 80
```

### 標準入出力（パイプ）

入力に `-` を指定すると標準入力から読み込み、`-o -` を指定すると標準出力へ書き出します。入力が標準入力の場合、`-o` を省略すると出力先は標準出力になります。
ffmpeg などのツールとパイプでつないで使えます。処理は前から順に進み、チャンクを処理するたびに出力します。

| オプション | 説明 |
|---|---|
| `--in-format <形式>` | 標準入力の形式。`wav`（既定）・`f32le`・`s16le` |
| `--rate <Hz>` | 標準入力が `f32le`・`s16le` の場合のサンプリングレート（8000〜48000） |
| `--channels <数>` | 標準入力が `f32le`・`s16le` の場合のチャンネル数（1〜32） |
| `--out-format <形式>` | 出力の形式。`wav`（既定）・`f32le`（32bit float のリトルエンディアン。ファイルに出力する場合の既定の拡張子は `.raw`） |

例（cmd.exe）:

```bat
ffmpeg -i in.mp4 -f f32le -ac 2 -ar 48000 - | reuse-win-cli - --in-format f32le --rate 48000 --channels 2 --out-format f32le | ffmpeg -f f32le -ar 48000 -ac 2 -i - out.flac
```

- Windows PowerShell 5.1（と 7.4 より前の PowerShell）は、外部コマンドの間のパイプやリダイレクトでバイナリを壊すので、cmd.exe か PowerShell 7.4 以降で実行してください。
- 標準入出力がコンソールに接続されたまま（リダイレクトしていない）の場合は、エラーになります。
- 標準入力の WAV は、16/24/32bit の PCM と 32bit float に対応しています。mp4 などのコンテナーは標準入力からは読み込めないので、ffmpeg で WAV か raw に変換してから渡してください。
- 標準入力のサンプリングレートが 8kHz〜48kHz の範囲外の場合はエラーになります（ファイルの入力と違い、変換しません）。
- パイプへ書き出す WAV は、全体の長さが分からないので、ヘッダーの長さの欄を「不明」（0xFFFFFFFF）にします。ffmpeg や sox はそのまま読み込めます。cmd.exe で標準出力をファイルにリダイレクトした場合は、正しい長さを書き込みます（PowerShell のリダイレクトはパイプを経由するので、長さは「不明」になります。ファイルに保存する場合は `-o` を推奨します）。
- 入力元がデータを送らないまま止まっている場合も、Ctrl+C で中止できます。
- 標準出力に音声を書き出す場合、進捗などの表示はすべて標準エラーに出力します。
- RE-USE は前後の音声を使うモデルなので、出力は入力より約 5 秒（チャンクの長さ）＋推論の時間だけ遅れます。
- 出力先のプログラムが先に終了してパイプが閉じられた場合は、そこで処理を終了します。

## 入力と出力

- **入力**: wav・mp3・m4a・aac・mp4・mov・wma・flac など、Windows（Media Foundation）がデコードできる形式。動画は最初の音声トラックを使用します。
- **出力**: 32bit float の WAV。サンプリングレートとチャンネル数は入力と同じです（入力が 8kHz〜48kHz の範囲外の場合は、範囲内に変換します）。
  動画の音声だけを出力するので、動画に戻す場合は動画編集ソフトで音声を差し替えてください。
- WAV のファイルサイズの上限（4GB）があるため、48kHz ステレオで約 3 時間を超える音声は出力できません。
- ステレオなどの複数チャンネルは、チャンネルごとに補正します。

## 動作環境

- Windows 10 / 11（64bit）。N エディションでは「メディア機能パック」（Media Feature Pack）が必要です。
- Direct3D 11（機能レベル 11.0）に対応した GPU（NVIDIA・AMD・Intel のどれでも）。GPU が複数ある PC（内蔵 GPU と外付け GPU を搭載したノート PC など）では、最も性能の高い GPU を使用します（Windows の「設定 > ディスプレイ > グラフィックス」でこのツールに「省電力」を選択しても、性能の高い GPU を使用します）。
- GPU が使えない場合は CPU（WARP）で処理します。GPU に比べて大幅に遅く、音声の長さの 20 倍以上の時間がかかることがあります。

## 処理の内容

公式の推論（`inference_chunk.py`）と同じく、音声を一定の長さ（最大 5 秒）のチャンクに分割し、半分ずつ重ねて推論して、Hann 窓で合成します。
チャンクの長さは GPU のメモリーに合わせて短くします。チャンクの長さが変わると、チャンクの境界付近の結果がわずかに変わります。

推論は公式の PyTorch の実装と比較して検証しています（float64 の PyTorch の出力に対して、44.1kHz で 86.8dB、8kHz で 112dB の SNR。公式の fp32 の CUDA の実装自体は 55.8dB・68.7dB）。

## ライセンス

- このツールのソースコード: [MIT License](LICENSE)
- exe に同梱されている RE-USE の重み: NVIDIA の [OpenMDW License Agreement, version 1.1](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)
- その他のライブラリ: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

`reuse-win-cli --license` でも表示できます。

RE-USE のモデルの構造は [SEMamba](https://github.com/RoyChao19477/SEMamba)（MP-SENet・CMGAN から派生したもの）に基づいています。

## 開発

### ビルド

必要なもの:

- .NET 10 SDK
- Windows SDK（シェーダーをコンパイルする `fxc.exe`）
- Visual Studio の「C++ によるデスクトップ開発」（Native AOT のリンクに使用）

```powershell
dotnet publish src/ReuseWinCli/ReuseWinCli.csproj -c Release -o publish
```

`publish/reuse-win-cli.exe` が生成されます。初回のビルドでは、RE-USE の重みを Hugging Face から `.cache/` にダウンロードします（リビジョンを固定し、SHA256 で内容を検証します）。
ローカルの重みを使用する場合は `-p:ReuseModelPath=<絶対パス>` を指定します（相対パスはプロジェクトのフォルダ `src/ReuseWinCli` が基準になります）。

`'vswhere.exe' は、内部コマンドまたは外部コマンド…として認識されていません` というエラーでリンクに失敗する場合は、`C:\Program Files (x86)\Microsoft Visual Studio\Installer` を `PATH` に追加してからビルドしてください。

### テスト

```powershell
dotnet test
```

GPU の各カーネルを CPU の参照実装と比較するテストと、チャンクの分割・合成のテストが実行されます。

公式の PyTorch の出力と比較するテストは、環境変数 `REUSE_FIXTURE_DIR` に fixture のフォルダを指定した場合のみ実行されます。
fixture は、公式の実装で同じ入力を処理して出力した float32 の生データ（リトルエンディアン）です。

| ファイル | 内容 |
|---|---|
| `input.bin` / `input_8000.bin` | 入力の音声（44.1kHz / 8kHz、モノラル） |
| `enhanced64.bin` / `enhanced64_8000.bin` | 公式の実装を float64 で実行した出力 |
| `enc_out.bin` / `block0_out.bin` / `stack_out.bin` | 44.1kHz の入力に対するエンコーダーの出力・TF-Mamba の 1 段目の出力・30 段の出力（`[時間][周波数][チャンネル]` の順。形状は 257 × 442 × 64） |

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

Use `-` as the input to read standard input, and `-o -` to write standard output (standard input defaults to standard output). Options: `--in-format wav|f32le|s16le` (default `wav`), `--rate` and `--channels` (required for `f32le`/`s16le` input), `--out-format wav|f32le` (default `wav`).

```bat
ffmpeg -i in.mp4 -f f32le -ac 2 -ar 48000 - | reuse-win-cli - --in-format f32le --rate 48000 --channels 2 --out-format f32le | ffmpeg -f f32le -ar 48000 -ac 2 -i - out.flac
```

Use cmd.exe or PowerShell 7.4+ (Windows PowerShell 5.1 corrupts binary data in pipes). WAV written to a pipe uses "unknown" (0xFFFFFFFF) sizes; redirecting to a file in cmd.exe writes the correct sizes. With `--out-format f32le`, the default output extension is `.raw`. Messages go to standard error when audio is written to standard output. The output lags the input by about 5 seconds (one chunk) plus the inference time.

- **Input**: anything Windows Media Foundation can decode (wav, mp3, m4a, aac, mp4, mov, wma, flac, ...). For videos, the first audio track is used.
- **Output**: 32-bit float WAV with the same sample rate and channel count as the input (rates outside 8–48 kHz are converted).
- WAV files are limited to 4 GB, so audio longer than about 3 hours (48 kHz stereo) cannot be written.
- **Requirements**: Windows 10/11 (64-bit; N editions need the Media Feature Pack) and a Direct3D 11 (feature level 11.0) GPU. On PCs with several GPUs (e.g. laptops with integrated and discrete GPUs), the highest-performance GPU is used, even if "Power saving" is selected for this tool in Windows graphics settings. Without a GPU, processing can take more than 20× the audio length.

### License

- Source code: [MIT License](LICENSE)
- Embedded RE-USE weights: NVIDIA [OpenMDW License Agreement, version 1.1](https://huggingface.co/nvidia/NVIDIA-NemotronLabs-RE-USE)
- Other components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

The RE-USE architecture is based on [SEMamba](https://github.com/RoyChao19477/SEMamba) (derived from MP-SENet and CMGAN).
