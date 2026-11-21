namespace BareunOrder.Core.Models;

/// <summary>
/// 판매 상품 (홈페이지 PRODUCTS와 같게 유지).
/// OnSale = false: 판매 중지. 새 주문은 받지 않지만(BAD_FORMAT) 지난 주문·통계·DB 기록 때문에 목록에는 남긴다.
/// </summary>
public sealed record Product(string Id, string Name, int Price, int PackSize, bool OnSale = true)
{
    /// <summary>화면 표시용 이름 (판매 중지면 뒤에 표시)</summary>
    public string Label => OnSale ? Name : $"{Name} (판매 중지)";
}

public static class Products
{
    /// <summary>모든 상품 (판매 중지 포함). 지난 주문을 읽거나 통계를 낼 때</summary>
    public static readonly IReadOnlyList<Product> All = new[]
    {
        new Product("aa4",  "AA 알카라인 4개입",  3200,  4),
        new Product("aa6",  "AA 알카라인 6개입",  4700,  6),   // 11/21 팀 결정: 팔레트 한 판(2×3)과 같은 크기. 같은 날 추가한 aa8(8개입)을 바꿈
        new Product("aa12", "AA 알카라인 12개입", 8900,  12),
        new Product("aa24", "AA 알카라인 24개입", 15900, 24),
        // 11/20 팀 결정 ⑨: 라인은 알카라인 AA만 검사 → 홈페이지 v9에서 뺌. 지난 주문 통계를 위해 지우지 않음
        new Product("aar2", "AA 충전지 2개입",    9800,  2, OnSale: false),
    };

    /// <summary>지금 파는 상품만. 새 주문 검사·재고·가짜 주문에 씀</summary>
    public static readonly IReadOnlyList<Product> OnSale = All.Where(p => p.OnSale).ToList();

    public static Product? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static int PackSize(string id) => Find(id)?.PackSize ?? 1;
}
