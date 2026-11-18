using System.Text.Json;
using BareunOrder.Core.Models;

namespace BareunOrder.Core.Comm;

/// <summary>
/// 가짜 연결. 예시 주문 8건으로 시작하고, 30초마다 홈페이지에서 온 것처럼 새 주문(JSON)이 들어온다.
/// 세 번에 한 번은 고객 문의도 같이 온다. 보내는 것(PublishAsync)은 아무 데도 안 간다.
/// 브로커·홈페이지 없이 화면을 만들 때 쓴다 (settings.json "UseFakeOrders": true).
/// 이름·주소·연락처는 모두 지어낸 예시 데이터.
/// </summary>
public sealed class FakeOrderClient : IOrderClient
{
    private const string NoChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 0 O 1 I 제외

    private static readonly string[] Names = { "김민수", "이서연", "박지훈", "최유진", "정하늘", "한도윤", "오세린", "윤재민", "장수아", "임태현" };
    private static readonly string[] Towns = { "서울 관악구 봉천동", "서울 동작구 상도동", "경기 성남시 분당구", "인천 남동구 구월동", "경기 수원시 영통구", "서울 마포구 합정동" };
    private static readonly string[] Memos = { "", "", "", "문 앞에 놓아 주세요", "부재 시 경비실", "배송 전 연락 부탁드립니다" };
    private static readonly (string Type, string Text)[] Questions =
    {
        ("delivery", "언제 출고되나요? 이번 주 안에 받아야 해서요."),
        ("product", "충전지는 몇 번 정도 다시 쓸 수 있나요?"),
        ("bulk", "24개입 20박스 주문하려고 하는데 견적 받을 수 있을까요?"),
        ("cancel", "받은 건전지 하나가 금방 닳았어요. 교환되나요?"),
    };

    private readonly Random _rnd = new();
    private Timer? _timer;
    private int _tick;

    public event Action<string>? NewOrderJson;
    public event Action<string>? CancelJson;
    public event Action<string>? NewInquiryJson;
    public event Action<bool>? ConnectionChanged;

    public FakeOrderClient()
    {
        var statuses = new[]
        {
            OrderStatus.Delivered, OrderStatus.Delivered, OrderStatus.Shipping, OrderStatus.Shipping,
            OrderStatus.Inspect, OrderStatus.Inspect, OrderStatus.Received, OrderStatus.Canceled,
        };
        var start = DateTime.Today.AddHours(8).AddMinutes(10);
        InitialOrders = statuses.Select((s, i) => MakeOrder(start.AddMinutes(i * 37 + _rnd.Next(0, 15)), s)).ToList();
    }

    public bool IsConnected { get; private set; }
    public string Description => "가짜 주문 (30초마다 1건)";
    public IReadOnlyList<Order> InitialOrders { get; }

    public Task StartAsync()
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(true);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, string json, bool retain) => Task.CompletedTask;

    private void Tick()
    {
        _tick++;
        var o = MakeOrder(DateTime.Now, OrderStatus.Received);
        NewOrderJson?.Invoke(JsonSerializer.Serialize(new
        {
            no = o.No,
            ts = OrderJson.Ts(o.ReceivedAt),
            name = o.Name,
            phone = o.Phone,
            addr = o.Address,
            memo = o.Memo,
            items = o.Items.Select(i => new { id = i.Id, name = i.Name, qty = i.Qty, price = i.Price }),
            total = o.Total,
        }));

        if (_tick % 3 == 0)
        {
            var (type, text) = Questions[_rnd.Next(Questions.Length)];
            NewInquiryJson?.Invoke(JsonSerializer.Serialize(new
            {
                no = $"Q-{DateTime.Now:yyyyMMdd}-{RandomCode()}",
                ts = OrderJson.Now(),
                name = Names[_rnd.Next(Names.Length)],
                phone = $"010-{_rnd.Next(1000, 9999)}-{_rnd.Next(1000, 9999)}",
                type,
                order_no = type is "delivery" or "cancel" ? o.No : null,
                question = text,
            }));
        }
    }

    private string RandomCode() => new(Enumerable.Range(0, 4).Select(_ => NoChars[_rnd.Next(NoChars.Length)]).ToArray());

    private Order MakeOrder(DateTime at, OrderStatus status)
    {
        var lines = _rnd.Next(1, 3);
        var items = Products.All.OrderBy(_ => _rnd.Next()).Take(lines)
            .Select(p => new OrderItem(p.Id, p.Name, _rnd.Next(1, 4), p.Price))
            .ToList();

        return new Order
        {
            No = $"BR-{at:yyyyMMdd}-{RandomCode()}",
            ReceivedAt = at,
            Name = Names[_rnd.Next(Names.Length)],
            Phone = $"010-{_rnd.Next(1000, 9999)}-{_rnd.Next(1000, 9999)}",
            Address = $"{Towns[_rnd.Next(Towns.Length)]} {_rnd.Next(1, 300)}-{_rnd.Next(1, 30)}",
            Memo = Memos[_rnd.Next(Memos.Length)],
            Items = items,
            Total = items.Sum(i => i.Amount),
            Status = status,
            Courier = status is OrderStatus.Shipping or OrderStatus.Delivered ? "CJ대한통운" : null,
            Tracking = status is OrderStatus.Shipping or OrderStatus.Delivered ? $"6{_rnd.Next(10000000, 99999999)}{_rnd.Next(100, 999)}" : null,
        };
    }
}
