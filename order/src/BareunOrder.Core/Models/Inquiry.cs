using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Core.Models;

/// <summary>고객 문의 1건 (주문_통신_규약 4장). F5 고객·문의 탭에서 쓴다.</summary>
public sealed partial class Inquiry : ObservableObject
{
    public required string No { get; init; }           // Q-YYYYMMDD-XXXX
    public DateTime AskedAt { get; init; }
    public string Name { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Type { get; init; } = "etc";         // delivery / cancel / product / bulk / etc
    public string? OrderNo { get; init; }
    public string Question { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnswered))]
    private string? _answer;

    [ObservableProperty] private string? _answeredBy;
    [ObservableProperty] private DateTime? _answeredAt;

    public bool IsAnswered => !string.IsNullOrEmpty(Answer);

    public string TypeText => InquiryTypes.ToKorean(Type);

    public string MaskedPhone
    {
        get
        {
            var parts = Phone.Split('-');
            return parts.Length == 3 ? $"{parts[0]}-****-{parts[2]}" : Phone;
        }
    }
}

public static class InquiryTypes
{
    public static readonly IReadOnlyList<string> All = new[] { "delivery", "cancel", "product", "bulk", "etc" };

    public static string ToKorean(string type) => type switch
    {
        "delivery" => "주문·배송",
        "cancel" => "취소·교환",
        "product" => "제품",
        "bulk" => "대량 구매",
        _ => "기타",
    };
}
