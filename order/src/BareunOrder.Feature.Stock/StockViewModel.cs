using System.Collections.ObjectModel;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Stock;

/// <summary>상품 한 줄의 재고 (⑪ 안 A: 재고 = 라인 A 등급 건전지)</summary>
public sealed partial class StockItem : ObservableObject
{
    private readonly int _lowLimit;

    public StockItem(Product p, int lowLimit)
    {
        Product = p;
        _lowLimit = lowLimit;
    }

    public Product Product { get; }
    public string Name => Product.Name;
    public int Price => Product.Price;

    /// <summary>라인 A 남은 수로 만들 수 있는 팩 수 (주문 대기분을 빼기 전). 화면 칸 이름은 예전 "창고"</summary>
    [ObservableProperty] private int _packs;

    /// <summary>주문 대기(접수·출고 검사)에 묶인 이 상품의 팩 수. 배송 중은 출고 때 A에서 이미 빠져서 넣지 않음</summary>
    [ObservableProperty] private int _reserved;

    /// <summary>새 주문에 더 팔 수 있는 팩 수 = (라인 A − 모든 주문 대기분) ÷ 팩당 개수</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    private int _available;

    public bool IsLow => Available <= _lowLimit;
}

/// <summary>
/// 담당: (미정) — F3 재고.
/// 11/23 팀 결정 ⑪ 안 A: 포장 입고 단계를 두지 않고 "재고 = 라인 A 등급 건전지"로 자동 계산한다.
///   - 창고 팩 수(DB stock 표)와 손 입고(order/stock)는 쓰지 않는다 → 입고 버튼 없앰 (화면은 김하균)
///   - 배송 완료 때 stock 차감도 끔 (DB 저장 프로그램, 장성찬) — 출고(F2) 때 회차 연결로 이미 A에서 빠지므로 두 번 차감 방지
///   - 숫자는 Core LineSupplyReader 한 곳에서 계산 (F2 출고·아래 줄과 같은 값)
///
///   상품 줄 Packs     = 라인 A 남음 ÷ 팩당 개수
///          Reserved  = 접수·출고 검사 주문에 들어 있는 이 상품 팩 수
///          Available = (라인 A 남음 − 모든 접수·출고 검사 주문의 건전지 수) ÷ 팩당 개수
///   상품끼리 같은 A를 나눠 쓰므로 Available은 "이 상품만 판다면"의 값이다 (합계 아님).
///
/// ── 할 일 (TODO) ──
/// - 회차별 A 남은 수 목록 보이기 (LineSupplyReader.Runs) — 필요하면
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

        foreach (var p in Products.OnSale)   // 판매 중지 상품(aar2)은 재고 관리에서 뺌 (11/20 ⑨)
            Items.Add(new StockItem(p, settings.LowStockPacks));

        _store.Changed += Recount;     // 주문이 들어오거나 상태가 바뀌면 주문 대기분이 바뀜
        supply.Changed += Recount;     // 라인 A 남은 수가 바뀜 (1분마다·회차 연결 때)
        Recount();
    }

    public ObservableCollection<StockItem> Items { get; } = new();

    /// <summary>화면 안내 한 줄 (입고 버튼 자리)</summary>
    public string StockNote => "재고는 라인 A 등급 기준 자동 계산";

    public int LowLimit => _settings.LowStockPacks;
    public int LowCount => Items.Count(i => i.IsLow);

    /// <summary>라인 A 등급 남은 수 (아직 주문에 안 나간 것, Core LineSupplyReader)</summary>
    public int LineReadyA => _supply.Available;

    /// <summary>접수·출고 검사 주문이 가져갈 A 등급 수</summary>
    public int PendingNeedA => _supply.PendingNeed;

    /// <summary>새 주문에 쓸 수 있는 A 등급 수</summary>
    public int FreeA => _supply.Free;

    /// <summary>라인 A를 읽은 시각·예시 값 여부 (Core 아래 줄과 같은 글)</summary>
    public string SupplyText => _supply.StatusText;

    [ObservableProperty] private StockItem? _selected;

    /// <summary>[새로 고침]: 라인 A 남은 수를 DB에서 지금 다시 읽기 (화면 버튼 이름은 예전 그대로 LoadFromDbCommand)</summary>
    [RelayCommand]
    private Task LoadFromDbAsync() => _supply.RefreshAsync();

    private void Recount()
    {
        var open = _store.Orders.Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect).ToList();
        foreach (var i in Items)
        {
            i.Packs = _supply.PacksFor(i.Product);
            i.Reserved = open.SelectMany(o => o.Items).Where(x => x.Id == i.Product.Id).Sum(x => x.Qty);
            i.Available = _supply.FreePacksFor(i.Product);
        }
        OnPropertyChanged(nameof(LowCount));
        OnPropertyChanged(nameof(LineReadyA));
        OnPropertyChanged(nameof(PendingNeedA));
        OnPropertyChanged(nameof(FreeA));
        OnPropertyChanged(nameof(SupplyText));
    }
}
