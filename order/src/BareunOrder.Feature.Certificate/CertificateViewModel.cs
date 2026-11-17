using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Feature.Certificate;

/// <summary>성적서의 검사 항목 한 줄</summary>
public sealed record CheckRow(string Item, string Standard, string Result, bool Pass);

/// <summary>주문 하나의 검사 성적서 내용</summary>
public sealed record CertificateData(
    string CertNo,
    DateTime InspectedAt,
    string RunId,
    int Count,
    IReadOnlyList<CheckRow> Rows)
{
    public bool AllPass => Rows.All(r => r.Pass);
}

/// <summary>
/// 담당: (미정) — F6 검사 성적서.
/// 출고한(배송 중·배송 완료) 주문마다, 그 주문에 나간 건전지가 라인 검사를 통과했다는 성적서를 보여 준다.
/// 우리 라인의 검사 결과가 주문과 이어지는 탭.
///
/// ── 할 일 (TODO) ──
/// - 지금 성적서 숫자는 예시 값. 출고할 때 그 주문에 들어간 건전지의 회차(run_id)를 기록하고,
///   DB battery_cycles·electrical_tests에서 평균 전압·내부저항·외관·극 방향 결과를 불러오기 (서버 담당과 테이블 확인)
/// - 성적서 인쇄·PDF 저장 (PrintDialog.PrintVisual 또는 PDF 라이브러리 — 패키지 추가는 공통 담당에게)
/// - 성적서 번호를 홈페이지 주문 조회에서 볼 수 있게 보내기 (order/status에 칸 추가 → 공통 담당과 상의)
/// </summary>
public sealed partial class CertificateViewModel : ObservableObject
{
    private readonly OrderStore _store;

    public CertificateViewModel(OrderStore store)
    {
        _store = store;
        Shipped = new ListCollectionView(store.Orders)
        {
            Filter = o => ((Order)o).Status is OrderStatus.Shipping or OrderStatus.Delivered,
        };
        _store.Changed += () => Shipped.Refresh();
    }

    /// <summary>출고한 주문 (배송 중·배송 완료)</summary>
    public ListCollectionView Shipped { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Data))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private Order? _selected;

    /// <summary>고른 주문의 성적서 (TODO: DB에서 불러오기 — 지금은 예시 값)</summary>
    public CertificateData? Data => Selected == null ? null : MakeExample(Selected);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print() =>
        MessageBox.Show("성적서 인쇄·PDF 저장은 준비 중입니다.", "검사 성적서", MessageBoxButton.OK, MessageBoxImage.Information);

    private bool CanPrint() => Selected != null;

    /// <summary>예시 성적서. 주문번호로 숫자를 정해서, 같은 주문은 항상 같은 값이 나온다.</summary>
    private static CertificateData MakeExample(Order o)
    {
        var rnd = new Random(o.No.GetHashCode());
        var vOpen = 1.580 + rnd.NextDouble() * 0.030;
        var vLoad = vOpen - 0.08 - rnd.NextDouble() * 0.02;
        var rInt = 0.17 + rnd.NextDouble() * 0.06;
        var at = o.ReceivedAt.Date.AddHours(9).AddMinutes(rnd.Next(0, 300));

        var rows = new List<CheckRow>
        {
            new("외관 (스크래치·파손·부식)", "불량 없음", "이상 없음", true),
            new("극 방향", "정방향", "정방향", true),
            new("무부하 전압 (평균)", "1.55 V 이상", $"{vOpen:0.000} V", vOpen >= 1.55),
            new("부하 전압 (평균)", "1.45 V 이상", $"{vLoad:0.000} V", vLoad >= 1.45),
            new("내부저항 (평균)", "0.30 Ω 이하", $"{rInt:0.000} Ω", rInt <= 0.30),
            new("등급", "A", "A", true),
        };

        return new CertificateData(
            CertNo: $"QC-{o.No[3..]}",
            InspectedAt: at,
            RunId: $"{at:yyyyMMdd-HHmmss}-{rnd.Next(0x1000, 0xFFFF):x4}",
            Count: o.BatteryCount,
            Rows: rows);
    }
}
