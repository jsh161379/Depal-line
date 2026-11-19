using System.ComponentModel;
using System.Media;
using System.Windows.Threading;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Pages;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BareunOrder.Shell;

/// <summary>탭 버튼 하나 (F키, 이름, 배지)</summary>
public sealed partial class TabVm : ObservableObject
{
    public TabVm(IPage page, int index)
    {
        Page = page;
        Key = $"F{index + 1}";
        if (page.ViewModel is IHasBadge b)
        {
            Badge = b.BadgeCount;
            b.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IHasBadge.BadgeCount)) Badge = b.BadgeCount;
            };
        }
    }

    public IPage Page { get; }
    public string Key { get; }
    public string Title => Page.Title;

    [ObservableProperty] private int _badge;
}

/// <summary>담당: (미정) — 메인 창 (위 상태줄, 탭, 새 주문·라인 메시지 알림, 아래 줄)</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly OrderStore _store;
    private readonly AppSettings _settings;
    private readonly Navigator _navigator;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _chatToastTimer;
    private readonly LineSupplyReader _supply;

    public MainViewModel(IEnumerable<IPage> pages, OrderStore store, ChatStore chat, Navigator navigator, AppSettings settings,
        LineSupplyReader supply)
    {
        _store = store;
        _supply = supply;
        supply.Changed += () => { OnPropertyChanged(nameof(LineText)); OnPropertyChanged(nameof(LineFailed)); };
        _settings = settings;
        _navigator = navigator;

        Tabs = pages.OrderBy(p => p.Order).Select((p, i) => new TabVm(p, i)).ToList();
        Selected = Tabs.FirstOrDefault();

        navigator.NavigateRequested += title =>
        {
            var t = Tabs.FirstOrDefault(x => x.Title == title);
            if (t != null) Selected = t;
        };

        _store.Changed += RefreshCounts;
        _store.NewOrder += ShowToast;
        _store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrderStore.IsConnected)) OnPropertyChanged(nameof(IsConnected));
        };
        _store.Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LastLog));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(Now));
        _clock.Start();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) => CloseToast();

        // 라인 메시지: 다른 탭을 보고 있을 때만 알림 (나를 부른 메시지는 소리를 ChatStore가 냄)
        _chatToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _chatToastTimer.Tick += (_, _) => CloseChatToast();
        chat.NewMessage += line =>
        {
            if (_navigator.CurrentTitle == ChatStore.TabTitle) return;
            ToastChat = line;
            _chatToastTimer.Stop();
            _chatToastTimer.Start();
        };
    }

    public IReadOnlyList<TabVm> Tabs { get; }

    [ObservableProperty] private TabVm? _selected;

    /// <summary>탭이 바뀌면 다른 화면에 알린다 (라인 메시지 탭을 열면 안 읽은 수 0 등)</summary>
    partial void OnSelectedChanged(TabVm? value) => _navigator.SetCurrent(value?.Title);

    public DateTime Now => DateTime.Now;
    public string Operator => _settings.Operator;
    public bool IsConnected => _store.IsConnected;
    public string ConnectionText => _store.ConnectionText;

    // ── 위 상태줄 숫자 ──
    [ObservableProperty] private int _todayCount;
    [ObservableProperty] private int _waitingCount;     // 주문 접수 + 출고 검사
    [ObservableProperty] private int _todaySales;

    /// <summary>아래 줄 가운데: 라인 A 등급 남은 수 (LineSupplyReader, 1분마다 갱신)</summary>
    public string LineText =>
        _supply.Error != null ? "라인 A 읽기 실패 — " + _supply.Error
        : _supply.ReadAt == null ? "라인 A 읽는 중…"
        : $"라인 A 남음 {_supply.Available}개 · 오늘 A {_supply.TodayA}개 · {_supply.ReadAt:HH:mm}{(_supply.IsExample ? " (예시)" : "")}";

    public bool LineFailed => _supply.Error != null;

    public string LastLog => _store.Log.Count == 0 ? "" : $"{_store.Log[0].Time:HH:mm:ss}  {_store.Log[0].What}";

    private void RefreshCounts()
    {
        var today = _store.Today.Where(o => o.Status != OrderStatus.Canceled).ToList();
        TodayCount = today.Count;
        TodaySales = today.Sum(o => o.Total);
        WaitingCount = _store.CountOf(OrderStatus.Received) + _store.CountOf(OrderStatus.Inspect);
    }

    public void SelectByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count) Selected = Tabs[index];
    }

    // ── 새 주문 알림 (오른쪽 아래, 8초 뒤 사라짐) ──
    [ObservableProperty] private Order? _toastOrder;

    private void ShowToast(Order o)
    {
        ToastOrder = o;
        if (_settings.PlaySound) SystemSounds.Asterisk.Play();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void CloseToast()
    {
        _toastTimer.Stop();
        ToastOrder = null;
    }

    [RelayCommand]
    private void OpenToastOrder()
    {
        if (ToastOrder == null) return;
        var o = ToastOrder;
        SelectByIndex(0);   // 주문 탭
        _store.RequestFocus(o);
        CloseToast();
    }

    // ── 라인 메시지 알림 (오른쪽 아래, 주문 알림 위, 8초 뒤 사라짐) ──
    [ObservableProperty] private ChatLine? _toastChat;

    [RelayCommand]
    private void CloseChatToast()
    {
        _chatToastTimer.Stop();
        ToastChat = null;
    }

    [RelayCommand]
    private void OpenToastChat()
    {
        _navigator.Navigate(ChatStore.TabTitle);
        CloseChatToast();
    }
}
