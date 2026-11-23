using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepalAdmin.Core.Auth;
using System.IO;
using Microsoft.Win32;

namespace DepalAdmin.Feature.Auth.Audit;

/// <summary>
/// 누가 언제 무엇을 했는지 (로그인, 라인 명령, 비상정지, 알람 확인, 계정 변경, 설정 변경 …).
/// </summary>
public partial class AuditViewModel : ObservableObject
{
    private const string All = "전체";
    private readonly IAuditLog _audit;
    private readonly Session _session;
    private List<AuditEntry> _loaded = new();

    public ObservableCollection<AuditEntry> Rows { get; } = new();
    public ObservableCollection<string> Categories { get; } = new() { All };

    [ObservableProperty] private DateTime _from = DateTime.Today.AddDays(-6);
    [ObservableProperty] private DateTime _to = DateTime.Today;
    [ObservableProperty] private string _category = All;
    [ObservableProperty] private string _userText = "";
    [ObservableProperty] private string _status = "";

    public AuditViewModel(IAuditLog audit, Session session)
    {
        _audit = audit;
        _session = session;
    }

    partial void OnCategoryChanged(string value) => ApplyFilter();
    partial void OnUserTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        Status = "읽는 중…";
        _loaded = (await _audit.ReadAsync(From.Date, To.Date.AddDays(1))).ToList();

        var keep = Category;
        Categories.Clear();
        Categories.Add(All);
        foreach (var c in _loaded.Select(e => e.Category).Distinct().OrderBy(c => c)) Categories.Add(c);
        Category = Categories.Contains(keep) ? keep : All;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var e in _loaded.Where(e =>
                     (Category == All || e.Category == Category) &&
                     (string.IsNullOrWhiteSpace(UserText) ||
                      e.UserId.Contains(UserText, StringComparison.OrdinalIgnoreCase) ||
                      e.UserName.Contains(UserText, StringComparison.OrdinalIgnoreCase))))
        {
            Rows.Add(e);
        }
        Status = $"{Rows.Count}건";
    }

    /// <summary>
    /// 지금 화면에 보이는 기록(종류·사람 조건 적용)을 서식 있는 엑셀 파일로 저장한다.
    /// 시트: 작업 기록(주의할 기록은 주황색) · 요약(종류별·사람별·날짜별)
    /// </summary>
    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "작업 기록 엑셀로 저장",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = $"작업기록_{From:yyyyMMdd}-{To:yyyyMMdd}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var madeBy = _session.User is { } u ? $"{u.Id} ({u.Name})" : "-";
            AuditExcelReport.Save(dialog.FileName, Rows.ToList(), From, To, Category, UserText, madeBy);
            Status = $"저장함: {dialog.FileName}";
        }
        catch (IOException ex)
        {
            Status = $"저장하지 못했습니다. 같은 파일이 엑셀에 열려 있으면 닫고 다시 시도하세요. ({ex.Message})";
        }
    }
}
