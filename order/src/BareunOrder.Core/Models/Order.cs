using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Core.Models;

/// <summary>주문 상태. 홈페이지 주문 조회의 4단계 + 취소.</summary>
public enum OrderStatus
{
    Received,   // 주문 접수
    Inspect,    // 출고 검사
    Shipping,   // 배송 중
    Delivered,  // 배송 완료
    Canceled,   // 취소
}

public static class OrderStatusText
{
    public static string ToKorean(OrderStatus s) => s switch
    {
        OrderStatus.Received => "주문 접수",
        OrderStatus.Inspect => "출고 검사",
        OrderStatus.Shipping => "배송 중",
        OrderStatus.Delivered => "배송 완료",
        OrderStatus.Canceled => "취소",
        _ => "",
    };

    /// <summary>MQTT로 보낼 때 쓰는 이름</summary>
    public static string ToWire(OrderStatus s) => s switch
    {
        OrderStatus.Received => "received",
        OrderStatus.Inspect => "inspect",
        OrderStatus.Shipping => "shipping",
        OrderStatus.Delivered => "delivered",
        OrderStatus.Canceled => "canceled",
        _ => "received",
    };

    public static OrderStatus FromWire(string? s) => s switch
    {
        "inspect" => OrderStatus.Inspect,
        "shipping" => OrderStatus.Shipping,
        "delivered" => OrderStatus.Delivered,
        "canceled" => OrderStatus.Canceled,
        _ => OrderStatus.Received,
    };

    /// <summary>다음 단계. 마지막(배송 완료)이나 취소면 null.</summary>
    public static OrderStatus? Next(OrderStatus s) => s switch
    {
        OrderStatus.Received => OrderStatus.Inspect,
        OrderStatus.Inspect => OrderStatus.Shipping,
        OrderStatus.Shipping => OrderStatus.Delivered,
        _ => null,
    };
}

/// <summary>주문 한 줄 (상품 하나)</summary>
public sealed record OrderItem(string Id, string Name, int Qty, int Price)
{
    public int Amount => Qty * Price;

    /// <summary>이 줄에 들어간 건전지 개수 (팩 수 × 팩당 개수)</summary>
    public int BatteryCount => Qty * Products.PackSize(Id);
}

/// <summary>홈페이지에서 들어온 주문 1건</summary>
public sealed partial class Order : ObservableObject
{
    public required string No { get; init; }          // BR-YYYYMMDD-XXXX
    public DateTime ReceivedAt { get; init; }
    public string Name { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Address { get; init; } = "";
    public string Memo { get; init; } = "";
    public IReadOnlyList<OrderItem> Items { get; init; } = Array.Empty<OrderItem>();
    public int Total { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(NextStatusText))]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private OrderStatus _status;

    [ObservableProperty] private string? _courier;    // 택배사
    [ObservableProperty] private string? _tracking;   // 송장번호
    [ObservableProperty] private string? _certNo;     // 검사 성적서 번호 (F6, 형식은 F6 담당이 정함)

    /// <summary>주문 관리 프로그램이 검사를 통과시킨 시각 (order/accepted 의 accepted_at)</summary>
    public DateTime? AcceptedAt { get; set; }

    public string StatusText => OrderStatusText.ToKorean(Status);

    public string NextStatusText =>
        OrderStatusText.Next(Status) is { } n ? OrderStatusText.ToKorean(n) : "";

    public bool CanAdvance => OrderStatusText.Next(Status) != null;
    public bool CanCancel => Status is OrderStatus.Received or OrderStatus.Inspect;

    /// <summary>화면에 보일 연락처 (가운데 가림): 010-****-5678</summary>
    public string MaskedPhone
    {
        get
        {
            var parts = Phone.Split('-');
            return parts.Length == 3 ? $"{parts[0]}-****-{parts[2]}" : Phone;
        }
    }

    /// <summary>목록에 보일 상품 요약: "AA 12개입 ×2 외 1건"</summary>
    public string ItemsSummary => Items.Count switch
    {
        0 => "",
        1 => $"{Items[0].Name} ×{Items[0].Qty}",
        _ => $"{Items[0].Name} ×{Items[0].Qty} 외 {Items.Count - 1}건",
    };

    public int PackCount => Items.Sum(i => i.Qty);
    public int BatteryCount => Items.Sum(i => i.BatteryCount);
}
