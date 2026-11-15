# PTCGL 日本語化 Mod（ptcgl-ja）

ポケモンカードゲーム ライブ（Pokémon TCG Live、Windows版）の表示を日本語化する非公式Modです。公式のModではありません。

ゲーム本体のデータを直接書き換えるのではなく、BepInExプラグインが表示の直前で差し替える方式です。

- システム文（メニューやボタンなど）は言語テーブルの辞書差し替えで全件日本語化
- カード名・ワザ名・効果文は公式の日本語カードの原文を使った辞書で差し替え
- カードの絵の面は表示直前に日本語版カード画像へ差し替え（画像は同梱せず、初回表示時にTCGdexとLimitless TCGの公開データから取得してキャッシュし、中身のMD5照合に合格したものだけを表示）

## 対応環境と現状（2026-11-15時点）

- 対応バージョン: Pokémon TCG Live 1.44.0.0 / BepInEx 5.4.23.6
- カード画像の差し替え（JaCards v2.5）: 対応表 11,271件、検証済み 11,253件（全 15,201件の約74.0%）
- 未対応のカード、基本エネルギーの通常デザイン、プロモの一部は英語のまま表示されます
- 図鑑（CardDex）画面では、別のカードが表示される問題の回避のため、顔の差し替えを一時的に止めています（テキストは日本語のままです）
- v2.5で、対戦中に別のカードの顔が表示される問題を修正しました。そのカード自身の顔だと確認できた場合だけ差し替えます

## 導入方法

導入手順の詳細は [docs/install-guide.html](docs/install-guide.html) を見てください。

大まかな流れは次のとおりです。

1. ゲームを終了しておきます
2. 配布パッケージの `install.bat` を実行します（このリポジトリの `install.ps1` / `install.bat` はパッケージ内のスクリプトと同じものです）
3. ゲームを起動します

元に戻すには、ゲームのフォルダから `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx` フォルダを削除してください。

## リポジトリの構成

- `src/JaCardsV25.cs` — カード画像の差し替えプラグイン（v2.5）
- `src/JaFont13.cs` — 日本語フォント用プラグイン（v1.3）
- `src/zhmod-mod-source/` — ベースModの改変版ソース一式
- `data/imgmap.json` — カード画像の対応表（想定MD5付き）
- `data/databases/` — カード名・ワザ名・効果文の辞書
- `data/text/all.txt` — システム文の辞書
- `docs/install-guide.html` — 導入手引書

ビルド済みのDLLとBepInEx本体はこのリポジトリには含めていません。

## クレジットとライセンス

- ライセンス: GPL-3.0（`LICENSE` を見てください）
- ベースのMod: [Hill-98/ptcg-live-zh-mod](https://github.com/Hill-98/ptcg-live-zh-mod) v0.2.10（GPL-3.0）。日本語化にあたって辞書とフォントの読み込みを変更しています
- Mod読み込みの仕組み: [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.6
- カードのデータと画像の対応付け: [TCGdex](https://www.tcgdex.net) / [Limitless TCG](https://limitlesstcg.com) の公開データ

## 注意

- 対戦相手には影響しません。表示が変わるのは自分の画面だけです
- ゲームのアップデートで動かなくなることがあります。その場合は一度外してから起動を確認してください
