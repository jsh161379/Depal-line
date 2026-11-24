using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Shipping;

/// <summary>담당: 노원종 — F2 출고·배송. 공통 OrderStore를 통해서만 주문을 변경한다.</summary>
public sealed partial class ShippingViewModel : ObservableObject, IHasBadge
{
    private readonly OrderStore _store;
    private readonly ChatStore _chat;
    private readonly LineSupplyReader _supply;
    private readonly AppSettings _settings;
    private readonly IShippingDialogs _dialogs;
    private readonly ShippingAllocation _allocation = new();
    private readonly ObservableCollection<ShippingRow> _rows = new();

    public ShippingViewModel(OrderStore store, ChatStore chat, LineSupplyReader supply,
        AppSettings settings, IShippingDialogs dialogs)
    {
        _store = store;
        _chat = chat;
        _supply = supply;
        _settings = settings;
        _dialogs = dialogs;
        Pending = new ListCollectionView(_rows) { Filter = r => ((ShippingRow)r).Order.Status == OrderStatus.Inspect };
        Pending.SortDescriptions.Add(new SortDescription("Order.ReceivedAt", ListSortDirection.Ascending));
        OnTheWay = new ListCollectionView(_rows) { Filter = r => ((ShippingRow)r).Order.Status == OrderStatus.Shipping };
        store.Changed += Refresh;
        store.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OrderStore.IsConnected)) NotifyCommands(); };
        supply.Changed += () =>
        {
            foreach (var row in _rows) row.Checklist.NotifyEligibilityChanged();
            NotifySummary();
        };
        Refresh();
    }

    public ListCollectionView Pending { get; }
    public ListCollectionView OnTheWay { get; }
    public string CourierName => ShippingRow.FixedCourier;
    public string TrackingNote => IsDemo ? "연습용 자동 송장 · 실제 택배 조회에는 사용할 수 없습니다."
        : "택배사가 발급한 송장을 입력하세요. 실제 자동 발급은 택배사 연동 후 사용할 수 있습니다.";
    public int BadgeCount => Pending.Count;
    public int PendingCount => Pending.Count;
    public int NeededBatteries => PendingRows.Sum(r => r.Order.BatteryCount);
    public int LineReadyA => _allocation.Available(_supply.Runs);
    public bool IsShort => LineReadyA < NeededBatteries;
    public string SupplyText => _supply.StatusText;
    public bool IsDemo => _settings.UseFakeOrders;
    public string ModeNote => IsDemo
        ? "연습 모드 · 예시 주문과 수량을 사용합니다. 실제 주문은 전송하지 않습니다."
        : "실제 연결 모드 · 서버 연결과 DB 수량을 확인한 뒤 출고합니다.";
    public bool IsIdle => !IsBusy;
    public int CheckedCount => PendingRows.Count(r => r.IsChecked);
    public string InstructionButtonText => CheckedCount > 0 ? $"선택 {CheckedCount}건 지시서" : "출고 지시서 확인";
    public int InstructionOrderCount => DocumentRows().Length;
    public int CheckedBatteries => PendingRows.Where(r => r.IsChecked).Sum(r => r.Order.BatteryCount);
    public string CheckedSummary => $"선택 {CheckedCount}건 · 건전지 {CheckedBatteries:N0}개";
    public bool IsCheckedShort => CheckedBatteries > LineReadyA;
    public int UnconfirmedCheckedCount => PendingRows.Count(r => r.IsChecked && !r.Checklist.IsComplete);
    public string SelectedChecklistText => Selected?.Checklist.StatusText ?? "먼저 출고할 주문을 선택하세요.";
    public string CheckedReadiness => CheckedCount == 0 ? "왼쪽에서 출고할 주문을 체크하세요."
        : IsCheckedShort ? $"{CheckedBatteries - LineReadyA:N0}개 부족 · 선택 수량을 줄여 주세요."
        : UnconfirmedCheckedCount > 0 ? $"출고 지시서 {UnconfirmedCheckedCount}건 확인 필요"
        : $"출고 가능 · 처리 후 {LineReadyA - CheckedBatteries:N0}개 남음";
    public string SelectedReadiness => Selected == null ? ""
        : Selected.Order.Status != OrderStatus.Inspect ? "이미 출고된 주문입니다."
        : Selected.Order.BatteryCount > LineReadyA ? $"필요 {Selected.Order.BatteryCount:N0}개 · {Selected.Order.BatteryCount - LineReadyA:N0}개 부족"
        : $"필요 {Selected.Order.BatteryCount:N0}개 · 출고 가능";
    public ObservableCollection<string> Results { get; } = new();
    private IEnumerable<ShippingRow> PendingRows => _rows.Where(r => r.Order.Status == OrderStatus.Inspect);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShipCommand), nameof(PrintInstructionCommand), nameof(PrintStatementCommand))]
    private ShippingRow? _selected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDemoSupplyCommand))]
    private string _demoAddCount = "100";
    [ObservableProperty] private string _message = "출고 지시서에서 3개 항목을 체크하고 확인 완료한 뒤 출고하세요.";

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedChanged(ShippingRow? value)
    {
        OnPropertyChanged(nameof(SelectedReadiness));
        OnPropertyChanged(nameof(SelectedChecklistText));
        ReviewSelectedCommand.NotifyCanExecuteChanged();
        if (value == null) return;
        Message = value.NeedsReview ? "일부 전송이 진행된 주문입니다. 작업 기록과 서버 저장 상태를 확인해 주세요."
            : value.Order.Status == OrderStatus.Inspect ? !value.Checklist.IsComplete
                ? "출고 지시서를 열어 상품·수량, 포장 상태, 송장·배송지를 체크하고 ‘확인 완료’를 눌러 주세요."
                : "출고 지시서 확인을 마쳤습니다. 물량을 확인하고 출고하세요."
            : "배송 중인 주문입니다. 출고 지시서·거래명세서를 다시 인쇄할 수 있습니다.";
    }
    private bool CanShip() => !IsBusy && Selected is { NeedsReview: false } r && r.Checklist.IsComplete
        && r.Order.Status == OrderStatus.Inspect && r.Order.BatteryCount > 0 && r.Order.BatteryCount <= LineReadyA
        && TrackingRules.Error(r.Courier, r.Tracking) == null;
    private bool CanShipChecked() => !IsBusy && CheckedCount > 0 && !IsCheckedShort && UnconfirmedCheckedCount == 0
        && PendingRows.Where(r => r.IsChecked).All(r => !r.NeedsReview && TrackingRules.Error(r.Courier, r.Tracking) == null);
    private bool CanReviewSelected() => CanPrintInstruction();
    private bool CanUse() => !IsBusy;
    private bool CanPrint() => !IsBusy && (CheckedCount > 0 || Selected != null);
    private bool CanPrintInstruction() => CanPrint() && HasEnoughForInstruction(DocumentRows());
    private bool HasEnoughForInstruction(IReadOnlyList<ShippingRow> rows)
    {
        if (rows.Count == 0 || rows.Any(r => r.Order.Status is not (OrderStatus.Inspect or OrderStatus.Shipping or OrderStatus.Delivered))) return false;
        var required = rows.Where(r => r.Order.Status == OrderStatus.Inspect).Sum(r => r.Order.BatteryCount);
        return required <= LineReadyA;
    }
    private ShippingRow[] DocumentRows()
    {
        var rows = PendingRows.Where(r => r.IsChecked).ToArray();
        return rows.Length == 0 && Selected != null ? new[] { Selected } : rows;
    }

    [RelayCommand(CanExecute = nameof(CanShip))]
    private Task Ship() => ProcessAsync(Selected == null ? Array.Empty<ShippingRow>() : new[] { Selected });
    [RelayCommand(CanExecute = nameof(CanShipChecked))]
    private Task ShipChecked() => ProcessAsync(PendingRows.Where(r => r.IsChecked)
        .OrderBy(r => r.Order.ReceivedAt).ToArray());

    private async Task ProcessAsync(IReadOnlyList<ShippingRow> rows)
    {
        if (IsBusy || rows.Count == 0) return;
        var errors = new List<string>();
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var error = TrackingRules.Error(row.Courier, row.Tracking);
            if (row.Courier != CourierName) error = $"출고 택배사는 {CourierName}으로 고정되어 있습니다.";
            if (row.Order.Status != OrderStatus.Inspect) error = "출고 검사 상태의 주문만 출고할 수 있습니다.";
            if (row.NeedsReview) error = "이전 처리 상태를 먼저 확인해야 합니다.";
            if (!row.Checklist.IsComplete) error = "출고 지시서의 체크 3개와 ‘확인 완료’가 필요합니다.";
            if (error == null)
            {
                var key = row.Courier + ":" + row.NormalizedTracking;
                if (!numbers.Add(key) || _store.Orders.Any(o => o.No != row.Order.No
                    && o.Status != OrderStatus.Canceled && o.Courier == row.Courier
                    && TrackingRules.Normalize(o.Tracking) == row.NormalizedTracking))
                    error = "다른 주문과 송장번호가 같습니다. 주문마다 별도 송장을 입력해 주세요.";
            }
            if (error != null) { row.Result = error; errors.Add($"{row.Order.No}: {error}"); }
        }
        if (errors.Count > 0)
        {
            Message = "입력을 확인해 주세요. 아직 출고한 주문은 없습니다.";
            _dialogs.Show(string.Join("\n", errors));
            return;
        }
        IsBusy = true;
        try
        {
            if (!IsDemo)
            {
                if (!_store.IsConnected) { Message = "서버에 연결되지 않아 출고할 수 없습니다."; return; }
                if (_supply.IsExample) { Message = "실제 출고에는 DB 연결 설정이 필요합니다. 예시 수량으로는 출고하지 않습니다."; return; }
                await _supply.RefreshAsync();
                if (_supply.Error != null || _supply.ReadAt == null)
                { Message = "라인 수량을 확인하지 못했습니다. DB 연결과 작업 기록을 확인해 주세요."; return; }
            }
            var total = rows.Sum(r => r.Order.BatteryCount);
            if (total <= 0 || total > LineReadyA)
            {
                Message = $"출고 가능 수량이 부족합니다. 필요 {total:N0}개 / 남음 {LineReadyA:N0}개. 라인에 요청하거나 선택을 줄여 주세요.";
                _dialogs.Show(Message);
                return;
            }
            if (!_dialogs.Confirm($"{rows.Count}건, 건전지 {total:N0}개를 출고할까요?\n" +
                "주문별 송장과 검사 회차를 연결하고 배송 중으로 변경합니다." + (IsDemo ? "\n현재는 연습 모드입니다." : ""))) return;

            Results.Clear();
            var completed = 0;
            var reported = new HashSet<ShippingRow>();
            foreach (var row in rows)
            {
                reported.Add(row);
                if (row.Order.Status != OrderStatus.Inspect || !row.Checklist.IsComplete || (!IsDemo && !_store.IsConnected))
                { row.Result = "미처리 · 주문 상태 또는 서버 연결이 변경됨"; Results.Add($"{row.Order.No} · {row.Result}"); break; }
                var takes = _allocation.Plan(_supply.Runs, row.Order.BatteryCount);
                if (takes == null) { row.Result = "미처리 · 수량 변경"; Results.Add($"{row.Order.No} · {row.Result}"); break; }
                _allocation.Reserve(_supply.Runs, takes);
                // 일부 전송 뒤 실패하면 재시도로 회차를 중복 연결하지 않도록 확인 필요 상태로 남긴다.
                try
                {
                    row.Result = "처리 중";
                    foreach (var take in takes) await _store.PublishRunAsync(row.Order, take.RunId, take.Count);
                    await _store.SetShippingInfoAsync(row.Order, row.Courier, row.NormalizedTracking);
                    await _store.ChangeStatusAsync(row.Order, OrderStatus.Shipping);
                    row.Tracking = row.NormalizedTracking;
                    row.IsChecked = false;
                    row.Result = IsDemo ? "연습 출고 완료" : _store.IsConnected ? "출고 전송 완료" : "서버 재전송 대기 · 작업 기록 확인";
                    Results.Add($"{row.Order.No} · {row.Result}");
                    completed++;
                    if (!IsDemo && !_store.IsConnected) { row.NeedsReview = true; break; }
                }
                catch (Exception ex)
                {
                    row.NeedsReview = true;
                    row.Result = "확인 필요 · 일부 전송 가능";
                    Results.Add($"{row.Order.No} · {row.Result}");
                    _store.Write($"{row.Order.No} 출고 중단: {ex.Message}");
                    _dialogs.Show("출고 처리를 중단했습니다. 회차·송장·상태가 일부 반영됐을 수 있으니 작업 기록과 서버를 확인해 주세요.\n" + ex.Message);
                    break;
                }
            }
            foreach (var row in rows.Where(r => !reported.Contains(r)))
            {
                row.Result = "미처리 · 앞 주문에서 처리가 중단됨";
                Results.Add($"{row.Order.No} · {row.Result}");
            }
            Message = $"{rows.Count}건 중 {completed}건 처리, {rows.Count - completed}건 미완료. 아래 처리 결과를 확인하세요.";
        }
        catch (Exception ex) { Message = "출고 준비에 실패했습니다. " + ex.Message; _dialogs.Show(Message); }
        finally { IsBusy = false; Refresh(); }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void SelectAll()
    {
        var rows = PendingRows.ToArray();
        var check = !rows.All(r => r.IsChecked);
        foreach (var row in rows) row.IsChecked = check;
    }
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task RefreshSupply()
    {
        IsBusy = true;
        try { await _supply.RefreshAsync(); Message = _supply.StatusText; }
        finally { IsBusy = false; NotifySummary(); }
    }
    private bool CanAddDemoSupply() => IsDemo && !IsBusy && int.TryParse(DemoAddCount, out var count) && count is >= 1 and <= 10000;
    [RelayCommand(CanExecute = nameof(CanAddDemoSupply))]
    private async Task AddDemoSupply()
    {
        if (!CanAddDemoSupply()) return;
        IsBusy = true;
        try
        {
            var count = int.Parse(DemoAddCount);
            await _supply.AddExampleBatteriesAsync(count);
            Message = $"연습 수량 {count:N0}개를 추가했습니다. 출고 가능 {LineReadyA:N0}개입니다. 실제 재고에는 반영되지 않습니다.";
        }
        catch (Exception ex) { Message = "연습 수량을 추가하지 못했습니다. " + ex.Message; }
        finally { IsBusy = false; NotifySummary(); }
    }
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task RequestLine()
    {
        var checkedRows = PendingRows.Where(r => r.IsChecked).ToArray();
        var count = checkedRows.Length > 0 ? checkedRows.Sum(r => r.Order.BatteryCount)
            : Selected?.Order.BatteryCount ?? NeededBatteries;
        var reference = checkedRows.Length == 1 ? checkedRows[0].Order.No
            : checkedRows.Length == 0 ? Selected?.Order.No : null;
        var text = reference != null ? $"주문 {reference} 출고용 A 등급 건전지 {count}개 필요합니다."
            : $"출고 {(checkedRows.Length > 0 ? checkedRows.Length : PendingCount)}건, A 등급 건전지 {count}개 필요합니다.";
        if (await _chat.SendAsync($"{text} (지금 준비 {LineReadyA}개)", reference))
        { _store.Write("라인에 A 등급 요청 메시지 보냄"); Message = "라인 요청 메시지를 보냈습니다. F7에서 확인할 수 있습니다."; }
        else Message = "라인 요청 메시지를 보내지 못했습니다. 연결 상태를 확인해 주세요.";
    }
    [RelayCommand(CanExecute = nameof(CanPrintInstruction))]
    private void PrintInstruction() => Preview(false);
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void PrintStatement() => Preview(true);
    [RelayCommand(CanExecute = nameof(CanReviewSelected))]
    private void ReviewSelected()
    {
        if (CanReviewSelected()) Preview(false);
    }
    private void Preview(bool statement)
    {
        var rows = DocumentRows();
        if (rows.Length == 0) return;
        if (!statement && !HasEnoughForInstruction(rows))
        { Message = "건전지가 부족하여 출고 지시서를 열 수 없습니다. 출고 가능 수량을 먼저 확보해 주세요."; return; }
        ShippingDocuments.Preview(rows, statement, _settings.Operator, IsDemo);
        NotifySummary();
    }
    private void Refresh()
    {
        var used = new HashSet<string>(_store.Orders.Select(o => TrackingRules.Normalize(o.Tracking))
            .Concat(_rows.Select(r => r.NormalizedTracking)), StringComparer.Ordinal);
        foreach (var order in _store.Orders)
        {
            if (_rows.Any(r => r.Order == order)) continue;
            var row = new ShippingRow(order, () => LineReadyA);
            row.PropertyChanged += RowChanged;
            row.Checklist.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ShippingChecklist.IsComplete) or nameof(ShippingChecklist.StatusText)) NotifySummary();
                if (e.PropertyName == nameof(ShippingChecklist.StatusText) && Selected == row && row.Order.Status == OrderStatus.Inspect)
                    Message = row.Checklist.IsComplete ? "출고 지시서 확인을 마쳤습니다. 물량을 확인하고 출고하세요."
                        : "출고 지시서 확인이 필요합니다. 체크 3개와 ‘확인 완료’를 진행해 주세요.";
            };
            row.Checklist.Completed += () => _store.Write($"{row.Order.No} 출고 지시서 확인 완료 (상품·수량 / 포장 / 송장·배송지)");
            _rows.Add(row);
        }
        foreach (var row in PendingRows.Where(r => !r.NeedsReview))
        {
            row.Courier = CourierName;
            if (IsDemo && string.IsNullOrWhiteSpace(row.Tracking)) row.Tracking = DemoTrackingNumbers.Create(used);
        }
        foreach (var row in _rows.Where(r => r.Order.Status != OrderStatus.Inspect)) row.IsChecked = false;
        Pending.Refresh();
        OnTheWay.Refresh();
        NotifySummary();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShippingRow.IsChecked) or nameof(ShippingRow.Tracking)
            or nameof(ShippingRow.Courier) or nameof(ShippingRow.NeedsReview)) NotifySummary();
    }
    private void NotifySummary()
    {
        foreach (var name in new[] { nameof(BadgeCount), nameof(PendingCount), nameof(NeededBatteries), nameof(LineReadyA),
            nameof(IsShort), nameof(SupplyText), nameof(CheckedCount), nameof(CheckedBatteries), nameof(CheckedSummary),
            nameof(IsCheckedShort), nameof(CheckedReadiness), nameof(SelectedReadiness), nameof(UnconfirmedCheckedCount), nameof(SelectedChecklistText),
            nameof(InstructionButtonText), nameof(InstructionOrderCount) })
            OnPropertyChanged(name);
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        ShipCommand.NotifyCanExecuteChanged();
        ShipCheckedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        RequestLineCommand.NotifyCanExecuteChanged();
        RefreshSupplyCommand.NotifyCanExecuteChanged();
        PrintInstructionCommand.NotifyCanExecuteChanged();
        PrintStatementCommand.NotifyCanExecuteChanged();
        ReviewSelectedCommand.NotifyCanExecuteChanged();
        AddDemoSupplyCommand.NotifyCanExecuteChanged();
    }
}
