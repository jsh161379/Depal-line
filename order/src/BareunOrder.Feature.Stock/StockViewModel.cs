using System.Collections.ObjectModel;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MySqlConnector;

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
/// - (11/19, 조시현) 실제 모드: 켤 때 DB stock 표에서 창고 팩 수 읽기, 입고는 order/stock으로 DB에 저장
///   배송 완료 차감은 DB 저장 프로그램이 함 → [새로 고침]으로 다시 읽으면 반영됨
/// - (11/19, 조시현) 라인 A 등급 남은 수: Core LineSupplyReader. "주문에 묶인 팩"에 배송 중 포함
/// - 입고·차감 기록(stock_log)을 기록 목록에 보이기
/// - 가격·상품 이름 수정 (홈페이지 상품과 맞춰야 함 → 공통 담당과 상의)
/// </summary>
public sealed partial class StockViewModel : ObservableObject
{
    private readonly OrderStore _store;
    private readonly AppSettings _settings;
    private readonly LineSupplyReader _supply;

    public StockViewModel(OrderStore store, AppSettings settings, LineSupplyReader supply)
    {
        _store = store;
        _settings = settings;
        _supply = supply;

        var start = new Dictionary<string, int> { ["aa4"] = 40, ["aa12"] = 18, ["aa24"] = 6, ["aar2"] = 9 };   // 가짜 모드 예시 값
        foreach (var p in Products.All)
            Items.Add(new StockItem(p, UsesDb ? 0 : start.GetValueOrDefault(p.Id), settings.LowStockPacks));

        _store.Changed += Recount;
        supply.Changed += () => OnPropertyChanged(nameof(LineReadyA));
        if (UsesDb) _ = LoadFromDbAsync();
    }

    /// <summary>실제 모드 + DB 연결 설정이 있으면 DB stock 표가 기준</summary>
    private bool UsesDb => !_settings.UseFakeOrders && !string.IsNullOrWhiteSpace(_settings.MySqlConnection);

    /// <summary>창고 팩 수를 DB에서 다시 읽기 (배송 완료 차감은 DB 저장 프로그램이 함)</summary>
    [RelayCommand]
    private async Task LoadFromDbAsync()
    {
        if (!UsesDb) return;
        try
        {
            var packs = await Task.Run(async () =>
            {
                var d = new Dictionary<string, int>();
                await using var conn = new MySqlConnection(_settings.MySqlConnection);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT product_id, stock_packs FROM stock";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) d[r.GetString(0)] = Convert.ToInt32(r.GetValue(1));
                return d;
            });
            foreach (var i in Items) i.Packs = packs.GetValueOrDefault(i.Product.Id);
            Recount();
            _store.Write("재고: DB에서 창고 팩 수 읽음", "시스템");
        }
        catch (Exception ex)
        {
            _store.Write($"재고: DB 읽기 실패 ({ex.Message})", "시스템");
        }
    }

    public ObservableCollection<StockItem> Items { get; } = new();

    /// <summary>입고 기록 (최신이 위)</summary>
    public ObservableCollection<string> History { get; } = new();

    public int LowLimit => _settings.LowStockPacks;
    public int LowCount => Items.Count(i => i.IsLow);

    /// <summary>라인 A 등급 남은 수 (아직 주문에 안 나간 것, Core LineSupplyReader)</summary>
    public int LineReadyA => _supply.Available;

    [ObservableProperty] private StockItem? _selected;
    [ObservableProperty] private int _addPacks = 10;

    /// <summary>입고: 고른 상품에 팩 수 더하기 → order/stock(restock)으로 DB에 저장</summary>
    [RelayCommand]
    private async Task AddStock()
    {
        if (Selected == null || AddPacks <= 0) return;
        await _store.PublishStockAsync(Selected.Product.Id, "restock", AddPacks);
        Selected.Packs += AddPacks;
        var line = $"{DateTime.Now:MM-dd HH:mm}  {Selected.Name} +{AddPacks}팩 (창고 {Selected.Packs})";
        History.Insert(0, line);
        OnPropertyChanged(nameof(LowCount));
    }

    private void Recount()
    {
        // 재고는 배송 완료 때 빠지므로 배송 중인 주문도 아직 창고 수에 들어 있음 → 묶인 수에 넣는다 (DB 담당 메모)
        var open = _store.Orders.Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect or OrderStatus.Shipping).ToList();
        foreach (var i in Items)
            i.Reserved = open.SelectMany(o => o.Items).Where(x => x.Id == i.Product.Id).Sum(x => x.Qty);
        OnPropertyChanged(nameof(LowCount));
    }
}
