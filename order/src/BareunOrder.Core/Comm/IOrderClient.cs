using BareunOrder.Core.Models;

namespace BareunOrder.Core.Comm;

/// <summary>
/// 홈페이지·DB 저장 프로그램과 주고받는 연결 (주문_통신_규약 v1.0).
/// 연결은 "받은 글(JSON)을 넘겨주고, 시키는 토픽에 보내기"만 한다.
/// 검사·중복 확인·가격 계산 같은 판단은 OrderStore가 한다 (가짜·실제 연결이 같은 규칙을 쓰게).
/// 이벤트는 백그라운드 스레드에서 올 수 있다 → 화면은 OrderStore를 통해서만 쓴다.
/// </summary>
public interface IOrderClient
{
    /// <summary>order/new 로 온 글 (검사 전 원문)</summary>
    event Action<string>? NewOrderJson;

    /// <summary>order/cancel 로 온 글</summary>
    event Action<string>? CancelJson;

    /// <summary>inquiry/new 로 온 글</summary>
    event Action<string>? NewInquiryJson;

    /// <summary>브로커 연결이 바뀜 (true = 연결됨)</summary>
    event Action<bool>? ConnectionChanged;

    bool IsConnected { get; }

    /// <summary>화면에 보일 연결 설명 (예: "MQTT 192.168.0.158:1883")</summary>
    string Description { get; }

    /// <summary>시작할 때 이미 있는 주문 (가짜 연결의 예시 주문. 실제 연결은 비어 있음)</summary>
    IReadOnlyList<Order> InitialOrders { get; }

    Task StartAsync();
    Task StopAsync();

    /// <summary>토픽에 JSON 보내기 (QoS 1)</summary>
    Task PublishAsync(string topic, string json, bool retain);
}

/// <summary>주문·문의 토픽 (주문_통신_규약 v1.0, 3·4장)</summary>
public static class OrderTopics
{
    // ── 주문 (3장) ──
    public const string New = "order/new";              // 홈페이지 → 주문 관리
    public const string Ack = "order/ack";              // 주문 관리 → 홈페이지
    public const string Cancel = "order/cancel";        // 홈페이지 → 주문 관리 (고객 취소)
    public const string Accepted = "order/accepted";    // 주문 관리 → DB 저장 (검사 통과한 주문)
    public const string Stock = "order/stock";          // 주문 관리(재고 탭) → DB 저장
    public const string Run = "order/run";              // 주문 관리(출고 탭) → DB 저장
    public static string Status(string orderNo) => $"order/status/{orderNo}";   // retained

    // ── 문의 (4장) ──
    public const string InquiryNew = "inquiry/new";
    public const string InquiryAck = "inquiry/ack";
    public const string InquiryAccepted = "inquiry/accepted";
    public static string InquiryAnswer(string no) => $"inquiry/answer/{no}";     // retained
}
