namespace BareunOrder.Core.Models;

/// <summary>판매 상품 (홈페이지 PRODUCTS와 같게 유지)</summary>
public sealed record Product(string Id, string Name, int Price, int PackSize);

public static class Products
{
    public static readonly IReadOnlyList<Product> All = new[]
    {
        new Product("aa4",  "AA 알카라인 4개입",  3200,  4),
        new Product("aa12", "AA 알카라인 12개입", 8900,  12),
        new Product("aa24", "AA 알카라인 24개입", 15900, 24),
        new Product("aar2", "AA 충전지 2개입",    9800,  2),
    };

    public static Product? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static int PackSize(string id) => Find(id)?.PackSize ?? 1;
}
