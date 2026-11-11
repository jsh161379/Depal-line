namespace DepalAdmin.Core.Messaging;

/// <summary>
/// 채팅 메시지 1건. MQTT 토픽 chat/messages (Topics.ChatMessages), JSON snake_case.
/// ⚠ 통신 규약에 아직 없음 — 채팅 상대가 정해지면(관리자 여러 대 / 작업자 메인 프로그램) 규약에 추가.
/// </summary>
public sealed class ChatMessage
{
    public string MsgId { get; set; } = Guid.NewGuid().ToString("N");
    public string FromId { get; set; } = "";
    public string FromName { get; set; } = "";

    /// <summary>보낸 프로그램 (settings의 StationName). 같은 사람이 두 PC에 있을 때 구분.</summary>
    public string Station { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>붙여 보낸 것: "alarm" / "battery" / null</summary>
    public string? RefKind { get; set; }

    /// <summary>붙여 보낸 것의 내용 (알람 문구, "회차 #번호" 등)</summary>
    public string? RefText { get; set; }

    /// <summary>
    /// @로 부른 사람 (통신 규약 v1.3 5-1). 비어 있으면 그냥 단톡방 메시지.
    /// 받는 쪽은 id가 같거나, id가 비었으면 이름이 같을 때 "나를 부름"으로 본다
    /// (계정이 PC마다 따로라 id가 다를 수 있어서 이름도 같이 본다).
    /// </summary>
    public List<ChatMention> Mentions { get; set; } = new();

    public string Ts { get; set; } = Json.Now();
}

/// <summary>채팅에서 @로 부른 사람 한 명. Id는 모르면 빈 문자열.</summary>
public sealed class ChatMention
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>이 사람이 나인가 (나 = 지금 로그인한 계정)</summary>
    public bool Is(string myId, string myName) =>
        Id != "" ? string.Equals(Id, myId, StringComparison.OrdinalIgnoreCase) || Name == myName
                 : Name == myName;
}

/// <summary>
/// 채팅 창구. 구현: 조시현(Comm)의 MqttChatClient / FakeChatClient.
/// 이벤트는 UI 스레드에서 불린다. 내가 보낸 메시지도 MessageReceived로 한 번 돌아온다.
/// </summary>
public interface IChatClient
{
    event EventHandler<ChatMessage>? MessageReceived;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task SendAsync(ChatMessage message, CancellationToken ct = default);
}
