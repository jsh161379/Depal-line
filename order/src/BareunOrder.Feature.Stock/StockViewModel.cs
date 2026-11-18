using System.Collections.ObjectModel;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Stock;

/// <summary>상품 한 줄의 재고</summary>
public sealed partial class StockItem : ObservableObject
{
    private readonly int _lowLimit;

    public StockItem(Product p, int packs, int lowLimit)
    {
        Product = p;
        _packs = packs;
        _lowLimit = lowLimit;
    }

    public Product Product { get; }
    public string Name => Product.Name;
    public int Price => Product.Price;

    /// <summary>창고에 있는 팩 수</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Available))]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    private int _packs;

    /// <summary>들어온 주문(접수·출고 검사)에 묶인 팩 수</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Available))]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    private int _reserved;

    /// <summary>더 팔 수 있는 팩 수</summary>
    public int Available => Packs - Reserved;

    public bool IsLow => Available <= _lowLimit;
}

/// <summary>
/// 담당: (미정) — F3 재고.
/// 상품별 남은 팩 수, 주문에 묶인 수, 재고 부족 표시, 입고 기록.
///
/// ── 할 일 (TODO) ──
/// - 지금 팩 수는 예시 값. 재고를 DB(또는 파일)에 저장·불러오기
/// - 라인 A 등급 수 → 팩으로 포장(입고)하는 흐름: 오늘 A 등급 개수를 server/results에서 받아 "포장 가능 팩 수" 계산
/// - 가격·상품 이름 수정 (홈페이지 상품과 맞춰야 함 → 공통 담당과 상의)
/// - 배송 완료되면 창고 팩 수에서 빼기
/// </summary>
public sealed partial class StockViewModel : ObservableObject
{
    private readonly OrderStore _store;
    private readonly AppSettings _settings;

    public StockViewModel(OrderStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;

        var start = new Dictionary<string, int> { ["aa4"] = 40, ["aa12"] = 18, ["aa24"] = 6, ["aar2"] = 9 };   // 예시 값
        foreach (var p in Products.All)
            Items.Add(new StockItem(p, start.GetValueOrDefault(p.Id), settings.LowStockPacks));

        _store.Changed += Recount;
    }

    public ObservableCollection<StockItem> Items { get; } = new();

    /// <summary>입고 기록 (최신이 위)</summary>
    public ObservableCollection<string> History { get; } = new();

    public int LowLimit => _settings.LowStockPacks;
    public int LowCount => Items.Count(i => i.IsLow);

    /// <summary>TODO: 오늘 라인 검사 통과 A 등급 개수 (지금은 예시 값)</summary>
    [ObservableProperty] private int _lineReadyA = 60;

    [ObservableProperty] private StockItem? _selected;
    [ObservableProperty] private int _addPacks = 10;

    /// <summary>입고: 고른 상품에 팩 수 더하기</summary>
    [RelayCommand]
    private void AddStock()
    {
        if (Selected == null || AddPacks <= 0) return;
        Selected.Packs += AddPacks;
        var line = $"{DateTime.Now:MM-dd HH:mm}  {Selected.Name} +{AddPacks}팩 (창고 {Selected.Packs})";
        History.Insert(0, line);
        _store.Write($"재고 입고: {Selected.Name} +{AddPacks}팩");
        OnPropertyChanged(nameof(LowCount));
    }

    private void Recount()
    {
        var open = _store.Orders.Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect).ToList();
        foreach (var i in Items)
            i.Reserved = open.SelectMany(o => o.Items).Where(x => x.Id == i.Product.Id).Sum(x => x.Qty);
        OnPropertyChanged(nameof(LowCount));
    }
}
