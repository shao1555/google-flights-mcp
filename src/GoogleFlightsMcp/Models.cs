namespace GoogleFlightsMcp;

public enum CabinClass { Economy = 1, PremiumEconomy = 2, Business = 3, First = 4 }

public enum SortOrder { Best, Price, Departure, Arrival, Duration }

/// <summary>
/// 出発地・目的地。空港は IATA コード（HND）、都市は Google のエンティティ ID（/m/07dfk = 東京）。
/// 都市を指定すると、その都市の空港（東京なら羽田と成田）をまとめて検索する。
/// </summary>
public sealed record Place(string Code, bool IsCity, string? Name = null);

public sealed record Passengers(int Adults = 1, int Children = 0, int InfantsInSeat = 0, int InfantsOnLap = 0)
{
    public int Total => Adults + Children + InfantsInSeat + InfantsOnLap;
}

/// <summary>選んだ便の 1 区間。往路を選んで復路を検索するときに使う。</summary>
public sealed record SelectedLeg(string From, DateOnly Date, string To, string Carrier, string Number);

/// <summary>
/// 片道または往復の検索条件。Return が null なら片道。
/// SelectedOutbound を入れると、その往路便に組み合わせられる復路便を検索する。
/// </summary>
public sealed record FlightQuery(
    IReadOnlyList<Place> From,
    IReadOnlyList<Place> To,
    DateOnly Date,
    DateOnly? Return,
    Passengers Passengers,
    CabinClass Cabin = CabinClass.Economy,
    int? MaxStops = null,
    IReadOnlyList<string>? Airlines = null,
    IReadOnlyList<SelectedLeg>? SelectedOutbound = null);

public sealed record FlightSearchResult(
    IReadOnlyList<Itinerary> Itineraries,
    PriceInsight? PriceInsight);

/// <summary>Price は全員分の合計（往復検索では往復の合計）で、円。IsBest は Google の「おすすめ」に入っているか。</summary>
public sealed record Itinerary(
    bool IsBest,
    int? Price,
    IReadOnlyList<string> Airlines,
    FlightPoint Departure,
    FlightPoint Arrival,
    int DurationMinutes,
    IReadOnlyList<Leg> Legs,
    IReadOnlyList<Layover> Layovers,
    Emissions? Emissions);

public sealed record FlightPoint(string Airport, string? AirportName, DateOnly Date, TimeOnly Time);

public sealed record Leg(
    string Carrier,
    string Number,
    string? AirlineName,
    FlightPoint Departure,
    FlightPoint Arrival,
    int DurationMinutes,
    string? Aircraft,
    string? Legroom);

/// <summary>ArrivalAirport と DepartureAirport が違うときは、乗り継ぎで空港を移動する。</summary>
public sealed record Layover(int Minutes, string ArrivalAirport, string DepartureAirport, string? AirportName, string? City);

/// <summary>グラム。DiffPercent はこの路線の標準的な排出量との差（%）。</summary>
public sealed record Emissions(int Grams, int? TypicalGrams, int? DiffPercent);

/// <summary>この条件の最安値と、Google が「標準的」とする価格帯（円）。</summary>
public sealed record PriceInsight(int? Lowest, int? TypicalLow, int? TypicalHigh);

/// <summary>Kind は airport / city / station。Code は空港なら IATA コード、都市なら /m/… の ID。</summary>
public sealed record PlaceSuggestion(
    string Kind,
    string Name,
    string Code,
    string? City,
    string? Description,
    IReadOnlyList<NearbyAirport> Airports);

public sealed record NearbyAirport(string Code, string Name, string? Distance);

/// <summary>出発日（往復なら復路の日付も）ごとの最安値（円）。</summary>
public sealed record DatePrice(DateOnly Date, DateOnly? Return, int Price);
