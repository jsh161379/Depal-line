using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DepalAdmin.Core.Handover;
using DepalAdmin.Core.Settings;

namespace DepalAdmin.Feature.Handover;

/// <summary>
/// 인수인계 메모를 data/handover.json 한 파일에 저장한다.
/// 설정 UseFakeDb = true일 때 사용한다. 실제 DB 연결 시에는 MySqlHandoverStore를 쓴다 (HandoverModule에서 선택).
/// </summary>
public sealed class JsonHandoverStore : IHandoverStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<HandoverNote>? _notes;

    public event EventHandler? Changed;

    public JsonHandoverStore(AppSettings settings) => _path = settings.DataPath("handover.json");

    public async Task<IReadOnlyList<HandoverNote>> GetAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var notes = await LoadAsync(ct);
        return notes.Where(n => n.WrittenAt >= from && n.WrittenAt < to)
                    .OrderByDescending(n => n.WrittenAt)
                    .ToList();
    }

    public async Task AddAsync(HandoverNote note, CancellationToken ct = default)
    {
        var notes = await LoadAsync(ct);
        notes.Add(note);
        await SaveAsync(notes, ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkReadAsync(string noteId, string userId, CancellationToken ct = default)
    {
        var notes = await LoadAsync(ct);
        var note = notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null || note.IsReadBy(userId)) return;
        note.ReadBy.Add(userId);
        await SaveAsync(notes, ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<HandoverNote>> LoadAsync(CancellationToken ct)
    {
        if (_notes != null) return _notes;
        await _lock.WaitAsync(ct);
        try
        {
            if (_notes != null) return _notes;
            if (!File.Exists(_path)) return _notes = new List<HandoverNote>();
            await using var fs = File.OpenRead(_path);
            _notes = await JsonSerializer.DeserializeAsync<List<HandoverNote>>(fs, Options, ct) ?? new List<HandoverNote>();
            return _notes;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync(List<HandoverNote> notes, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";   // 쓰다가 꺼져도 원래 파일이 깨지지 않게 임시 파일에 먼저 쓴다
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(notes, Options), ct);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
