using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BareunOrder.Core.Models;

namespace BareunOrder.Core.Comm;

/// <summary>
/// 주문·문의 메시지 읽기(검사)와 만들기 (주문_통신_규약 v1.0, 3·4장).
/// 검사 규칙을 바꾸려면 여기만 고친다.
/// </summary>
public static class OrderJson
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>주문번호: BR-YYYYMMDD-XXXX (XXXX = 영문 대문자·숫자, 0 O 1 I 제외)</summary>
    public static readonly Regex OrderNo = new(@"^BR-\d{8}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.Compiled);

    /// <summary>문의번호: Q-YYYYMMDD-XXXX</summary>
    public static readonly Regex InquiryNo = new(@"^Q-\d{8}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.Compiled);

    /// <summary>검사 성적서 번호: CERT-YYYYMMDD-XXXX (주문번호의 날짜·4자 그대로, 11/18 F6 담당 결정)</summary>
    public static readonly Regex CertNo = new(@"^CERT-\d{8}-[A-HJ-NP-Z2-9]{4}$", RegexOptions.Compiled);

    /// <summary>주문번호로 성적서 번호 만들기: BR-20261117-7K3Q → CERT-20261117-7K3Q (형식이 틀린 주문번호면 null)</summary>
    public static string? CertNoFor(string orderNo) =>
        OrderNo.IsMatch(orderNo) ? "CERT-" + orderNo["BR-".Length..] : null;

    private static readonly JsonSerializerOptions Out = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Ts(DateTime t) => t.ToString(TimeFormat, CultureInfo.InvariantCulture);
    public static string Now() => Ts(DateTime.Now);

    public static DateTime? ParseTs(string? s) =>
        DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    // ───────────────────────── 주문 ─────────────────────────

    /// <summary>order/new 검사 결과. Error: null(통과) / BAD_FORMAT / PRICE_MISMATCH. No는 형식이 틀려도 읽혔으면 채움 (ack용)</summary>
    public sealed record OrderCheck(Order? Order, string? No, string? Error);

    public static OrderCheck CheckNewOrder(string json)
    {
        JsonElement r;
        try { r = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return new(null, null, "BAD_FORMAT"); }
        if (r.ValueKind != JsonValueKind.Object) return new(null, null, "BAD_FORMAT");

        var no = Str(r, "no");
        OrderCheck Bad() => new(null, no, "BAD_FORMAT");

        if (no == null || !OrderNo.IsMatch(no)) return Bad();
        var ts = ParseTs(Str(r, "ts"));
        var name = Str(r, "name");
        var phone = Str(r, "phone");
        var addr = Str(r, "addr");
        var memo = Str(r, "memo") ?? "";
        if (ts == null) return Bad();
        if (name is not { Length: >= 1 and <= 50 }) return Bad();
        if (Digits(phone).Length is < 9 or > 11) return Bad();
        if (addr is not { Length: >= 1 and <= 255 }) return Bad();
        if (memo.Length > 255) return Bad();
        if (!r.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return Bad();
        if (!Int(r, "total", out var total)) return Bad();

        var items = new List<OrderItem>();
        var priceWrong = false;
        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) return Bad();
            var id = Str(it, "id");
            var product = id == null ? null : Products.Find(id);
            if (product == null || !product.OnSale) return Bad();   // 판매 중지 상품(aar2, 11/20 ⑨)도 BAD_FORMAT
            if (!Int(it, "qty", out var qty) || qty is < 1 or > 99) return Bad();
            if (!Int(it, "price", out var price)) return Bad();
            if (price != product.Price) priceWrong = true;             // 단가를 가격표로 다시 확인
            items.Add(new OrderItem(product.Id, product.Name, qty, product.Price));
        }
        if (priceWrong || total != items.Sum(i => i.Amount)) return new(null, no, "PRICE_MISMATCH");

        // 고객 정보 (선택, v1.0.4): 없거나 틀려도 거절하지 않고 기본값으로
        var (customerType, ageGroup, region) =
            CustomerInfo.Normalize(Str(r, "customer_type"), Str(r, "age_group"), Str(r, "region"));

        var order = new Order
        {
            No = no,
            ReceivedAt = ts.Value,
            Name = name!,
            Phone = phone!,
            Address = addr!,
            Memo = memo,
            Items = items,
            Total = total,
            Status = OrderStatus.Received,
            CustomerType = customerType,
            AgeGroup = ageGroup,
            Region = region,
        };
        return new(order, no, null);
    }

    /// <summary>같은 번호로 다시 온 주문이 내용도 같은지 (QoS 1 중복이면 true)</summary>
    public static bool SameContent(Order a, Order b) =>
        a.No == b.No && a.Name == b.Name && Digits(a.Phone) == Digits(b.Phone) && a.Address == b.Address
        && a.Memo == b.Memo && a.Total == b.Total && a.Items.SequenceEqual(b.Items);

    public static string Ack(string no, bool ok, string? error) =>
        Ser(new { no, ok, error, ts = Now() });

    /// <summary>
    /// order/accepted — order/new와 같은 내용 + accepted_at (DB 저장용).
    /// ts는 주문 관리가 받은 시각(서버 PC 시계), 홈페이지가 보낸 시각은 client_ts (규약 3장, 11/17).
    /// 고객 PC 시계가 빠르면 뒤에 오는 상태 메시지가 DB에서 '오래된 것'으로 거부되기 때문.
    /// </summary>
    public static string Accepted(Order o) => Ser(new
    {
        no = o.No,
        ts = Ts(o.AcceptedAt ?? DateTime.Now),
        client_ts = Ts(o.ReceivedAt),
        name = o.Name,
        phone = o.Phone,
        addr = o.Address,
        memo = o.Memo,
        items = o.Items.Select(i => new { id = i.Id, name = i.Name, qty = i.Qty, price = i.Price }),
        total = o.Total,
        accepted_at = Ts(o.AcceptedAt ?? DateTime.Now),
        customer_type = o.CustomerType,   // v1.0.4 고객 정보 (region이 none이면 DB 저장 프로그램이 배송 주소로 채움)
        age_group = o.AgeGroup,
        region = o.Region,
    });

    /// <summary>order/status/&lt;no&gt; (retained). 이름·연락처·주소는 넣지 않는다</summary>
    public static string Status(Order o, string? by, DateTime? at = null) => Ser(new
    {
        no = o.No,
        status = OrderStatusText.ToWire(o.Status),
        ts = Ts(at ?? DateTime.Now),
        courier = o.Courier,
        tracking = o.Tracking,
        cert_no = o.CertNo,
        by,
    });

    /// <summary>order/cancel 읽기. 형식이 틀리면 null</summary>
    public static (string No, string Phone)? ParseCancel(string json)
    {
        try
        {
            var r = JsonDocument.Parse(json).RootElement;
            var no = Str(r, "no");
            var phone = Str(r, "phone");
            if (no == null || !OrderNo.IsMatch(no) || Digits(phone).Length is < 9 or > 11) return null;
            return (no, phone!);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>order/stock — kind: restock(packs만큼 더함) / adjust(창고 팩 수를 packs로)</summary>
    public static string Stock(string productId, string kind, int packs, string by) =>
        Ser(new { event_id = Guid.NewGuid().ToString("N"), ts = Now(), product_id = productId, kind, packs, by });

    /// <summary>order/run — 출고할 때 주문에 라인 회차 연결</summary>
    public static string Run(string orderNo, string runId, int batteryCount) =>
        Ser(new { no = orderNo, run_id = runId, battery_count = batteryCount, ts = Now() });

    // ───────────────────────── 문의 ─────────────────────────

    public sealed record InquiryCheck(Inquiry? Inquiry, string? No, string? Error);

    public static InquiryCheck CheckNewInquiry(string json)
    {
        JsonElement r;
        try { r = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return new(null, null, "BAD_FORMAT"); }
        if (r.ValueKind != JsonValueKind.Object) return new(null, null, "BAD_FORMAT");

        var no = Str(r, "no");
        InquiryCheck Bad() => new(null, no, "BAD_FORMAT");

        if (no == null || !InquiryNo.IsMatch(no)) return Bad();
        var ts = ParseTs(Str(r, "ts"));
        var name = Str(r, "name");
        var phone = Str(r, "phone");
        var type = Str(r, "type");
        var orderNo = Str(r, "order_no");
        var question = Str(r, "question");
        if (ts == null) return Bad();
        if (name is not { Length: >= 1 and <= 50 }) return Bad();
        if (Digits(phone).Length is < 9 or > 11) return Bad();
        if (type == null || !InquiryTypes.All.Contains(type)) return Bad();
        if (orderNo != null && !OrderNo.IsMatch(orderNo)) return Bad();
        if (question is not { Length: >= 5 and <= 1000 }) return Bad();

        return new(new Inquiry
        {
            No = no,
            AskedAt = ts.Value,
            Name = name!,
            Phone = phone!,
            Type = type,
            OrderNo = orderNo,
            Question = question!,
        }, no, null);
    }

    public static bool SameContent(Inquiry a, Inquiry b) =>
        a.No == b.No && a.Name == b.Name && Digits(a.Phone) == Digits(b.Phone) && a.Type == b.Type
        && a.OrderNo == b.OrderNo && a.Question == b.Question;

    /// <summary>inquiry/accepted — inquiry/new와 같은 내용 + accepted_at (DB 저장용). ts = 받은 시각(서버 시계), 홈페이지 시각은 client_ts</summary>
    public static string InquiryAccepted(Inquiry q, DateTime acceptedAt) => Ser(new
    {
        no = q.No,
        ts = Ts(acceptedAt),
        client_ts = Ts(q.AskedAt),
        name = q.Name,
        phone = q.Phone,
        type = q.Type,
        order_no = q.OrderNo,
        question = q.Question,
        accepted_at = Ts(acceptedAt),
    });

    /// <summary>inquiry/answer/&lt;no&gt; (retained). 질문·연락처는 넣지 않는다</summary>
    public static string Answer(Inquiry q) => Ser(new
    {
        no = q.No,
        answer = q.Answer,
        by = q.AnsweredBy,
        ts = Ts(q.AnsweredAt ?? DateTime.Now),
    });

    // ───────────────────────── 도우미 ─────────────────────────

    private static string Ser(object o) => JsonSerializer.Serialize(o, Out);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Int(JsonElement e, string name, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }
}
