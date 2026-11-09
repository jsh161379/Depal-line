using System.Text.Json;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Comm;

/// <summary>
/// server/devices 해석.
/// central_server.py가 실제로 보내는 형식 (2026-11-09 확인):
///   {"run_id": "...", "ts": "...",
///    "devices": [{"name": "pi", "status": "ok", "age_sec": 0.4, "last_seq": 120,
///                 "info": {"gripper": "normal", "arm": "ok", "arduino": "ok"}}, ...]}
///   status 값: ok / warn / offline / unknown
/// 장치 하트비트의 추가 정보(info 안의 값)는 Extra에 바로 넣는다 → Extra["gripper"] 처럼 꺼내 쓴다.
/// 예전 형식 {"pi": {"state": "ok"}, "yolo": "ok"} 도 계속 받는다 (가짜 데이터·시험용).
/// </summary>
internal static class DeviceParser
{
    private static readonly HashSet<string> SkipKeys = new() { "ts", "run_id" };

    /// <summary>안의 값을 꺼내 Extra에 펼쳐 넣는 칸</summary>
    private const string InfoKey = "info";

    public static Dictionary<string, DeviceInfo> Parse(string json)
    {
        var result = new Dictionary<string, DeviceInfo>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("devices", out var inner))
            root = inner;

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (SkipKeys.Contains(p.Name)) continue;
                Add(result, p.Name, p.Value);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var name = ReadString(item, "name") ?? ReadString(item, "device") ?? "?";
                Add(result, name, item);
            }
        }
        return result;
    }

    private static void Add(Dictionary<string, DeviceInfo> target, string name, JsonElement value)
    {
        var info = new DeviceInfo { Name = name };

        if (value.ValueKind == JsonValueKind.String)
        {
            info.State = value.GetString() ?? "unknown";
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in value.EnumerateObject())
            {
                if (p.NameEquals("state") || p.NameEquals("status"))
                    info.State = Text(p.Value);
                else if (p.NameEquals(InfoKey) && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var q in p.Value.EnumerateObject())       // info 안의 값을 펼침
                        info.Extra[q.Name] = Text(q.Value);
                else if (!p.NameEquals("name") && !p.NameEquals("device"))
                    info.Extra[p.Name] = Text(p.Value);
            }
        }
        target[name] = info;
    }

    /// <summary>문자열은 그대로, 숫자·null 등은 JSON 글자 그대로 (null → "")</summary>
    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => v.GetRawText(),
    };

    private static string? ReadString(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
