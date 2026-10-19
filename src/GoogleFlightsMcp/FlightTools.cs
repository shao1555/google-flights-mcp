using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GoogleFlightsMcp;

[McpServerToolType]
public sealed partial class FlightTools(FlightsClient client)
{
    private const string PlaceHelp =
        "空港の IATA コード（HND）、search_airports で調べた都市の ID（/m/07dfk）、または地名（札幌、ホノルル）。" +
        "カンマ区切りで複数指定できる（HND,NRT）。都市を指定するとその都市の空港をまとめて検索する";

    [McpServerTool(Name = "search_airports", ReadOnly = true, Idempotent = true)]
    [Description("""
        Google Flights の入力補完で、空港と都市を検索する。日本語・英語・IATA コードのどれでも引ける（例: 札幌, London, HND）。
        都市には code として /m/… の ID が付き、search_flights の from / to に渡すとその都市の空港（東京なら羽田と成田）をまとめて検索できる。
        空港の code は IATA コードで、そのまま from / to に使える。
        """)]
    public async Task<object> SearchAirports(
        [Description("空港名・都市名・IATA コード（例: 札幌, ホノルル, London, CTS）")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query を指定してください。");
        var items = await client.SuggestAsync(query.Trim(), ct);
        return new
        {
            places = items.Select(i => new
            {
                kind = i.Kind,
                code = i.Code,
                name = i.Name,
                description = i.Description,
                airports = i.Airports.Count > 0 ? i.Airports.Select(a => new { code = a.Code, name = a.Name, distance = a.Distance }) : null,
            }),
            note = items.Count == 0
                ? "一致する空港・都市がありません。表記を変えて検索してください。"
                : "kind=city の airports はその都市から近い空港と、都市中心からの距離。",
        };
    }

    [McpServerTool(Name = "search_flights", ReadOnly = true, Idempotent = true)]
    [Description("""
        Google Flights で航空券を検索する（日本語・日本円・日本市場の価格）。
        便ごとに価格（全員分の合計・円）、航空会社、出発/到着日時（各空港の現地時刻）、所要時間、乗り継ぎ、区間ごとの便名・機材・足元の広さ、CO2 排出量を返す。

        - 片道: from, to, date を指定する
        - 往復: return_date も指定する。結果は往路の便の一覧で、price は往復の最安合計。
          復路の便を見るには、選んだ往路の id を outbound_flight_id に入れてもう一度呼ぶ（同じ from / to / date / return_date で）。
          そのときの price は、その往路と組み合わせた往復の合計になる。
        日付が決まっていない・安い日を探したいときは search_cheapest_dates を使う。

        例: 「11/20 に東京から札幌」→ from="東京", to="CTS", date="2026-11-20"
            「羽田から LA へ直行便で、ビジネスクラス」→ from="HND", to="LAX", max_stops=0, cabin="business"
        結果を利用者に示すときは url（Google Flights の検索結果ページ）をリンクとして添えること。
        """)]
    public async Task<object> SearchFlights(
        [Description("出発地。" + PlaceHelp)] string from,
        [Description("目的地。" + PlaceHelp)] string to,
        [Description("出発日 (YYYY-MM-DD)")] string date,
        [Description("往復のときの復路の日付 (YYYY-MM-DD)。省略すると片道")] string? return_date = null,
        [Description("往復で復路を検索するとき、前回の結果から選んだ往路の id")] string? outbound_flight_id = null,
        [Description("大人（12 歳以上）の人数")] int adults = 1,
        [Description("子供（2〜11 歳）の人数")] int children = 0,
        [Description("幼児（2 歳未満・座席あり）の人数")] int infants_in_seat = 0,
        [Description("幼児（2 歳未満・膝上）の人数")] int infants_on_lap = 0,
        [Description("座席クラス: economy（既定）, premium_economy, business, first")] string cabin = "economy",
        [Description("乗り継ぎ回数の上限: 0=直行便のみ, 1, 2。省略すると制限なし")] int? max_stops = null,
        [Description("航空会社の IATA コード（JL, NH など）かアライアンス（STAR_ALLIANCE, ONEWORLD, SKYTEAM）で絞り込む")] string[]? airlines = null,
        [Description("並び順: best=Google のおすすめ順（既定）, price=安い順, departure=出発が早い順, arrival=到着が早い順, duration=所要時間が短い順")] string sort = "best",
        [Description("返す便の数（1〜100、既定 20）")] int limit = 20,
        CancellationToken ct = default)
    {
        var (fromPlaces, toPlaces) = await ResolveRouteAsync(from, to, ct);
        var depart = ParseDate(date, nameof(date));
        var ret = string.IsNullOrWhiteSpace(return_date) ? (DateOnly?)null : ParseDate(return_date, nameof(return_date));
        if (ret is { } r && r < depart) throw new McpException("return_date は date 以降の日付にしてください。");
        if (limit is < 1 or > 100) throw new McpException("limit は 1〜100 で指定してください。");

        IReadOnlyList<SelectedLeg>? outbound = null;
        if (!string.IsNullOrWhiteSpace(outbound_flight_id))
        {
            if (ret is null) throw new McpException("outbound_flight_id は往復（return_date あり）の検索でだけ使えます。");
            outbound = ParseFlightId(outbound_flight_id);
        }

        var query = new FlightQuery(
            fromPlaces, toPlaces, depart, ret,
            ParsePassengers(adults, children, infants_in_seat, infants_on_lap),
            ParseCabin(cabin),
            ParseMaxStops(max_stops),
            ParseAirlines(airlines),
            outbound);
        var sortOrder = ParseOption(sort, nameof(sort), new Dictionary<string, SortOrder>
        {
            ["best"] = SortOrder.Best,
            ["price"] = SortOrder.Price,
            ["departure"] = SortOrder.Departure,
            ["arrival"] = SortOrder.Arrival,
            ["duration"] = SortOrder.Duration,
        });

        var result = await client.SearchFlightsAsync(query, ct);
        var sorted = sortOrder switch
        {
            SortOrder.Price => result.Itineraries.OrderBy(i => i.Price ?? int.MaxValue),
            SortOrder.Departure => result.Itineraries.OrderBy(i => i.Departure.Date.ToDateTime(i.Departure.Time)),
            SortOrder.Arrival => result.Itineraries.OrderBy(i => i.Arrival.Date.ToDateTime(i.Arrival.Time)),
            SortOrder.Duration => result.Itineraries.OrderBy(i => i.DurationMinutes),
            _ => result.Itineraries.AsEnumerable(),
        };

        var direction = outbound is not null ? "return" : ret is not null ? "outbound" : "one_way";
        var pi = result.PriceInsight;
        return new
        {
            from = Describe(fromPlaces),
            to = Describe(toPlaces),
            date = Tfs.Iso(depart),
            return_date = ret is { } rd ? Tfs.Iso(rd) : null,
            // 往復で、ここに並ぶのが往路か復路か
            listing = direction,
            selected_outbound = outbound_flight_id,
            passengers = query.Passengers.Total,
            cabin = cabin.Trim().ToLowerInvariant(),
            total_count = result.Itineraries.Count,
            price_insight = pi is null ? null : new
            {
                lowest = pi.Lowest,
                typical_range = pi is { TypicalLow: { } lo, TypicalHigh: { } hi } ? $"{lo:N0}〜{hi:N0}円" : null,
                level = pi switch
                {
                    { Lowest: { } p, TypicalLow: { } lo2 } when p < lo2 => "安い",
                    { Lowest: { } p, TypicalHigh: { } hi2 } when p > hi2 => "高い",
                    { Lowest: not null, TypicalLow: not null, TypicalHigh: not null } => "標準",
                    _ => null,
                },
            },
            flights = sorted.Take(limit).Select(i => new
            {
                id = FlightId(i),
                best = i.IsBest ? true : (bool?)null,
                price = i.Price,
                airlines = string.Join(", ", i.Airlines),
                departure = Point(i.Departure),
                arrival = Point(i.Arrival),
                duration = Duration(i.DurationMinutes),
                stops = i.Legs.Count - 1,
                layovers = i.Layovers.Count > 0
                    ? i.Layovers.Select(l => new
                    {
                        airport = l.ArrivalAirport == l.DepartureAirport ? l.ArrivalAirport : $"{l.ArrivalAirport}→{l.DepartureAirport}",
                        name = l.AirportName,
                        duration = Duration(l.Minutes),
                        change_airport = l.ArrivalAirport != l.DepartureAirport ? true : (bool?)null,
                    })
                    : null,
                legs = i.Legs.Select(l => new
                {
                    flight = l.Carrier + l.Number,
                    airline = l.AirlineName,
                    from = $"{l.Departure.AirportName} ({l.Departure.Airport})",
                    to = $"{l.Arrival.AirportName} ({l.Arrival.Airport})",
                    departure = Stamp(l.Departure),
                    arrival = Stamp(l.Arrival),
                    duration = Duration(l.DurationMinutes),
                    aircraft = l.Aircraft,
                    legroom = l.Legroom,
                }),
                co2_kg = i.Emissions is { } e ? (int?)Math.Round(e.Grams / 1000.0) : null,
                co2_vs_typical = i.Emissions?.DiffPercent is { } diff ? $"{diff:+0;-0;0}%" : null,
            }),
            url = FlightsClient.BuildSearchUrl(query),
            note = result.Itineraries.Count == 0
                ? "条件に合う便が見つかりませんでした。日付・乗り継ぎ回数・航空会社の条件を変えて検索してください。"
                : direction switch
                {
                    "outbound" => "往路の一覧。price は往復の最安合計（円・全員分）。復路の便は、選んだ往路の id を outbound_flight_id に入れてもう一度検索する。",
                    "return" => "selected_outbound の往路に組み合わせられる復路の一覧。price はその往路と組み合わせた往復の合計（円・全員分）。",
                    _ => "price は全員分の合計（円）。",
                } + "日時は各空港の現地時刻。best=true は Google の「おすすめ」。価格は検索時点の目安で、予約サイトでの最終価格とは異なることがある。",
        };
    }

    [McpServerTool(Name = "search_cheapest_dates", ReadOnly = true, Idempotent = true)]
    [Description("""
        Google Flights の価格カレンダーで、出発日ごとの最安値（円・全員分の合計）を調べる。「いつが安い？」「来月で一番安い日は？」に使う。
        往復は trip_days（滞在日数）を指定する。出発日の trip_days 日後に戻る組み合わせの最安値になる。
        安い日が見つかったら、その日付で search_flights を呼んで便を確かめること。
        """)]
    public async Task<object> SearchCheapestDates(
        [Description("出発地。" + PlaceHelp)] string from,
        [Description("目的地。" + PlaceHelp)] string to,
        [Description("出発日の範囲の始まり (YYYY-MM-DD)。省略すると今日（日本時間）")] string? start_date = null,
        [Description("出発日の範囲の終わり (YYYY-MM-DD)。省略すると start_date の 30 日後。範囲は最大 90 日")] string? end_date = null,
        [Description("往復のときの滞在日数（出発日から復路の日までの日数、1〜30）。省略すると片道")] int? trip_days = null,
        [Description("大人（12 歳以上）の人数")] int adults = 1,
        [Description("子供（2〜11 歳）の人数")] int children = 0,
        [Description("幼児（2 歳未満・座席あり）の人数")] int infants_in_seat = 0,
        [Description("幼児（2 歳未満・膝上）の人数")] int infants_on_lap = 0,
        [Description("座席クラス: economy（既定）, premium_economy, business, first")] string cabin = "economy",
        [Description("乗り継ぎ回数の上限: 0=直行便のみ, 1, 2。省略すると制限なし")] int? max_stops = null,
        [Description("航空会社の IATA コード（JL, NH など）かアライアンス（STAR_ALLIANCE, ONEWORLD, SKYTEAM）で絞り込む")] string[]? airlines = null,
        CancellationToken ct = default)
    {
        var (fromPlaces, toPlaces) = await ResolveRouteAsync(from, to, ct);
        var start = string.IsNullOrWhiteSpace(start_date) ? FlightsClient.TodayJst() : ParseDate(start_date, nameof(start_date));
        var end = string.IsNullOrWhiteSpace(end_date) ? start.AddDays(30) : ParseDate(end_date, nameof(end_date));
        if (end < start) throw new McpException("end_date は start_date 以降の日付にしてください。");
        if (end.DayNumber - start.DayNumber > 90) throw new McpException("出発日の範囲は 90 日以内にしてください。");
        if (trip_days is < 1 or > 30) throw new McpException("trip_days は 1〜30 で指定してください。");

        var query = new FlightQuery(
            fromPlaces, toPlaces, start, trip_days is { } days ? start.AddDays(days) : null,
            ParsePassengers(adults, children, infants_in_seat, infants_on_lap),
            ParseCabin(cabin),
            ParseMaxStops(max_stops),
            ParseAirlines(airlines));

        var prices = await client.GetCalendarAsync(query, start, end, trip_days, ct);
        var cheapest = prices.Count > 0 ? prices.Min(p => p.Price) : (int?)null;
        return new
        {
            from = Describe(fromPlaces),
            to = Describe(toPlaces),
            trip = trip_days is { } d ? $"往復（{d} 日後に戻る）" : "片道",
            passengers = query.Passengers.Total,
            cheapest = prices.Where(p => p.Price == cheapest).Select(DateLabel),
            cheapest_price = cheapest,
            dates = prices.Select(p => new
            {
                date = DateLabel(p),
                price = p.Price,
            }),
            url = FlightsClient.BuildSearchUrl(query),
            note = prices.Count == 0
                ? "価格の情報がありませんでした。日付の範囲や条件を変えて検索してください。"
                : "price は日付ごとの最安値（円・全員分の合計、往復なら往復の合計）。Google Flights の価格カレンダーの値で、便を検索したときの価格とずれることがある。",
        };

        static string DateLabel(DatePrice p) =>
            p.Return is { } r ? $"{WithWeekday(p.Date)} → {WithWeekday(r)}" : WithWeekday(p.Date);
    }

    // ---- 地点の解決 ----

    [GeneratedRegex("^[A-Za-z]{3}$")]
    private static partial Regex IataRegex();

    private async Task<(IReadOnlyList<Place> From, IReadOnlyList<Place> To)> ResolveRouteAsync(string from, string to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new McpException("from（出発地）を指定してください。");
        if (string.IsNullOrWhiteSpace(to)) throw new McpException("to（目的地）を指定してください。");
        var f = await ResolvePlacesAsync(from, nameof(from), ct);
        var t = await ResolvePlacesAsync(to, nameof(to), ct);
        if (f.Select(p => p.Code).Intersect(t.Select(p => p.Code)).Any())
            throw new McpException("出発地と目的地が同じです。");
        return (f, t);
    }

    /// <summary>IATA コードと /m/… の ID はそのまま使い、地名は入力補完の先頭の空港か都市にする。</summary>
    private async Task<IReadOnlyList<Place>> ResolvePlacesAsync(string input, string name, CancellationToken ct)
    {
        var places = new List<Place>();
        foreach (var token in input.Split([',', '、', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IataRegex().IsMatch(token))
            {
                places.Add(new Place(token.ToUpperInvariant(), IsCity: false));
            }
            else if (token.StartsWith("/m/", StringComparison.Ordinal) || token.StartsWith("/g/", StringComparison.Ordinal))
            {
                places.Add(new Place(token, IsCity: true));
            }
            else
            {
                var hit = (await client.SuggestAsync(token, ct)).FirstOrDefault()
                    ?? throw new McpException($"{name} の「{token}」に該当する空港・都市が見つかりません。search_airports で探すか、IATA コードで指定してください。");
                places.Add(new Place(hit.Code, hit.Kind == "city", hit.Name));
            }
        }
        if (places.Count == 0) throw new McpException($"{name} を指定してください。");
        if (places.Count > 7) throw new McpException($"{name} に指定できる空港・都市は 7 つまでです。");
        return places;
    }

    private static string Describe(IReadOnlyList<Place> places) =>
        string.Join(", ", places.Select(p => p.Name is null ? p.Code : $"{p.Name} ({p.Code})"));

    // ---- 便の id ----

    /// <summary>区間ごとの「便名/出発空港-到着空港/日付」をカンマでつないだもの（例: JL501/HND-CTS/2026-11-20）。</summary>
    private static string FlightId(Itinerary i) =>
        string.Join(",", i.Legs.Select(l => $"{l.Carrier}{l.Number}/{l.Departure.Airport}-{l.Arrival.Airport}/{Tfs.Iso(l.Departure.Date)}"));

    [GeneratedRegex(@"^([A-Z0-9]{2})(\d{1,4})/([A-Z]{3})-([A-Z]{3})/(\d{4}-\d{2}-\d{2})$")]
    private static partial Regex LegIdRegex();

    private static IReadOnlyList<SelectedLeg> ParseFlightId(string id)
    {
        var legs = new List<SelectedLeg>();
        foreach (var part in id.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = LegIdRegex().Match(part.ToUpperInvariant());
            if (!m.Success)
                throw new McpException("outbound_flight_id は search_flights の結果の id をそのまま渡してください（例: JL501/HND-CTS/2026-11-20）。");
            legs.Add(new SelectedLeg(
                m.Groups[3].Value,
                DateOnly.ParseExact(m.Groups[5].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                m.Groups[4].Value,
                m.Groups[1].Value,
                m.Groups[2].Value));
        }
        return legs;
    }

    // ---- 引数の解釈 ----

    private static DateOnly ParseDate(string value, string name)
    {
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new McpException($"{name} は YYYY-MM-DD 形式で指定してください。");
        var today = FlightsClient.TodayJst();
        if (d < today) throw new McpException($"{name} が過去の日付です（今日は {Tfs.Iso(today)}）。");
        if (d > today.AddDays(330)) throw new McpException($"{name} は今日から 330 日以内にしてください（Google Flights で検索できる範囲）。");
        return d;
    }

    private static Passengers ParsePassengers(int adults, int children, int infantsInSeat, int infantsOnLap)
    {
        if (adults < 1) throw new McpException("adults は 1 人以上にしてください。");
        if (children < 0 || infantsInSeat < 0 || infantsOnLap < 0) throw new McpException("人数に負の数は指定できません。");
        if (infantsOnLap > adults) throw new McpException("膝上の幼児は大人 1 人につき 1 人までです。");
        var p = new Passengers(adults, children, infantsInSeat, infantsOnLap);
        if (p.Total > 9) throw new McpException("人数は合計 9 人までです。");
        return p;
    }

    private static CabinClass ParseCabin(string cabin) => ParseOption(cabin, nameof(cabin), new Dictionary<string, CabinClass>
    {
        ["economy"] = CabinClass.Economy,
        ["premium_economy"] = CabinClass.PremiumEconomy,
        ["business"] = CabinClass.Business,
        ["first"] = CabinClass.First,
    });

    private static int? ParseMaxStops(int? maxStops) =>
        maxStops is null or (>= 0 and <= 2) ? maxStops : throw new McpException("max_stops は 0〜2 で指定してください。");

    private static IReadOnlyList<string>? ParseAirlines(string[]? airlines)
    {
        var list = (airlines ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToUpperInvariant()).Distinct().ToList();
        return list.Count > 0 ? list : null;
    }

    private static T ParseOption<T>(string value, string name, Dictionary<string, T> options)
    {
        if (options.TryGetValue(value.Trim().ToLowerInvariant(), out var v)) return v;
        throw new McpException($"{name} は {string.Join(" / ", options.Keys)} のいずれかで指定してください。");
    }

    // ---- 表示 ----

    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    private static string WithWeekday(DateOnly d) => $"{Tfs.Iso(d)}({d.ToString("ddd", Japanese)})";

    private static string Stamp(FlightPoint p) => $"{WithWeekday(p.Date)} {p.Time:HH:mm}";

    private static string Point(FlightPoint p) => $"{Stamp(p)} {p.AirportName} ({p.Airport})";

    private static string Duration(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}時間{(minutes % 60 > 0 ? $"{minutes % 60}分" : "")}" : $"{minutes}分";
}
