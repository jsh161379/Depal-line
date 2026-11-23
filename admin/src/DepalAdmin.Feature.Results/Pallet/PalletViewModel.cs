using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DepalAdmin.Core;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Feature.Results.Pallet;

/// <summary>
/// 담당: 김하균 — 팔레트 2×3 맵 (김초아의 라인 현황 화면 가운데에 끼움). 11/20 2×5 → 2×4, 11/23 2×4 → 2×3 (규약 v1.5.3, 퍽 6개)
/// 받는 것: server/results (칸 번호·등급·건전지 번호)
///          server/line은 회차가 바뀌었는지(run_id)만 본다 — 새 회차면 칸을 비움
/// TODO(김하균): 칸 번호를 실물 팔레트 방향과 맞추기 (지금은 왼쪽 위 = [0,0] = 1번)
/// </summary>
public partial class PalletViewModel : ObservableObject
{
    // ── 수정할 값 (실물 팔레트에 맞출 것) ──────────────────
    internal const int Rows = 2;
    internal const int Cols = 3;   // 검사 결과 표의 칸 번호(ResultRow.Cell)도 이 값을 씀. PalletView.xaml Columns와 같게
    // ──────────────────────────────────────────────────────

    private string? _currentRun;

    public ObservableCollection<PalletCell> Pallet { get; } = new();

    public PalletViewModel(ILineClient client)
    {
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                Pallet.Add(new PalletCell(r, c, r * Cols + c + 1));

        client.LineStatusReceived += (_, line) => NewRunCheck(line.RunId);
        client.ResultReceived += (_, result) => Apply(result);
        if (client.LastLine != null) NewRunCheck(client.LastLine.RunId);
    }

    private void NewRunCheck(string? runId)
    {
        if (runId == _currentRun) return;
        _currentRun = runId;
        foreach (var cell in Pallet) cell.Clear();
    }

    private void Apply(BatteryResult r)
    {
        NewRunCheck(r.RunId);
        if (r.PalletCell is not { Length: 2 } cell) return;
        if (cell[0] < 0 || cell[0] >= Rows || cell[1] < 0 || cell[1] >= Cols) return;   // 칸 범위 밖 (예: 열 3)이면 다음 줄로 넘어가지 않게 버림
        var index = cell[0] * Cols + cell[1];

        foreach (var p in Pallet) p.IsLatest = false;
        Pallet[index].Set(r.Grade, r.BatteryId);
    }
}

/// <summary>팔레트 한 칸. Slot은 DB의 pallet_slot과 같은 번호 (행×3 + 열 + 1, 1~6)</summary>
public partial class PalletCell : ObservableObject
{
    public PalletCell(int row, int col, int slot)
    {
        Row = row;
        Col = col;
        Slot = slot;
    }

    public int Row { get; }
    public int Col { get; }
    public int Slot { get; }

    [ObservableProperty] private string _grade = "";
    [ObservableProperty] private string _gradeText = "";
    [ObservableProperty] private string _battery = "";
    [ObservableProperty] private bool _isLatest;

    public void Set(string grade, int batteryId)
    {
        Grade = grade;
        GradeText = Labels.Grade(grade);
        Battery = $"#{batteryId}";
        IsLatest = true;
    }

    public void Clear()
    {
        Grade = "";
        GradeText = "";
        Battery = "";
        IsLatest = false;
    }
}
