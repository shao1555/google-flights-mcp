using System.Globalization;
using System.Text;

namespace GoogleFlightsMcp;

/// <summary>
/// Google Flights の検索 URL の tfs パラメータ（検索条件の protobuf を URL-safe Base64 にしたもの）を組み立てる。
/// .proto は公開されていないので、使うフィールドだけを手で書き出す。
/// </summary>
/// <remarks>
/// Info { repeated FlightData data = 3; repeated Passenger passengers = 8; Seat seat = 9; Trip trip = 19; }
/// FlightData { string date = 2; repeated SelectedLeg selected = 4; int32 max_stops = 5; repeated string airlines = 6;
///              repeated Location from = 13; repeated Location to = 14; }
/// Location { int32 type = 1; string code = 2; }  // type 0 = 空港（IATA）、3 = 都市（/m/…）
/// SelectedLeg { string from = 1; string date = 2; string to = 3; string carrier = 5; string number = 6; }
/// Passenger は 1 = 大人、2 = 子供、3 = 幼児（座席）、4 = 幼児（膝上）で、人数分だけ繰り返す。
/// Trip は 1 = 往復、2 = 片道。
/// </remarks>
public static class Tfs
{
    public static string Build(FlightQuery q)
    {
        var info = new ProtoWriter();
        info.Message(3, FlightData(q.Date, q.From, q.To, q, q.SelectedOutbound));
        if (q.Return is { } ret)
            info.Message(3, FlightData(ret, q.To, q.From, q, null));
        foreach (var (kind, count) in new[]
                 {
                     (1, q.Passengers.Adults), (2, q.Passengers.Children),
                     (3, q.Passengers.InfantsInSeat), (4, q.Passengers.InfantsOnLap),
                 })
            for (var i = 0; i < count; i++) info.Varint(8, kind);
        info.Varint(9, (int)q.Cabin);
        info.Varint(19, q.Return is null ? 2 : 1);
        return Convert.ToBase64String(info.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ProtoWriter FlightData(DateOnly date, IReadOnlyList<Place> from, IReadOnlyList<Place> to, FlightQuery q,
        IReadOnlyList<SelectedLeg>? selected)
    {
        var w = new ProtoWriter();
        w.String(2, Iso(date));
        foreach (var leg in selected ?? [])
        {
            w.Message(4, new ProtoWriter()
                .String(1, leg.From).String(2, Iso(leg.Date)).String(3, leg.To)
                .String(5, leg.Carrier).String(6, leg.Number));
        }
        // 0 は「直行のみ」の意味なので、未指定のときはフィールドごと省く
        if (q.MaxStops is { } stops) w.Varint(5, stops);
        foreach (var airline in q.Airlines ?? []) w.String(6, airline);
        foreach (var p in from) w.Message(13, Location(p));
        foreach (var p in to) w.Message(14, Location(p));
        return w;
    }

    private static ProtoWriter Location(Place p)
    {
        var w = new ProtoWriter();
        if (p.IsCity) w.Varint(1, 3);
        return w.String(2, p.Code);
    }

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _buf = new();

        public ProtoWriter Varint(int field, long value)
        {
            WriteVarint((ulong)(field << 3));
            WriteVarint((ulong)value);
            return this;
        }

        public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public ProtoWriter Message(int field, ProtoWriter message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _buf.ToArray();

        private ProtoWriter Bytes(int field, byte[] value)
        {
            WriteVarint((ulong)((field << 3) | 2));
            WriteVarint((ulong)value.Length);
            _buf.Write(value);
            return this;
        }

        private void WriteVarint(ulong v)
        {
            while (v >= 0x80)
            {
                _buf.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _buf.WriteByte((byte)v);
        }
    }
}
