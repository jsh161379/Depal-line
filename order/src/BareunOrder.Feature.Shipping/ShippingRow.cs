using BareunOrder.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Feature.Shipping;

/// <summary>탭 안에서만 쓰는 주문별 선택·송장 초안. 공통 Order 모델을 수정하지 않는다.</summary>
public sealed partial class ShippingRow : ObservableObject
{
    public const string FixedCourier = "CJ대한통운";
    public ShippingRow(Order order, Func<int>? available = null)
    {
        Order = order;
        _courier = order.Status == OrderStatus.Inspect ? FixedCourier : order.Courier ?? FixedCourier;
        _tracking = order.Tracking ?? "";
        Checklist = new ShippingChecklist(() => order.Status == OrderStatus.Inspect
            && !NeedsReview && TrackingRules.Error(Courier, Tracking) == null
            && order.BatteryCount > 0 && order.BatteryCount <= (available?.Invoke() ?? int.MaxValue));
        var previous = order.Status;
        order.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Order.Status)) return;
            if (order.Status == OrderStatus.Inspect && previous != OrderStatus.Inspect) Checklist.Reset();
            previous = order.Status;
            Checklist.NotifyEligibilityChanged();
        };
    }

    public Order Order { get; }
    public ShippingChecklist Checklist { get; }
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackingHint), nameof(TrackingError))]
    private string _courier;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackingError))]
    private string _tracking;
    [ObservableProperty] private string _result = "미처리";
    [ObservableProperty] private bool _needsReview;

    public string TrackingHint => TrackingRules.Hint(Courier);
    public string TrackingError => string.IsNullOrWhiteSpace(Tracking) ? "" : TrackingRules.Error(Courier, Tracking) ?? "";
    public string NormalizedTracking => TrackingRules.Normalize(Tracking);

    partial void OnCourierChanged(string? oldValue, string newValue)
    {
        if (oldValue != newValue) Checklist.Reset();
        Checklist.NotifyEligibilityChanged();
    }
    partial void OnTrackingChanged(string? oldValue, string newValue)
    {
        if (TrackingRules.Normalize(oldValue) != TrackingRules.Normalize(newValue)) Checklist.Reset();
        Checklist.NotifyEligibilityChanged();
    }
    partial void OnNeedsReviewChanged(bool value) => Checklist.NotifyEligibilityChanged();
}
