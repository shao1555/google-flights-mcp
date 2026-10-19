# google-flights-mcp

Google Flights で航空券を検索する、ローカル用の MCP サーバー（stdio）。
[shadyvb/mcp-skyscanner](https://github.com/shadyvb/mcp-skyscanner)（Python）を参考に C# で作ったもの。
Skyscanner は bot 対策が強く安定して取れないので、検索先を Google Flights にしている。
言語・地域・通貨は日本（日本語・日本市場・日本円）に固定している。

> [!IMPORTANT]
> **非公式**のツールです。Google LLC とは一切関係がなく、承認や支援も受けていません。
> **無保証**です。取得した価格・時刻が正確である保証はありません。予約の前に航空会社・予約サイトで確認してください。
> このツールの利用によって生じたいかなる損害についても、作者は責任を負いません。

## ツール

| ツール | 内容 |
|---|---|
| `search_airports` | 空港と都市を名前・IATA コードで検索する。都市には近くの空港の一覧が付く |
| `search_flights` | 片道・往復の航空券を検索する。便ごとに価格、航空会社、出発/到着日時、所要時間、乗り継ぎ、区間ごとの便名・機材・足元の広さ、CO2 排出量を返す |
| `search_cheapest_dates` | 出発日ごとの最安値（価格カレンダー）を返す。往復は滞在日数を指定する |

`search_flights` の主な引数:

| 引数 | 既定 | 内容 |
|---|---|---|
| `from` / `to` | （必須） | IATA コード（`HND`）、都市の ID（`/m/07dfk`）、または地名（`札幌`）。カンマ区切りで複数指定できる（`HND,NRT`） |
| `date` | （必須） | 出発日 `YYYY-MM-DD` |
| `return_date` | なし | 復路の日付。指定すると往復 |
| `outbound_flight_id` | なし | 往復で復路を検索するときに、前回の結果から選んだ往路の `id` |
| `adults` / `children` / `infants_in_seat` / `infants_on_lap` | 1 / 0 / 0 / 0 | 人数（合計 9 人まで） |
| `cabin` | `economy` | `economy`, `premium_economy`, `business`, `first` |
| `max_stops` | 制限なし | 乗り継ぎ回数の上限（`0` = 直行便のみ） |
| `airlines` | なし | 航空会社の IATA コード（`JL`, `NH`）かアライアンス（`STAR_ALLIANCE`, `ONEWORLD`, `SKYTEAM`） |
| `sort` | `best` | `best`（Google のおすすめ順）, `price`, `departure`, `arrival`, `duration` |
| `limit` | 20 | 返す便の数（最大 100） |

都市を指定すると、その都市の空港をまとめて検索する（東京なら羽田と成田）。
地名を渡したときは入力補完の先頭の候補（空港か都市）を使う。

往復は Google Flights の画面と同じく 2 段階で検索する。

1. `return_date` を付けて検索すると、往路の一覧が返る。`price` はその往路を使う往復の最安合計
2. 選んだ往路の `id`（例: `JL501/HND-CTS/2026-11-20`）を `outbound_flight_id` に入れてもう一度検索すると、組み合わせられる復路の一覧が返る。`price` は往復の合計

### mcp-skyscanner からの変更点

- 検索先を Skyscanner から Google Flights に変えた
- ロケール・通貨・地域を環境変数で切り替えるのをやめ、日本（`hl=ja`, `gl=JP`, `curr=JPY`）に固定した
- 検索結果の生データ（buckets）をそのまま返すのをやめ、便・区間・乗り継ぎ・価格を整理した JSON にして返す
- 都市・地名での検索、子供・幼児の人数、乗り継ぎ回数・航空会社での絞り込み、復路の検索を追加した
- 日付ごとの最安値を調べる `search_cheapest_dates` を追加した

## インストール

[Releases](https://github.com/shao1555/google-flights-mcp/releases) から使う環境のアーカイブを落として展開する。
.NET ランタイムを含む単一バイナリなので、ほかに必要なものはない。

| 環境 | ファイル |
|---|---|
| macOS（Apple Silicon） | `google-flights-mcp-osx-arm64.tar.gz` |
| Linux（x64） | `google-flights-mcp-linux-x64.tar.gz` |
| Windows（x64） | `google-flights-mcp-win-x64.zip` |

macOS でブラウザから落とした場合は、Gatekeeper に止められないよう隔離属性を外す。

```sh
xattr -d com.apple.quarantine google-flights-mcp
```

Linux では libicu と tzdata が要る（多くのディストリビューションでは最初から入っている）。

## ビルド

ソースからビルドする場合:

```sh
mise install          # .NET SDK 10
mise run publish      # ./dist/google-flights-mcp（自己完結型の単一バイナリ）を作る
```

`dist/google-flights-mcp` は .NET ランタイムがなくても起動するので、MCP クライアントにはこのバイナリを登録する。
mise で入れた dotnet は `DOTNET_ROOT` を設定しないため、`bin/` 配下の apphost を直接指定すると起動しない。

`v*` のタグを push すると、GitHub Actions が 3 環境向けにビルドし、スモークテスト（MCP の initialize と tools/list）を通してから Release を作る。

## 登録

MCP クライアントには、バイナリの場所を絶対パスで登録する。

- Releases から落とした場合: 展開した `google-flights-mcp`（Windows は `google-flights-mcp.exe`）のパス
- ソースからビルドした場合: リポジトリ直下で `$PWD/dist/google-flights-mcp`

以下の例では、バイナリのパスを `BIN` に入れておく。

```sh
BIN="$HOME/bin/google-flights-mcp"          # Releases から落として ~/bin に置いた場合
# BIN="$PWD/dist/google-flights-mcp"        # ソースからビルドした場合（リポジトリ直下で）
```

### Claude Code

```sh
claude mcp add google-flights --scope user -- "$BIN"
```

### Codex

```sh
codex mcp add google-flights -- "$BIN"
```

`codex mcp add` を使わない場合は、`~/.codex/config.toml` に直接書いてもよい。

```toml
[mcp_servers.google-flights]
command = "/absolute/path/to/google-flights-mcp"
```

## しくみ

公開 API はないので、ブラウザ版の Google Flights が使っている内部 API を呼んでいる。

| 用途 | API |
|---|---|
| 便の検索 | `POST /_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/GetShoppingResults` |
| 日付ごとの最安値 | `POST /_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/GetCalendarGraph` |
| 空港・都市の入力補完 | `POST /_/FlightsFrontendUi/data/batchexecute?rpcids=H028ib` |

どれも `f.req` に検索条件の JSON を入れて送り、`)]}'` に続く `[["wrb.fr", …, "結果の JSON 文字列"]]` を受け取る。
検索条件と結果は、キーのない入れ子の配列で、位置で意味が決まる（たとえば便の [2] が区間の一覧、区間の [22] が便名）。
検索条件は、検索結果ページの URL の `tfs` パラメータ（protobuf を Base64 にしたもの）と同じ内容を配列にしたもの。
結果に添える Google Flights の URL は、同じ条件から `tfs` を組み立てて作っている。

子供を含む国際線などでは、Google 側で運賃を集め終わる前の途中の結果が返ることがある。
集め終わった応答は末尾の要素が `true` なので、そうでなければ 1.5 秒待って問い合わせ直す（最大 3 回）。

同じ問い合わせは 5 分間メモリにキャッシュし、同時リクエスト数は 2 に制限している。

## 注意

- 内部 API を解析しているので、Google Flights が改修されると動かなくなる可能性がある。
- 価格は検索時点の目安で、予約サイトでの最終価格（手数料・受託手荷物など）とは異なることがある。
- 個人がローカルで使う前提で作っている。短い間隔で繰り返し問い合わせるような使い方はしないこと。
- Google の利用規約を守るのは利用者の責任とする。

## ライセンス

[MIT License](LICENSE)
