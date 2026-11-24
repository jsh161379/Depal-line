using System.Windows.Threading;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using MySqlConnector;

namespace BareunOrder.Core.Data;

/// <summary>
/// 라인 회차 하나의 A 등급 건전지 수 (출고·재고용).
/// Linked = 이미 주문에 연결된 수 (order_runs), Available = 아직 안 나간 수.
/// </summary>
public sealed record LineRun(string RunId, DateTime StartedAt, DateTime? EndedAt, int ACount, int Linked)
{
    public int Available => Math.Max(0, ACount - Linked);
    public string Text => $"{StartedAt:MM-dd HH:mm}  A {ACount}개 · 나감 {Linked} · 남음 {Available}";
}

/// <summary>주문 하나를 출고할 때 회차에서 꺼낼 몫 (order/run 한 건)</summary>
public sealed record RunTake(string RunId, int Count);

/// <summary>
/// 공통: 라인에서 검사 통과한 A 등급 건전지가 얼마나 남았는지 (F2 출고·배송, F3 재고가 같이 씀).
/// 주문_통신_규약 v1.0.2 6장 안 A — DB를 읽는다 (읽기 전용 계정 ui_reader).
///
///   출고 가능 A = Σ 회차별 (A 등급 수 − 그 회차로 이미 주문에 연결된 수)
///
/// - 실제 모드: 켤 때 + 1분마다 + RefreshAsync()로 DB 조회. 최근 DbLoadDays일 회차만 본다.
/// - 가짜 모드(또는 MySqlConnection 없음): 예시 회차 3개. PublishRunAsync로 연결하면 그만큼 줄어든다.
/// - 출고할 때: var takes = supply.Plan(order.BatteryCount); → null이면 부족.
///   아니면 foreach (var t in takes) await store.PublishRunAsync(order, t.RunId, t.Count);
///   (오래된 회차부터 꺼낸다. 연결하면 OrderStore가 알려 줘서 숫자가 바로 줄어든다)
/// - 화면은 Available, TodayA, Runs, StatusText에 바인딩하고, 바뀌면 Changed 이벤트.
/// </summary>
public sealed partial class LineSupplyReader : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly OrderStore _store;
    private string? _lastLog;   // 같은 기록을 반복해서 남기지 않게
    private readonly Dictionary<string, int> _fakeLinked = new();   // 가짜 모드에서 연결한 수
    private readonly Dispatcher _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private DispatcherTimer? _timer;
    private int _loading;   // 0/1 — 동시에 두 번 읽지 않게

    public LineSupplyReader(AppSettings settings, OrderStore store)
    {
        _settings = settings;
        _store = store;
        store.RunLinked += OnRunLinked;
    }

    /// <summary>true면 예시 값 (가짜 모드 또는 DB 설정 없음)</summary>
    public bool IsExample => _settings.UseFakeOrders || string.IsNullOrWhiteSpace(_settings.MySqlConnection);

    /// <summary>회차 목록 (오래된 것부터). 남은 A가 0인 회차도 들어 있음</summary>
    [ObservableProperty] private IReadOnlyList<LineRun> _runs = Array.Empty<LineRun>();

    /// <summary>출고 가능 A 등급 수 (모든 회차의 남은 수 합)</summary>
    [ObservableProperty] private int _available;

    /// <summary>오늘 검사를 끝낸 A 등급 수 (참고용, 이미 나간 것 포함)</summary>
    [ObservableProperty] private int _todayA;

    /// <summary>마지막으로 읽은 시각</summary>
    [ObservableProperty] private DateTime? _readAt;

    /// <summary>마지막 읽기 실패 이유 (성공하면 null). 실패해도 이전 값은 그대로 둔다</summary>
    [ObservableProperty] private string? _error;

    /// <summary>화면 아래쪽에 띄울 한 줄</summary>
    public string StatusText =>
        Error != null ? $"라인 A 등급 읽기 실패 — {Error}"
        : ReadAt == null ? "라인 A 등급 읽는 중…"
        : IsExample ? $"예시 값 (가짜 모드) · {ReadAt:HH:mm:ss}"
        : $"DB 기준 {ReadAt:HH:mm:ss} (1분마다 갱신)";

    /// <summary>값이 바뀔 때마다 (UI 스레드)</summary>
    public event Action? Changed;

    /// <summary>App 시작 때 한 번 (UI 스레드에서). 바로 한 번 읽고 1분마다 다시 읽는다</summary>
    public async Task StartAsync()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), _ui);
        _timer.Start();
        await RefreshAsync();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>지금 다시 읽기 ([새로 고침] 버튼용)</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            if (IsExample && !_settings.UseFakeOrders)
            {
                // 실제 모드인데 DB 설정이 없음 (settings.local.json을 못 읽은 경우 등, 포트폴리오 ㊳).
                // 예시 값을 보이면 F2가 예시 회차 번호로 order/run을 보내 DB에서 버려진다 → 0개 + 빨간 글씨
                Apply(Array.Empty<LineRun>(), today: 0,
                      error: "DB 연결 설정이 없습니다 (settings.local.json의 MySqlConnection 확인)");
                return;
            }
            if (IsExample)
            {
                Apply(MakeExample(), today: 37, error: null);
                return;
            }
            var (runs, today) = await Task.Run(() => LoadAsync(_settings.MySqlConnection!, _settings.DbLoadDays));
            Apply(runs, today, error: null);
        }
        catch (Exception ex)
        {
            _ui.Invoke(() =>
            {
                Error = ex.Message;
                ReadAt ??= DateTime.Now;
                Log($"라인 A 등급 읽기 실패 ({ex.Message})");
                OnPropertyChanged(nameof(StatusText));
                Changed?.Invoke();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    /// <summary>
    /// 주문 하나에 필요한 개수를 오래된 회차부터 나눠 담는다. 모자라면 null.
    /// (DB는 바꾸지 않는다. 실제 연결은 store.PublishRunAsync로)
    /// </summary>
    public IReadOnlyList<RunTake>? Plan(int needed)
    {
        if (needed <= 0) return Array.Empty<RunTake>();
        if (needed > Available) return null;
        var takes = new List<RunTake>();
        var left = needed;
        foreach (var r in Runs)
        {
            if (left == 0) break;
            var n = Math.Min(left, r.Available);
            if (n <= 0) continue;
            takes.Add(new RunTake(r.RunId, n));
            left -= n;
        }
        return left == 0 ? takes : null;
    }

    /// <summary>A 등급 남은 수로 만들 수 있는 팩 수 (주문 대기분을 빼기 전)</summary>
    public int PacksFor(Product product) => product.PackSize <= 0 ? 0 : Available / product.PackSize;

    /// <summary>
    /// 주문 대기(접수·출고 검사)가 가져갈 A 등급 수. 결정 ⑪ 안 A (11/23):
    /// 재고 = 라인 A. 출고(F2) 때 회차를 연결하면 Available에서 이미 빠지므로 배송 중·배송 완료는 넣지 않는다.
    /// 판매 중인 상품(라인 알카라인 AA)만 센다 — 판매 중지 충전지(aar2)·목록에 없는 옛 상품은 라인 A를 쓰지 않음 (11/24,
    /// F3 표의 "주문 대기" 합과 같아지게)
    /// </summary>
    public int PendingNeed => _store.Orders
        .Where(o => o.Status is OrderStatus.Received or OrderStatus.Inspect)
        .SelectMany(o => o.Items)
        .Where(i => Products.Find(i.Id)?.OnSale == true)
        .Sum(i => i.BatteryCount);

    /// <summary>새 주문에 쓸 수 있는 A 등급 수 = 남음 − 주문 대기분 (0 아래로 안 내려감)</summary>
    public int Free => Math.Max(0, Available - PendingNeed);

    /// <summary>새 주문에 더 팔 수 있는 팩 수 (F3 "판매 가능"). 상품끼리 같은 A를 나눠 쓰므로 합계가 아니라 "이 상품만 판다면"의 값</summary>
    public int FreePacksFor(Product product) => product.PackSize <= 0 ? 0 : Free / product.PackSize;

    // ───────────────────────── 내부 ─────────────────────────

    private void Apply(IReadOnlyList<LineRun> runs, int today, string? error)
    {
        _ui.Invoke(() =>
        {
            Runs = runs;
            Available = runs.Sum(r => r.Available);
            TodayA = today;
            ReadAt = DateTime.Now;
            Error = error;
            Log($"라인 A 등급 남은 수 {Available}개 (회차 {runs.Count}개, 오늘 A {today}개{(IsExample ? ", 예시 값" : "")})");
            OnPropertyChanged(nameof(StatusText));
            Changed?.Invoke();
        });
    }

    /// <summary>작업 기록에 남김 (값이 바뀔 때만)</summary>
    private void Log(string text)
    {
        if (text == _lastLog) return;
        _lastLog = text;
        _store.Write(text, "시스템");
    }

    /// <summary>OrderStore.PublishRunAsync가 불리면: 가짜 모드는 바로 빼고, 실제 모드는 DB 저장을 잠깐 기다렸다가 다시 읽음</summary>
    private void OnRunLinked(Order order, string runId, int count)
    {
        if (IsExample)
        {
            _fakeLinked[runId] = _fakeLinked.GetValueOrDefault(runId) + count;
            _ = RefreshAsync();
            return;
        }
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => RefreshAsync());
    }

    private IReadOnlyList<LineRun> MakeExample()
    {
        var d = DateTime.Today;
        var runs = new[]
        {
            new LineRun($"{d.AddDays(-1):yyyyMMdd}-141000-ex01", d.AddDays(-1).AddHours(14).AddMinutes(10), d.AddDays(-1).AddHours(14).AddMinutes(18), 18, 12),
            new LineRun($"{d:yyyyMMdd}-093000-ex02", d.AddHours(9).AddMinutes(30), d.AddHours(9).AddMinutes(38), 21, 0),
            new LineRun($"{d:yyyyMMdd}-110500-ex03", d.AddHours(11).AddMinutes(5), null, 16, 0),
        };
        return runs.Select(r => r with { Linked = r.Linked + _fakeLinked.GetValueOrDefault(r.RunId) }).ToList();
    }

    /// <summary>
    /// 회차별 A 등급 수와 이미 연결된 수 (주문관리_조회쿼리.sql의 "오늘 A 등급 수"를 회차별로 넓힌 것).
    /// 11/20 팀 결정 ⑦: A 등급 = final_grade 'A' + sorted_destination 있음 (실제로 A 자리에 배출된 것만,
    /// 배출 실패는 dest가 null), runs.run_type = 'LINE'만 (BENCH_TEST 제외). 최근 days일에 시작한 회차.
    /// 권한: ui_reader에 runs·battery_cycles·order_runs SELECT (F6 성적서와 같음)
    /// </summary>
    private static async Task<(IReadOnlyList<LineRun> Runs, int TodayA)> LoadAsync(string cs, int days)
    {
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        var runs = new List<LineRun>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ru.source_run_id, ru.started_at, ru.ended_at,
                       COUNT(b.cycle_id)       AS a_count,
                       COALESCE(l.linked, 0)   AS linked
                FROM runs ru
                JOIN battery_cycles b ON b.run_id = ru.run_id AND b.final_grade = 'A'
                                     AND b.sorted_destination IS NOT NULL
                LEFT JOIN (SELECT source_run_id, SUM(battery_count) AS linked
                           FROM order_runs GROUP BY source_run_id) l
                       ON l.source_run_id = ru.source_run_id
                WHERE ru.run_type = 'LINE'
                  AND ru.source_run_id IS NOT NULL
                  AND ru.started_at >= NOW() - INTERVAL @days DAY
                GROUP BY ru.run_id, ru.source_run_id, ru.started_at, ru.ended_at, l.linked
                ORDER BY ru.started_at;
                """;
            cmd.Parameters.AddWithValue("@days", days);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                runs.Add(new LineRun(
                    RunId: r.GetString(0),
                    StartedAt: r.GetDateTime(1),
                    EndedAt: r.IsDBNull(2) ? null : r.GetDateTime(2),
                    ACount: Convert.ToInt32(r.GetValue(3)),
                    Linked: Convert.ToInt32(r.GetValue(4))));
            }
        }

        int today;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM battery_cycles b
                JOIN runs ru ON ru.run_id = b.run_id AND ru.run_type = 'LINE'
                WHERE b.final_grade = 'A' AND b.sorted_destination IS NOT NULL
                  AND b.finished_at >= CURDATE() AND b.finished_at < CURDATE() + INTERVAL 1 DAY;
                """;
            today = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        return (runs, today);
    }
}
