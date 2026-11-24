using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Feature.Shipping;

/// <summary>주문별 출고 전 확인. 체크만 하거나 문서를 열기만 해서는 승인되지 않는다.</summary>
public sealed partial class ShippingChecklist : ObservableObject
{
    private readonly Func<bool> _eligible;
    private DateTime? _confirmedAt;
    private string _confirmedBy = "";
    public ShippingChecklist(Func<bool> eligible) => _eligible = eligible;

    [ObservableProperty] private bool _productChecked;
    [ObservableProperty] private bool _packagingChecked;
    [ObservableProperty] private bool _destinationChecked;
    public DateTime? ConfirmedAt => _confirmedAt;
    public string ConfirmedBy => _confirmedBy;
    public bool IsComplete => _confirmedAt != null && ProductChecked && PackagingChecked && DestinationChecked;
    public bool CanEdit => _eligible();
    public bool CanComplete => ProductChecked && PackagingChecked && DestinationChecked && !IsComplete && _eligible();
    public string StatusText => IsComplete ? $"출고 지시서 확인 완료 · {ConfirmedBy} · {ConfirmedAt:HH:mm}"
        : "출고 지시서 확인 필요";
    public event Action? Completed;

    partial void OnProductCheckedChanged(bool value) => Changed();
    partial void OnPackagingCheckedChanged(bool value) => Changed();
    partial void OnDestinationCheckedChanged(bool value) => Changed();

    private void Changed()
    {
        _confirmedAt = null;
        _confirmedBy = "";
        Notify();
    }
    public bool Complete(string staff)
    {
        if (!CanComplete) return false;
        _confirmedAt = DateTime.Now;
        _confirmedBy = string.IsNullOrWhiteSpace(staff) ? "담당자" : staff;
        Notify();
        Completed?.Invoke();
        return true;
    }
    public void Reset()
    {
        ProductChecked = false;
        PackagingChecked = false;
        DestinationChecked = false;
        Changed();
    }
    public void NotifyEligibilityChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanComplete));
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(ConfirmedAt), nameof(ConfirmedBy), nameof(IsComplete), nameof(CanComplete), nameof(StatusText) })
            OnPropertyChanged(name);
    }
}

public sealed record ShippingReviewRecord(bool Product, bool Packaging, bool Destination, DateTime? At, string By)
{
    public bool IsComplete => Product && Packaging && Destination && At.HasValue;
    public static ShippingReviewRecord From(ShippingChecklist checklist) => new(checklist.ProductChecked,
        checklist.PackagingChecked, checklist.DestinationChecked, checklist.ConfirmedAt, checklist.ConfirmedBy);
}
