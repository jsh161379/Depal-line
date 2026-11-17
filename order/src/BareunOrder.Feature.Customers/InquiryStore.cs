using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Data;
using BareunOrder.Core.Models;
using BareunOrder.Core.Settings;

namespace BareunOrder.Feature.Customers;

/// <summary>
/// 문의 기록 파일 (data/inquiries.json, 가짜 모드는 data/inquiries_fake.json — 가짜 문의가 실제 모드에 섞이지 않게). 문의 받기·답변 보내기는 Core(OrderStore)가 하고,
/// 이 클래스는 OrderStore.Inquiries를 껐다 켜도 남게 저장·불러오기만 한다.
/// - 켤 때: 파일 → store.Inquiries (Core가 브로커에 붙기 전이라, 다시 온 문의를 같은 번호로 알아봄)
/// - 문의가 늘거나 답변이 바뀌면: 파일에 다시 씀
/// TODO: 공통에 MySQL이 붙으면 DB inquiries 표에서 읽기
/// </summary>
public sealed class InquiryArchive
{
    private readonly OrderStore _store;
    private readonly string _file;
    private bool _loading;

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public InquiryArchive(OrderStore store, AppSettings settings)
    {
        _store = store;
        _file = Path.Combine(AppContext.BaseDirectory, "data", settings.UseFakeOrders ? "inquiries_fake.json" : "inquiries.json");
        Load();
        foreach (var q in _store.Inquiries) q.PropertyChanged += OnInquiryChanged;
        _store.Inquiries.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>문의가 늘거나 답변이 바뀜 (목록·배지 다시 그리기용)</summary>
    public event Action? Changed;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Inquiry q in e.NewItems) q.PropertyChanged += OnInquiryChanged;
        if (_loading) return;
        Save();
        Changed?.Invoke();
    }

    private void OnInquiryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Inquiry.Answer)) return;
        Save();
        Changed?.Invoke();
    }

    private sealed record Saved(string No, DateTime AskedAt, string Name, string Phone, string Type,
                                string? OrderNo, string Question, string? Answer, DateTime? AnsweredAt, string? AnsweredBy);

    private void Load()
    {
        if (!File.Exists(_file)) return;
        _loading = true;
        try
        {
            var list = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_file)) ?? new();
            foreach (var s in list.OrderByDescending(x => x.AskedAt))
            {
                if (_store.Inquiries.Any(q => q.No == s.No)) continue;
                _store.Inquiries.Add(new Inquiry
                {
                    No = s.No, AskedAt = s.AskedAt, Name = s.Name, Phone = s.Phone, Type = s.Type,
                    OrderNo = s.OrderNo, Question = s.Question,
                    Answer = s.Answer, AnsweredAt = s.AnsweredAt, AnsweredBy = s.AnsweredBy,
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"문의 기록을 읽지 못했습니다 ({_file}).\n{ex.Message}", "고객·문의",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var list = _store.Inquiries.Select(q => new Saved(q.No, q.AskedAt, q.Name, q.Phone, q.Type, q.OrderNo,
                                                              q.Question, q.Answer, q.AnsweredAt, q.AnsweredBy)).ToList();
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOut));
            File.Move(tmp, _file, overwrite: true);   // 반쯤 쓴 파일이 남지 않게
        }
        catch (Exception ex)
        {
            _store.Write($"문의 기록 저장 실패: {ex.Message}", "문의");
        }
    }
}

/// <summary>
/// 고객 메모 (예: "부재 시 경비실"). 연락처 숫자로 묶어 data/customer_memos.json에 저장 (가짜 모드는 customer_memos_fake.json).
/// DB에 표가 없어서 이 PC에만 남는다.
/// </summary>
public sealed class CustomerMemoStore
{
    private readonly string _file;
    private readonly Dictionary<string, string> _memos;

    public CustomerMemoStore(AppSettings settings)
    {
        _file = Path.Combine(AppContext.BaseDirectory, "data", settings.UseFakeOrders ? "customer_memos_fake.json" : "customer_memos.json");
        try
        {
            _memos = File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new()
                : new();
        }
        catch (Exception)
        {
            _memos = new();
        }
    }

    public string Get(string phone) => _memos.TryGetValue(OrderJson.Digits(phone), out var m) ? m : "";

    /// <summary>저장. 성공하면 null, 실패하면 이유.</summary>
    public string? Set(string phone, string memo)
    {
        var key = OrderJson.Digits(phone);
        memo = memo.Trim();
        if (memo.Length == 0) _memos.Remove(key);
        else _memos[key] = memo;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_memos, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
