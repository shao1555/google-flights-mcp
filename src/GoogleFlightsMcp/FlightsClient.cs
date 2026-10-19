using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace GoogleFlightsMcp;

/// <summary>
/// Google Flights（www.google.com/travel/flights）の内部 API を呼んで結果を解析するクライアント。
/// 言語・地域・通貨は日本（hl=ja, gl=JP, curr=JPY）に固定している。
/// </summary>
/// <remarks>
/// 公開 API はないので、ブラウザ版が使っているものを読む。
/// - 便の検索: FlightsFrontendService/GetShoppingResults（検索結果ページに埋め込まれる ds:1 と同じ、位置で意味が決まる入れ子の配列）
/// - 空港・都市の入力補完: batchexecute の H028ib
/// - 日付ごとの最安値: FlightsFrontendService/GetCalendarGraph
/// </remarks>
public sealed class FlightsClient(IHttpClientFactory httpClientFactory, ILogger<FlightsClient> logger)
{
    public const string HttpClientName = "google-flights";
    public const string BaseUrl = "https://www.google.com/";
    private const string LocaleQuery = "hl=ja&gl=JP&curr=JPY";

    // 同じ条件の検索がすぐに繰り返されることがあるので、短時間だけキャッシュする
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Throttle = new(2);
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, string Body)> _cache = new();

    public static DateOnly TodayJst() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).DateTime);

    public static string BuildSearchUrl(FlightQuery q) => $"{BaseUrl}travel/flights/search?tfs={Tfs.Build(q)}&{LocaleQuery}";

    // ---- 便の検索 ----

    private const string ShoppingPath =
        $"_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/GetShoppingResults?{LocaleQuery}";

    /// <summary>
    /// 便の検索。子供を含む国際線などでは、Google 側で運賃を集め終わる前の途中の結果（数件だけ）が返ることがある。
    /// 集め終わった応答は末尾の要素が true なので、そうでなければ少し待って問い合わせ直す（2 回目以降はそろっていることが多い）。
    /// </summary>
    public async Task<FlightSearchResult> SearchFlightsAsync(FlightQuery query, CancellationToken ct)
    {
        var freq = new JsonArray(null, new JsonArray(null, ToRequestParams(query)).ToJsonString()).ToJsonString();
        JsonNode? data = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                ForgetCache(ShoppingPath, freq);
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            }
            data = await PostRpcAsync(ShoppingPath, freq, ct,
                "検索できませんでした。出発地・目的地の空港コードを確認してください（分からなければ search_airports で調べる）。");
            if (data is JsonArray { Count: > 0 } a && a[^1] is JsonValue v && v.GetValueKind() == JsonValueKind.True) break;
            logger.LogInformation("検索結果がまだそろっていないので問い合わせ直します（{Attempt} 回目）", attempt + 1);
        }
        return ParseShoppingResults(data);
    }

    internal static FlightSearchResult ParseShoppingResults(JsonNode? data)
    {
        // [2] が「おすすめ」、[3] がそのほかの便。どちらも [[便, …], …] の形
        var itineraries = new List<Itinerary>();
        var seen = new HashSet<string>();
        foreach (var (bucket, best) in new[] { (2, true), (3, false) })
        {
            if (data.At(bucket, 0) is not JsonArray items) continue;
            foreach (var item in items)
            {
                var it = ToItinerary(item, best);
                if (it is null) continue;
                var key = string.Join("|", it.Legs.Select(l => $"{l.Carrier}{l.Number}{l.Departure.Date}"));
                if (seen.Add(key)) itineraries.Add(it);
            }
        }

        // [5] = [?, [null, 最安値], [null, 標準価格], [null, 差], [null, 標準の下限], [null, 標準の上限], …]
        var insight = data.At(5) is JsonArray
            ? new PriceInsight(data.At(5, 1, 1).AsInt(), data.At(5, 4, 1).AsInt(), data.At(5, 5, 1).AsInt())
            : null;
        if (insight is { Lowest: null, TypicalLow: null, TypicalHigh: null }) insight = null;

        return new FlightSearchResult(itineraries, insight);
    }

    /// <summary>
    /// 便 1 件は [情報, [[null, 価格], トークン], …]。情報は
    /// [航空会社コード, [航空会社名], [区間], 出発空港, 出発日, 出発時刻, 到着空港, 到着日, 到着時刻, 所要分, …, [13] 乗り継ぎ, …, [22] CO2]。
    /// </summary>
    private static Itinerary? ToItinerary(JsonNode? item, bool best)
    {
        var info = item.At(0);
        if (info.At(2) is not JsonArray legNodes || legNodes.Count == 0) return null;

        var legs = legNodes.Select(ToLeg).ToList();
        var layovers = (info.At(13) as JsonArray ?? [])
            .Select(l => new Layover(
                l.At(0).AsInt() ?? 0,
                l.At(1).AsString() ?? "",
                l.At(2).AsString() ?? "",
                l.At(4).AsString(),
                l.At(5).AsString()))
            .ToList();

        // [22] = [null, null, ?, 標準との差(%), null, ?, ?, 排出量(g), 標準の排出量(g), …]
        var co2 = info.At(22);
        var emissions = co2.At(7).AsInt() is { } grams ? new Emissions(grams, co2.At(8).AsInt(), co2.At(3).AsInt()) : null;

        var airlines = (info.At(1) as JsonArray ?? []).Select(a => a.AsString()).OfType<string>().ToList();

        return new Itinerary(
            best,
            item.At(1, 0, 1).AsInt(),
            airlines,
            new FlightPoint(info.At(3).AsString() ?? legs[0].Departure.Airport, legs[0].Departure.AirportName,
                AsDate(info.At(4)) ?? legs[0].Departure.Date, AsTime(info.At(5))),
            new FlightPoint(info.At(6).AsString() ?? legs[^1].Arrival.Airport, legs[^1].Arrival.AirportName,
                AsDate(info.At(7)) ?? legs[^1].Arrival.Date, AsTime(info.At(8))),
            info.At(9).AsInt() ?? legs.Sum(l => l.DurationMinutes),
            legs,
            layovers,
            emissions);
    }

    /// <summary>
    /// 区間は [.., .., .., 出発空港, 出発空港名, 到着空港名, 到着空港, .., 出発時刻, .., 到着時刻, 所要分, 設備, .., 足元の広さ, ..,
    /// .., 機材, .., .., 出発日, 到着日, [航空会社コード, 便名, null, 航空会社名], …]。
    /// </summary>
    private static Leg ToLeg(JsonNode? l) => new(
        l.At(22, 0).AsString() ?? "",
        l.At(22, 1).AsString() ?? "",
        l.At(22, 3).AsString(),
        new FlightPoint(l.At(3).AsString() ?? "", l.At(4).AsString(), AsDate(l.At(20)) ?? default, AsTime(l.At(8))),
        new FlightPoint(l.At(6).AsString() ?? "", l.At(5).AsString(), AsDate(l.At(21)) ?? default, AsTime(l.At(10))),
        l.At(11).AsInt() ?? 0,
        NullIfEmpty(l.At(17).AsString()),
        NullIfEmpty(l.At(14).AsString()));

    /// <summary>[2026, 11, 20] → 2026-11-20。</summary>
    private static DateOnly? AsDate(JsonNode? n) =>
        n.At(0).AsInt() is { } y && n.At(1).AsInt() is { } m && n.At(2).AsInt() is { } d ? new DateOnly(y, m, d) : null;

    /// <summary>[6, 45] → 6:45。0 時は [null, 5]、0 分は [7] のように省かれる。</summary>
    private static TimeOnly AsTime(JsonNode? n) => new(n.At(0).AsInt() ?? 0, n.At(1).AsInt() ?? 0);

    // ---- 空港・都市の入力補完 ----

    public async Task<IReadOnlyList<PlaceSuggestion>> SuggestAsync(string query, CancellationToken ct)
    {
        var inner = new JsonArray(query, new JsonArray(1, 2, 3, 5, 4), null, new JsonArray(1, 1, 1), 1);
        var payload = await BatchExecuteAsync("H028ib", inner, ct);
        if (payload.At(0) is not JsonArray groups) return [];

        // 1 グループ = [地点, [[近くの空港・駅, 距離], …]]。地点の [0] は 1 = 空港、3 = 都市、5 = 駅
        var result = new List<PlaceSuggestion>();
        foreach (var g in groups)
        {
            var p = g.At(0);
            var kind = p.At(0).AsInt();
            var code = kind == 1 ? p.At(5).AsString() : p.At(4).AsString();
            var name = p.At(1).AsString();
            if (code is null || name is null || kind is not (1 or 3)) continue;
            var airports = (g.At(1) as JsonArray ?? [])
                .Where(a => a.At(0, 0).AsInt() == 1 && a.At(0, 5).AsString() is not null)
                .Select(a => new NearbyAirport(a.At(0, 5).AsString()!, a.At(0, 1).AsString() ?? "", NullIfEmpty(a.At(1).AsString())))
                .ToList();
            result.Add(new PlaceSuggestion(kind == 1 ? "airport" : "city", name, code, p.At(2).AsString(), p.At(3).AsString(), airports));
        }
        return result;
    }

    // ---- 日付ごとの最安値 ----

    /// <summary>
    /// 出発日を start〜end の範囲で動かしたときの、日付ごとの最安値。
    /// 往復（tripDays あり）では、出発日の tripDays 日後に戻る組み合わせの最安値になる。
    /// </summary>
    public async Task<IReadOnlyList<DatePrice>> GetCalendarAsync(FlightQuery query, DateOnly start, DateOnly end, int? tripDays,
        CancellationToken ct)
    {
        var request = new JsonArray(null, ToRequestParams(query), new JsonArray(Tfs.Iso(start), Tfs.Iso(end)));
        if (tripDays is { } days)
        {
            request.Add(null);
            request.Add(new JsonArray(days, days));
        }
        var payload = await PostRpcAsync(
            $"_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/GetCalendarGraph?{LocaleQuery}",
            new JsonArray(null, request.ToJsonString()).ToJsonString(), ct);

        // [1] = [[出発日, 復路の日付, [[null, 価格], トークン], …], …]
        return (payload.At(1) as JsonArray ?? [])
            .Select(d => (
                Date: ParseIsoDate(d.At(0).AsString()),
                Return: ParseIsoDate(d.At(1).AsString()),
                Price: d.At(2, 0, 1).AsInt()))
            .Where(d => d.Date is { } date && date >= start && date <= end && d.Price is not null)
            .Select(d => new DatePrice(d.Date!.Value, d.Return, d.Price!.Value))
            .ToList();
    }

    /// <summary>
    /// 内部 API に渡す検索条件。検索結果ページの ds:0 に入っているものと同じ形で、tfs の protobuf の中身を配列にしたもの。
    /// 地点の種別は 0 = 空港、5 = 都市。経由数は 0 = 指定なし、n + 1 = n 回まで。
    /// </summary>
    private static JsonArray ToRequestParams(FlightQuery q)
    {
        var legs = new JsonArray(Leg(q.From, q.To, q.Date, q.SelectedOutbound));
        if (q.Return is { } ret) legs.Add(Leg(q.To, q.From, ret, null));

        var p = q.Passengers;
        return new JsonArray(
            null, null, q.Return is null ? 2 : 1, null, new JsonArray(), (int)q.Cabin,
            new JsonArray(p.Adults, p.Children, p.InfantsInSeat, p.InfantsOnLap),
            null, null, null, null, null, null, legs, null, null, null, 1);

        // 選んだ往路の区間は [出発空港, 日付, 到着空港, null, 航空会社コード, 便名]
        JsonArray Leg(IReadOnlyList<Place> from, IReadOnlyList<Place> to, DateOnly date, IReadOnlyList<SelectedLeg>? selected) => new(
            Places(from), Places(to), null, q.MaxStops is { } s ? s + 1 : 0,
            q.Airlines is { Count: > 0 } a ? new JsonArray([.. a.Select(x => (JsonNode?)x)]) : null,
            null, Tfs.Iso(date), null,
            selected is { Count: > 0 }
                ? new JsonArray([.. selected.Select(l => (JsonNode?)new JsonArray(l.From, Tfs.Iso(l.Date), l.To, null, l.Carrier, l.Number))])
                : null,
            null, null, null, null, null, 3);

        static JsonArray Places(IReadOnlyList<Place> places) =>
            new(new JsonArray([.. places.Select(x => (JsonNode?)new JsonArray(x.Code, x.IsCity ? 5 : 0))]));
    }

    // ---- 共通 ----

    private Task<JsonNode?> BatchExecuteAsync(string rpcId, JsonArray inner, CancellationToken ct)
    {
        var freq = new JsonArray(new JsonArray(new JsonArray(rpcId, inner.ToJsonString(), null, "generic"))).ToJsonString();
        return PostRpcAsync($"_/FlightsFrontendUi/data/batchexecute?rpcids={rpcId}&{LocaleQuery}", freq, ct);
    }

    /// <summary>
    /// 内部 API の応答は「)]}'」のあとに [["wrb.fr", rpcId, "結果の JSON 文字列", …], …] が続く。
    /// 失敗したときは結果が null で、[5] にエラーが入る。
    /// </summary>
    private async Task<JsonNode?> PostRpcAsync(string path, string freq, CancellationToken ct, string? errorMessage = null)
    {
        var body = await PostFormAsync(path, freq, ct);
        var start = body.IndexOf('[');
        var envelope = start < 0 ? null : JsonNode.Parse(body[start..]) as JsonArray;
        var entry = envelope?.FirstOrDefault(e => e.At(0).AsString() == "wrb.fr");
        if (entry.At(2).AsString() is not { } json)
        {
            logger.LogWarning("{Path} がエラーを返しました: {Body}", path, body.Length > 500 ? body[..500] : body);
            ForgetCache(path, freq);
            throw new McpException(errorMessage ?? "Google Flights への問い合わせが失敗しました。条件を見直すか、しばらくしてから試してください。");
        }
        return JsonNode.Parse(json);
    }

    private Task<string> PostFormAsync(string path, string freq, CancellationToken ct) =>
        SendAsync(CacheKey(path, freq), () => new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent([new("f.req", freq)]),
        }, ct);

    private static string CacheKey(string path, string freq) => path + "\n" + freq;

    private void ForgetCache(string path, string freq) => _cache.TryRemove(CacheKey(path, freq), out _);

    private async Task<string> SendAsync(string cacheKey, Func<HttpRequestMessage> request, CancellationToken ct)
    {
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTtl) return hit.Body;

        await Throttle.WaitAsync(ct);
        try
        {
            using var req = request();
            using var res = await httpClientFactory.CreateClient(HttpClientName).SendAsync(req, ct);
            if ((int)res.StatusCode is >= 300 and < 400)
            {
                // 同意画面（consent.google.com）や bot 判定（/sorry/）へのリダイレクト
                logger.LogWarning("{Path} は {Location} にリダイレクトされました", req.RequestUri, res.Headers.Location);
                throw new McpException("Google にアクセスを止められました（同意画面または bot 判定へのリダイレクト）。しばらくしてから試してください。");
            }
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("{Path} が {Status} を返しました", req.RequestUri, (int)res.StatusCode);
                throw new McpException((int)res.StatusCode == 429
                    ? "Google Flights へのリクエストが多すぎます。しばらくしてから試してください。"
                    : $"Google Flights への問い合わせに失敗しました（HTTP {(int)res.StatusCode}）。");
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            _cache[cacheKey] = (DateTime.UtcNow, body);
            return body;
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static DateOnly? ParseIsoDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>位置で意味が決まる入れ子の配列を、範囲外や型違いを null にしながら辿る。</summary>
internal static class JsonNodePath
{
    public static JsonNode? At(this JsonNode? node, params int[] path)
    {
        foreach (var i in path)
        {
            if (node is not JsonArray a || i < 0 || i >= a.Count) return null;
            node = a[i];
        }
        return node;
    }

    public static string? AsString(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static int? AsInt(this JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? (int)d : null;
}
