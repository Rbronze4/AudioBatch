# AudioBatch

MP4などの動画を、まとめてMP3に変換するWindows用ツールです。
フォルダーごとの追加、中断・再開、失敗したファイルの再試行に対応しています。

## 使い方

Windows 10 / 11（64bit）、.NET Framework 4.8が必要です。

1. このフォルダーの `build.bat` をダブルクリックします。
2. [FFmpegのWindows版](https://www.gyan.dev/ffmpeg/builds/)の **release essentials ZIP** を取得して展開します。
3. このフォルダーに `bin` フォルダーを作り、取得した `ffmpeg.exe` と `ffprobe.exe` を入れます。
4. 作成された `AudioBatch.exe` を起動します。
5. ファイルやフォルダーを追加し、モード・保存先を選んで「変換開始 / 再開」を押します。

```text
AudioBatch.exe
bin/
  ffmpeg.exe
  ffprobe.exe
```

## 音質について

- **MP3モード**：320kbpsで変換します。元音声がMP3なら再圧縮せずコピーします。
- **無劣化抽出**：元の音声データをそのままM4A・MP3・MKAへ保存します。

**MP3への再圧縮は劣化ゼロにはできません。音質をそのまま保ちたい場合は「無劣化抽出」を選んでください。**

入力はMP4・M4V・MOV、対象は最初の音声トラックです。3ch以上の音声は無劣化抽出を使用してください。
元ファイルや既存の出力は上書きしません。変換はPC内で行います。

## GitHubに載せるファイル

`README.md`、`AudioBatch.cs`、`build.bat`、`.gitignore`、`LICENSE` の5つだけでOKです。
実行ファイル・FFmpeg・変換履歴・動画や音声は含めません。

## ライセンス

AudioBatch本体はMIT Licenseです。FFmpegは本体に含めず、別途取得します。
FFmpegには、その配布物のライセンスが適用されます。
