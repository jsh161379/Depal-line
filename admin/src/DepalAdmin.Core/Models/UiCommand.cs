using System.Text.Json.Serialization;

namespace DepalAdmin.Core.Models;

/// <summary>ui/cmd — 화면 → 서버 버튼 명령. 통신 규약 v1.5.4 5장.</summary>
public sealed class UiCommand
{
    public string Action { get; set; } = "";
    public string Ts { get; set; } = "";
    /// <summary>로그인 계정 아이디. 로그인 정보가 없으면 생략한다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? By { get; set; }
}
