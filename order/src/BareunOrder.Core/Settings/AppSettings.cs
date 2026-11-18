using System.IO;
using System.Text.Json;

namespace BareunOrder.Core.Settings;

/// <summary>
/// 실행 파일 옆 settings.json. settings.local.json이 있으면 그걸 먼저 쓴다 (개인 설정, git 제외).
/// </summary>
public sealed class AppSettings
{
    /// <summary>true면 가짜 주문이 저절로 들어온다 (브로커 없이 화면 만들 때)</summary>
    public bool UseFakeOrders { get; set; } = true;

    public string MqttHost { get; set; } = "127.0.0.1";
    public int MqttPort { get; set; } = 1883;
    public string MqttClientId { get; set; } = "order_admin";
    public string? MqttUser { get; set; }
    public string? MqttPassword { get; set; }

    /// <summary>화면 위쪽에 보일 담당자 이름 (로그인 붙기 전까지)</summary>
    public string Operator { get; set; } = "관리자";

    /// <summary>새 주문 알림 소리</summary>
    public bool PlaySound { get; set; } = true;

    /// <summary>재고 부족으로 표시할 팩 수 (이하이면 빨간색)</summary>
    public int LowStockPacks { get; set; } = 5;

    /// <summary>
    /// 켤 때 지난 주문·문의를 읽어 올 MySQL 연결 (읽기 전용 계정 ui_reader).
    /// 비밀번호가 들어가므로 settings.local.json(git 제외)에만 적는다. 비어 있거나 가짜 모드면 안 읽음.
    /// </summary>
    public string? MySqlConnection { get; set; }

    /// <summary>DB에서 읽어 올 기간(일). 이보다 오래돼도 안 끝난 주문·답변 안 한 문의는 읽음</summary>
    public int DbLoadDays { get; set; } = 30;

    public static AppSettings Load(string folder)
    {
        foreach (var name in new[] { "settings.local.json", "settings.json" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                if (s != null) return s;
            }
            catch (JsonException) { /* 형식이 틀리면 기본값 */ }
        }
        return new AppSettings();
    }
}
