using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepalAdmin.Core.Settings;

/// <summary>
/// 실행 파일 옆의 settings.json을 읽는다.
/// settings.local.json이 있으면 그걸 대신 읽는다 (DB 비밀번호 등 개인 값, git에 안 올라감).
/// 설정 페이지에서 저장하면 settings.local.json에 쓴다.
/// </summary>
public sealed class AppSettings
{
    private const string SharedFile = "settings.json";
    private const string LocalFile = "settings.local.json";

    /// <summary>상단에 보이는 라인 이름</summary>
    public string StationName { get; set; } = "DEPAL-01";

    /// <summary>true면 브로커 없이 가짜 데이터로 실행 (화면 개발용)</summary>
    public bool UseFakeMqtt { get; set; } = true;

    /// <summary>true면 MySQL 없이 가짜 이력 데이터로 실행</summary>
    public bool UseFakeDb { get; set; } = true;

    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    /// <summary>
    /// 이 PC의 관리자 화면 번호 (1, 2, 3 …). 접속 이름이 "admin-01"처럼 된다 (규약 v1.4).
    /// PC마다 달라야 하므로 settings.local.json(이 PC에만 있는 파일)에 적는다. 0이면 PC 이름을 대신 씀.
    /// 화면·모니터에는 "관리자 화면 1 (로그인한 사람)"으로 보인다 (하트비트의 user 칸).
    /// </summary>
    public int AdminNo { get; set; } = 0;

    /// <summary>
    /// MQTT 접속 이름을 직접 지정할 때만 적는다 (보통은 비워 두고 AdminNo를 씀).
    /// 같은 이름으로 두 대가 접속하면 브로커가 먼저 붙은 쪽을 끊으므로 PC마다 달라야 한다.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// 실제로 쓰는 접속 이름 (하트비트 hb/·status/ 토픽, 채팅 연결 이름 …-chat 에도 이 이름을 씀).
    /// ClientId를 적었으면 그대로 → AdminNo가 1 이상이면 "admin-01" → 둘 다 없으면 "admin-PC이름"
    /// </summary>
    [JsonIgnore]
    public string EffectiveClientId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ClientId)) return ClientId.Trim();
            if (AdminNo > 0) return $"admin-{AdminNo:00}";
            return "admin-" + CleanName(Environment.MachineName);
        }
    }

    /// <summary>접속·토픽 이름에 쓸 수 있게 다듬음: 소문자, a-z·0-9·'-'만, 연속 '-'는 하나로, 앞뒤 '-' 제거</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (ok) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>읽기 전용 계정으로 접속 (DB 담당과 합의)</summary>
    public string MySqlConnection { get; set; } =
        "Server=localhost;Port=3306;Database=aa_battery_inspection;User ID=ui_reader;Password=CHANGE_ME;";

    /// <summary>실시간 목록(결과·이벤트·알람)에 남겨 둘 최대 줄 수</summary>
    public int MaxRowsInMemory { get; set; } = 500;

    /// <summary>아무 조작이 없으면 자동 로그아웃 (분). 0이면 끔.</summary>
    public int AutoLogoutMinutes { get; set; } = 10;

    /// <summary>ERROR 알람 때 소리</summary>
    public bool AlarmSound { get; set; } = true;

    /// <summary>계정·작업 기록을 저장할 폴더 (실행 파일 기준 상대 경로 가능)</summary>
    public string DataFolder { get; set; } = "data";

    public string DataPath(params string[] parts)
    {
        var root = Path.IsPathRooted(DataFolder) ? DataFolder : Path.Combine(AppContext.BaseDirectory, DataFolder);
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    public static AppSettings Load(string folder)
    {
        var local = Path.Combine(folder, LocalFile);
        var shared = Path.Combine(folder, SharedFile);
        var path = File.Exists(local) ? local : shared;
        if (!File.Exists(path)) return new AppSettings();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options) ?? new AppSettings();
    }

    /// <summary>지금 값을 settings.local.json에 저장</summary>
    public void SaveLocal(string folder)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(folder, LocalFile), JsonSerializer.Serialize(this, options));
    }
}
