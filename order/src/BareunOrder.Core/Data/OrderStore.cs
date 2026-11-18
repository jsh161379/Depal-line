using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BareunOrder.Core.Data;

/// <summary>작업 기록 한 줄 (누가, 언제, 무엇을)</summary>
public sealed record WorkLogEntry(DateTime Time, string Who, string What);

/// <summary>
/// 주문·문의 보관함. 모든 탭이 같은 목록을 본다. (주문_통신_규약 v1.0의 "주문 관리 프로그램이 할 일"이 여기 있다)
/// - 화면은 Orders / Inquiries를 읽기만 하고, 바꿀 때는 꼭 아래 메서드를 쓴다
///   (ChangeStatusAsync, SetShippingInfoAsync, AnswerAsync, PublishStockAsync, PublishRunAsync)
///   → 그래야 홈페이지·DB 저장 프로그램에 알리고 작업 기록이 남는다.
/// - 저장은 DB 저장 프로그램이 order/accepted 등을 받아서 함. 켤 때 DB에서 지난 것을 읽어 옴(OrderDbReader).
/// </summary>
public sealed partial class OrderStore : ObservableObject
{
    private readonly IOrderClient _client;
    private readonly AppSettings _settings;
    private bool _started;

    public OrderStore(IOrderClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
        _client.NewOrderJson += json => OnUi(() => _ = OnNewOrderAsync(json));
        _client.CancelJson += json => OnUi(() => _ = OnCancelAsync(json));
        _client.NewInquiryJson += json => OnUi(() => _ = OnNewInquiryAsync(json));
        _client.ConnectionChanged += ok => OnUi(() =>
        {
            IsConnected = ok;
            Write(ok ? $"연결됨 ({_client.Description})" : "연결 끊김 — 다시 연결 중", "시스템");
        });
    }

    /// <summary>최신 주문이 위로</summary>
    public ObservableCollection<Order> Orders { get; } = new();

    /// <summary>고객 문의 (최신이 위로) — F5 고객·문의 탭</summary>
    public ObservableCollection<Inquiry> Inquiries { get; } = new();

    /// <summary>작업 기록 (최신이 위로)</summary>
    public ObservableCollection<WorkLogEntry> Log { get; } = new();

    [ObservableProperty] private bool _isConnected;

    public string ConnectionText => _client.Description;

    /// <summary>새 주문이 접수됨 (알림 띄우기용). 시작할 때 있던 주문에는 안 뜬다.</summary>
    public event Action<Order>? NewOrder;

    /// <summary>새 문의가 접수됨</summary>
    public event Action<Inquiry>? NewInquiry;

    /// <summary>주문이 추가되거나 상태가 바뀜 → 개수·배지를 다시 세면 된다</summary>
    public event Action? Changed;

    /// <summary>다른 탭에서 "이 주문 보여 줘" (예: 알림의 [보기] 버튼)</summary>
    public event Action<Order>? FocusRequested;

    public void RequestFocus(Order order) => FocusRequested?.Invoke(order);

    public int CountOf(OrderStatus s) => Orders.Count(o => o.Status == s);

    public IEnumerable<Order> Today => Orders.Where(o => o.ReceivedAt.Date == DateTime.Today);

    // ───────────────────── 화면에서 부르는 것 ─────────────────────

    /// <summary>주문 상태 바꾸기 → order/status/&lt;no&gt; 발행 (retained). 상태 변경은 여기서만.</summary>
    public async Task ChangeStatusAsync(Order order, OrderStatus to, string? by = null)
    {
        var from = order.StatusText;
        order.Status = to;
        Write($"{order.No} {from} → {order.StatusText}", by);
        await _client.PublishAsync(OrderTopics.Status(order.No), OrderJson.Status(order, by ?? _settings.Operator), retain: true);
    }

    public async Task SetShippingInfoAsync(Order order, string courier, string tracking)
    {
        order.Courier = courier;
        order.Tracking = tracking;
        Write($"{order.No} 송장 입력 ({courier} {tracking})");
        await _client.PublishAsync(OrderTopics.Status(order.No), OrderJson.Status(order, _settings.Operator), retain: true);
    }

    /// <summary>검사 성적서 번호 붙이기 (F6) → 상태 메시지에 실려 홈페이지 주문 조회에 보임</summary>
    /// <summary>검사 성적서 번호 저장·알림. 형식은 CERT-YYYYMMDD-XXXX (OrderJson.CertNoFor(order.No)로 만들면 됨)</summary>
    public async Task SetCertNoAsync(Order order, string certNo)
    {
        if (!OrderJson.CertNo.IsMatch(certNo))
            throw new ArgumentException($"성적서 번호 형식이 아닙니다: {certNo} (예: {OrderJson.CertNoFor(order.No)})", nameof(certNo));
        order.CertNo = certNo;
        Write($"{order.No} 성적서 {certNo}");
        await _client.PublishAsync(OrderTopics.Status(order.No), OrderJson.Status(order, _settings.Operator), retain: true);
    }

    /// <summary>문의에 답변 (F5) → inquiry/answer/&lt;no&gt; 발행 (retained). 고치면 다시 부르면 됨</summary>
    public async Task AnswerAsync(Inquiry q, string answer)
    {
        answer = answer.Trim();
        if (answer.Length is < 1 or > 1000) throw new ArgumentException("답변은 1~1000자");
        q.Answer = answer;
        q.AnsweredBy = _settings.Operator;
        q.AnsweredAt = DateTime.Now;
        Write($"{q.No} 문의 답변");
        await _client.PublishAsync(OrderTopics.InquiryAnswer(q.No), OrderJson.Answer(q), retain: true);
    }

    /// <summary>재고 입고·수정 (F3) → order/stock. kind: "restock"(더하기) / "adjust"(맞추기)</summary>
    public async Task PublishStockAsync(string productId, string kind, int packs)
    {
        Write($"재고 {(kind == "restock" ? "입고" : "수정")}: {productId} {packs}팩");
        await _client.PublishAsync(OrderTopics.Stock, OrderJson.Stock(productId, kind, packs, _settings.Operator), retain: false);
    }

    /// <summary>출고할 때 주문과 라인 회차 연결 (F2) → order/run</summary>
    public async Task PublishRunAsync(Order order, string runId, int batteryCount)
    {
        Write($"{order.No} 회차 연결 {runId} ({batteryCount}개)");
        await _client.PublishAsync(OrderTopics.Run, OrderJson.Run(order.No, runId, batteryCount), retain: false);
    }

    public void Write(string what, string? who = null)
    {
        Log.Insert(0, new WorkLogEntry(DateTime.Now, who ?? _settings.Operator, what));
        if (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }

    /// <summary>App 시작 때 한 번 부른다. 이 뒤로 들어오는 주문부터 알림이 뜬다.</summary>
    public async Task StartAsync()
    {
        foreach (var o in _client.InitialOrders) Insert(o);
        await LoadFromDbAsync();   // 브로커에 붙기 전에 → 다시 온 주문·문의를 같은 번호로 알아봄
        _started = true;
        await _client.StartAsync();
    }

    public Task StopAsync() => _client.StopAsync();

    /// <summary>실제 모드 + settings의 MySqlConnection이 있을 때만: DB의 지난 주문·문의를 목록에 넣는다</summary>
    private async Task LoadFromDbAsync()
    {
        if (_settings.UseFakeOrders || string.IsNullOrWhiteSpace(_settings.MySqlConnection)) return;
        try
        {
            var db = await Task.Run(() => OrderDbReader.LoadAsync(_settings.MySqlConnection!, _settings.DbLoadDays));
            var addedOrders = 0;
            foreach (var o in db.Orders)
            {
                if (Orders.Any(x => x.No == o.No)) continue;
                Insert(o);
                addedOrders++;
            }

            var addedInq = 0;
            foreach (var q in db.Inquiries)
            {
                if (Inquiries.FirstOrDefault(x => x.No == q.No) is { } old)
                {
                    if (!old.IsAnswered && q.IsAnswered)     // 파일보다 DB가 최신이면 답변을 채움
                    {
                        old.AnsweredBy = q.AnsweredBy;
                        old.AnsweredAt = q.AnsweredAt;
                        old.Answer = q.Answer;
                    }
                    continue;
                }
                var i = 0;
                while (i < Inquiries.Count && Inquiries[i].AskedAt > q.AskedAt) i++;   // 최신이 위
                Inquiries.Insert(i, q);
                addedInq++;
            }
            Write($"DB에서 불러옴: 주문 {addedOrders}건, 문의 {addedInq}건 (최근 {_settings.DbLoadDays}일 + 안 끝난 것)", "시스템");
        }
        catch (Exception ex)
        {
            Write($"DB 불러오기 실패 — 새 주문만 받습니다 ({ex.Message})", "시스템");
        }
    }

    // ───────────────────── 받은 것 처리 (규약 3·4장 순서) ─────────────────────

    /// <summary>
    /// order/new 처리: ① 형식 검사 → ② 같은 번호 확인 → ③ 가격 다시 계산 → ④ 추가 → order/accepted →
    /// order/status(received, 시각 = 받은 시각) → order/ack ok
    /// </summary>
    private async Task OnNewOrderAsync(string json)
    {
        var check = OrderJson.CheckNewOrder(json);                       // ①·③ (가격도 여기서 다시 계산)

        if (check.Order is { } incoming)
        {
            var old = Orders.FirstOrDefault(x => x.No == incoming.No);    // ②
            if (old != null)
            {
                if (OrderJson.SameContent(old, incoming))
                {
                    await Ack(incoming.No, true, null);                  // QoS 1 중복 → 처리 없이 ack만 다시
                    return;
                }
                Write($"{incoming.No} 같은 번호의 다른 주문 → 거절(DUPLICATE_NO)", "홈페이지");
                await Ack(incoming.No, false, "DUPLICATE_NO");
                return;
            }
        }

        if (check.Error != null)
        {
            Write($"{check.No ?? "(번호 없음)"} 주문 거절({check.Error})", "홈페이지");
            if (check.No != null) await Ack(check.No, false, check.Error);   // 번호도 못 읽으면 보낼 곳이 없음
            return;
        }

        var o = check.Order!;
        o.AcceptedAt = DateTime.Now;
        Insert(o);                                                        // ④
        await _client.PublishAsync(OrderTopics.Accepted, OrderJson.Accepted(o), retain: false);
        // 첫 상태 시각 = order/accepted의 ts (서버 시계). DB가 주문 저장 때 남긴 첫 기록과 같아 한 줄만 남는다
        await _client.PublishAsync(OrderTopics.Status(o.No), OrderJson.Status(o, _settings.Operator, o.AcceptedAt), retain: true);
        await Ack(o.No, true, null);

        Write($"{o.No} 새 주문 ({o.Name}, {o.Total:N0}원)", "홈페이지");
        if (_started) NewOrder?.Invoke(o);
    }

    private Task Ack(string no, bool ok, string? error) =>
        _client.PublishAsync(OrderTopics.Ack, OrderJson.Ack(no, ok, error), retain: false);

    /// <summary>order/cancel: 연락처가 같고 "주문 접수" 상태일 때만 취소. 아니면 아무것도 안 함 (응답 토픽 없음)</summary>
    private async Task OnCancelAsync(string json)
    {
        if (OrderJson.ParseCancel(json) is not { } req) return;
        var o = Orders.FirstOrDefault(x => x.No == req.No);
        if (o == null || OrderJson.Digits(o.Phone) != OrderJson.Digits(req.Phone)) return;
        if (o.Status != OrderStatus.Received)
        {
            if (o.Status != OrderStatus.Canceled) Write($"{o.No} 고객 취소 요청 — {o.StatusText} 상태라 취소 안 됨", "고객");
            return;
        }
        await ChangeStatusAsync(o, OrderStatus.Canceled, "고객");
    }

    /// <summary>inquiry/new: 형식 검사 → 같은 번호 확인 → 추가 → inquiry/accepted → inquiry/ack</summary>
    private async Task OnNewInquiryAsync(string json)
    {
        var check = OrderJson.CheckNewInquiry(json);
        if (check.Inquiry is { } incoming && Inquiries.FirstOrDefault(x => x.No == incoming.No) is { } old)
        {
            var same = OrderJson.SameContent(old, incoming);
            await InquiryAck(incoming.No, same, same ? null : "DUPLICATE_NO");
            return;
        }
        if (check.Error != null)
        {
            Write($"{check.No ?? "(번호 없음)"} 문의 거절({check.Error})", "홈페이지");
            if (check.No != null) await InquiryAck(check.No, false, check.Error);
            return;
        }

        var q = check.Inquiry!;
        Inquiries.Insert(0, q);
        await _client.PublishAsync(OrderTopics.InquiryAccepted, OrderJson.InquiryAccepted(q, DateTime.Now), retain: false);
        await InquiryAck(q.No, true, null);
        Write($"{q.No} 새 문의 ({q.TypeText})", "홈페이지");
        NewInquiry?.Invoke(q);
    }

    private Task InquiryAck(string no, bool ok, string? error) =>
        _client.PublishAsync(OrderTopics.InquiryAck, OrderJson.Ack(no, ok, error), retain: false);

    // ───────────────────── 내부 ─────────────────────

    private void Insert(Order o)
    {
        var i = 0;
        while (i < Orders.Count && Orders[i].ReceivedAt > o.ReceivedAt) i++;   // 시간 순서 (최신이 위)
        Orders.Insert(i, o);
        o.PropertyChanged += OnOrderChanged;
        Changed?.Invoke();
    }

    private void OnOrderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Order.Status)) Changed?.Invoke();
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
