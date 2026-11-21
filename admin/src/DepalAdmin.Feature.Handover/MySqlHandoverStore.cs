using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using DepalAdmin.Core.Handover;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Settings;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MySqlConnector;

namespace DepalAdmin.Feature.Handover;

/// <summary>
/// 담당: 유동호 — 교대 인수인계 MySQL + MQTT 저장소 (DB_인수인계_변경사항_2026-11-19 기준).
/// - 읽기(GetAsync): MySQL 읽기 전용 계정(ui_reader)으로 handover_notes / handover_reads 조회
/// - 쓰기(AddAsync / MarkReadAsync): MQTT 토픽(handover/note, handover/read) 발행 → mqtt_db_writer.py가 DB 저장
/// - 다중 PC 실시간 동기화: 동일 토픽을 구독해 다른 PC의 작성/읽음 이벤트를 수신하면 즉시 캐시 갱신 및 Changed 발생
/// </summary>
public sealed class MySqlHandoverStore : IHandoverStore, IDisposable {
    // 통신 담당(조시현) 확정 전 토픽 상수 (DB_인수인계_변경사항_2026-11-19 1·3장)
    public const string TopicHandoverNote = "handover/note";
    public const string TopicHandoverRead = "handover/read";

    private const int MaxNotes = 200;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    // 1) 기간 내 메모 목록 조회 (DB_인수인계_변경사항_2026-11-19 5장 쿼리 1 기반, 상한 @to 포함)
    private const string SelectNotesSql = @"
SELECT n.note_id, n.written_at, n.author_id, n.author_name, n.note_text,
       n.line_mode, n.source_run_id, n.unacked_alarms
FROM handover_notes n
WHERE n.written_at >= @from AND n.written_at < @to
ORDER BY n.written_at DESC
LIMIT @limit;";

    // 2) 해당 기간 메모들의 읽음 기록 조회 (5장 쿼리 2 기반)
    private const string SelectReadsSql = @"
SELECT r.note_id, r.user_id, r.read_at
FROM handover_reads r
JOIN handover_notes n ON n.note_id = r.note_id
WHERE n.written_at >= @from AND n.written_at < @to
ORDER BY r.note_id, r.read_at;";

    private readonly AppSettings _settings;
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _mqtt;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _mqttLoop;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, HandoverNote> _cache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public MySqlHandoverStore(AppSettings settings) {
        _settings = settings;
        _mqtt = _factory.CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += OnMqttMessageAsync;

        if (!_settings.UseFakeMqtt) {
            _mqttLoop = Task.Run(() => KeepConnectedAsync(_cts.Token));
        }
    }

    public async Task<IReadOnlyList<HandoverNote>> GetAsync(DateTime from, DateTime to, CancellationToken ct = default) {
        var dbNotes = new Dictionary<string, HandoverNote>(StringComparer.OrdinalIgnoreCase);

        await using var conn = new MySqlConnection(_settings.MySqlConnection);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new MySqlCommand(SelectNotesSql, conn)) {
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            cmd.Parameters.AddWithValue("@limit", MaxNotes);

            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false)) {
                var id = rd.GetString(0).ToLowerInvariant();
                dbNotes[id] = new HandoverNote {
                    Id = id,
                    WrittenAt = rd.GetDateTime(1),
                    AuthorId = rd.GetString(2),
                    AuthorName = rd.GetString(3),
                    Text = rd.GetString(4),
                    LineMode = rd.IsDBNull(5) ? "" : rd.GetString(5),
                    RunId = rd.IsDBNull(6) ? null : rd.GetString(6),
                    UnackedAlarms = rd.IsDBNull(7) ? 0 : Convert.ToInt32(rd.GetValue(7)),
                };
            }
        }

        if (dbNotes.Count > 0) {
            await using var readCmd = new MySqlCommand(SelectReadsSql, conn);
            readCmd.Parameters.AddWithValue("@from", from);
            readCmd.Parameters.AddWithValue("@to", to);

            await using var rd = await readCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false)) {
                var noteId = rd.GetString(0).ToLowerInvariant();
                var userId = rd.GetString(1);
                if (dbNotes.TryGetValue(noteId, out var note) && !note.IsReadBy(userId))
                    note.ReadBy.Add(userId);
            }
        }

        // 캐시 병합 (DB 저장 프로그램 반영 직전 MQTT로 먼저 받은 항목 유실 방지)
        lock (_cacheLock) {
            foreach (var kv in dbNotes) {
                if (_cache.TryGetValue(kv.Key, out var cached)) {
                    foreach (var uid in cached.ReadBy) {
                        if (!kv.Value.IsReadBy(uid))
                            kv.Value.ReadBy.Add(uid);
                    }
                }
                _cache[kv.Key] = kv.Value;
            }

            return _cache.Values
                .Where(n => n.WrittenAt >= from && n.WrittenAt < to)
                .OrderByDescending(n => n.WrittenAt)
                .ToList();
        }
    }

    public async Task AddAsync(HandoverNote note, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(note.Id))
            note.Id = Guid.NewGuid().ToString("N");
        note.Id = note.Id.ToLowerInvariant();

        if (!IsAsciiPrintable(note.AuthorId))
            throw new InvalidOperationException("작성자 계정 ID는 영문·숫자(ASCII)만 사용할 수 있습니다.");

        // 1) MQTT handover/note 발행 먼저 수행 (전송 실패 시 캐시 오염 방지)
        if (!_settings.UseFakeMqtt) {
            EnsureMqttConnected();
            var dto = new HandoverNotePayload {
                NoteId = note.Id,
                Ts = note.WrittenAt.ToString(Json.TimeFormat, CultureInfo.InvariantCulture),
                AuthorId = note.AuthorId,
                AuthorName = note.AuthorName,
                Text = note.Text,
                LineMode = string.IsNullOrWhiteSpace(note.LineMode) ? null : note.LineMode,
                RunId = string.IsNullOrWhiteSpace(note.RunId) ? null : note.RunId.ToLowerInvariant(),
                UnackedAlarms = Math.Max(0, note.UnackedAlarms),
            };
            var payload = JsonSerializer.Serialize(dto, Json.Options);
            await PublishAsync(TopicHandoverNote, payload, ct).ConfigureAwait(false);
        }

        // 2) 발행 성공 후 내 화면 캐시에 반영 (에코로 이미 들어온 읽음 목록 유지)
        lock (_cacheLock) {
            if (_cache.TryGetValue(note.Id, out var existing)) {
                foreach (var uid in existing.ReadBy) {
                    if (!note.IsReadBy(uid))
                        note.ReadBy.Add(uid);
                }
            }
            _cache[note.Id] = note;
        }

        RaiseChangedOnUi();
    }

    public async Task MarkReadAsync(string noteId, string userId, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(noteId) || string.IsNullOrWhiteSpace(userId))
            return;

        if (!IsAsciiPrintable(userId))
            throw new InvalidOperationException("읽음 확인 계정 ID는 영문·숫자(ASCII)만 사용할 수 있습니다.");

        var normalizedId = noteId.ToLowerInvariant();

        lock (_cacheLock) {
            if (_cache.TryGetValue(normalizedId, out var existing) && existing.IsReadBy(userId))
                return;
        }

        // 1) MQTT handover/read 발행 먼저 수행 (전송 실패 시 재시도 가능하도록 캐시 보존)
        if (!_settings.UseFakeMqtt) {
            EnsureMqttConnected();
            var dto = new HandoverReadPayload {
                NoteId = normalizedId,
                UserId = userId,
                Ts = Json.Now(),
            };
            var payload = JsonSerializer.Serialize(dto, Json.Options);
            await PublishAsync(TopicHandoverRead, payload, ct).ConfigureAwait(false);
        }

        // 2) 발행 성공 후 캐시 반영
        var added = false;
        lock (_cacheLock) {
            if (_cache.TryGetValue(normalizedId, out var note) && !note.IsReadBy(userId)) {
                note.ReadBy.Add(userId);
                added = true;
            }
        }

        if (added)
            RaiseChangedOnUi();
    }

    private void EnsureMqttConnected() {
        if (!_mqtt.IsConnected)
            throw new InvalidOperationException("MQTT 브로커에 연결되어 있지 않아 인수인계 내용을 전송하지 못했습니다.");
    }

    private Task PublishAsync(string topic, string payload, CancellationToken ct) {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();
        return _mqtt.PublishAsync(message, ct);
    }

    private async Task KeepConnectedAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            if (!_mqtt.IsConnected) {
                try {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(_settings.BrokerHost, _settings.BrokerPort)
                        .WithClientId(_settings.EffectiveClientId + "-handover")
                        .WithCleanSession(true)
                        .Build();

                    await _mqtt.ConnectAsync(options, ct).ConfigureAwait(false);

                    var sub = _factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(TopicHandoverNote).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(f => f.WithTopic(TopicHandoverRead).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build();
                    await _mqtt.SubscribeAsync(sub, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Debug.WriteLine($"[인수인계 MQTT] 연결 실패: {ex.Message}");
                }
            }

            try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private Task OnMqttMessageAsync(MqttApplicationMessageReceivedEventArgs e) {
        var seg = e.ApplicationMessage.PayloadSegment;
        if (seg.Array == null || seg.Count == 0)
            return Task.CompletedTask;

        var json = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
        var topic = e.ApplicationMessage.Topic;

        try {
            if (topic == TopicHandoverNote) {
                var dto = JsonSerializer.Deserialize<HandoverNotePayload>(json, Json.Options);
                if (dto == null || string.IsNullOrWhiteSpace(dto.NoteId))
                    return Task.CompletedTask;

                var id = dto.NoteId.ToLowerInvariant();
                var dirty = false;
                lock (_cacheLock) {
                    if (!_cache.ContainsKey(id)) {
                        var note = new HandoverNote {
                            Id = id,
                            WrittenAt = Json.ParseTs(dto.Ts) ?? DateTime.Now,
                            AuthorId = dto.AuthorId,
                            AuthorName = dto.AuthorName,
                            Text = dto.Text,
                            LineMode = dto.LineMode ?? "",
                            RunId = dto.RunId,
                            UnackedAlarms = dto.UnackedAlarms,
                        };
                        if (!string.IsNullOrWhiteSpace(dto.AuthorId))
                            note.ReadBy.Add(dto.AuthorId);

                        _cache[id] = note;
                        dirty = true;
                    }
                }
                if (dirty) RaiseChangedOnUi();
            } else if (topic == TopicHandoverRead) {
                var dto = JsonSerializer.Deserialize<HandoverReadPayload>(json, Json.Options);
                if (dto == null || string.IsNullOrWhiteSpace(dto.NoteId) || string.IsNullOrWhiteSpace(dto.UserId))
                    return Task.CompletedTask;

                var id = dto.NoteId.ToLowerInvariant();
                var dirty = false;
                lock (_cacheLock) {
                    if (_cache.TryGetValue(id, out var note) && !note.IsReadBy(dto.UserId)) {
                        note.ReadBy.Add(dto.UserId);
                        dirty = true;
                    }
                }
                if (dirty) RaiseChangedOnUi();
            }
        } catch (JsonException ex) {
            Debug.WriteLine($"[인수인계 MQTT] 형식 오류({topic}): {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void RaiseChangedOnUi() {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Changed?.Invoke(this, EventArgs.Empty);
        else
            dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static bool IsAsciiPrintable(string value) =>
        !string.IsNullOrEmpty(value) && value.All(ch => ch >= 0x20 && ch <= 0x7E);

    public void Dispose() {
        _cts.Cancel();
        try {
            if (_mqtt.IsConnected)
                _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
        } catch { }
        _mqtt.Dispose();
        _cts.Dispose();
    }

    private sealed class HandoverNotePayload {
        public string NoteId { get; set; } = "";
        public string Ts { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string Text { get; set; } = "";
        public string? LineMode { get; set; }
        public string? RunId { get; set; }
        public int UnackedAlarms { get; set; }
    }

    private sealed class HandoverReadPayload {
        public string NoteId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Ts { get; set; } = "";
    }
}