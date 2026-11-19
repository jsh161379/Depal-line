using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepalAdmin.Core;
using DepalAdmin.Core.Auth;
using DepalAdmin.Core.Messaging;
using DepalAdmin.Core.Models;

namespace DepalAdmin.Feature.Operation.Panel;

/// <summary>
/// 담당: 유동호 — 라인 조작 버튼 패널 (김초아의 라인 현황 화면 오른쪽에 들어감).
/// 받는 것: server/line (mode, step, message), server/events (시작·해제 거부, 장비 이상)
/// 보내는 것: ui/cmd (start, pause, resume, stop, skip, reset)
/// </summary>
public partial class ControlPanelViewModel : ObservableObject {
    private readonly ILineClient _client;
    private readonly Session _session;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand),
                                nameof(StopCommand), nameof(SkipCommand), nameof(ResetCommand))]
    private string _mode = "idle";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand),
                                nameof(StopCommand), nameof(SkipCommand), nameof(ResetCommand))]
    private string? _step;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand),
                                nameof(StopCommand), nameof(SkipCommand), nameof(ResetCommand))]
    private bool _canOperate;

    /// <summary>현재 상태·단계 요약 및 서버 안내 메시지 (이상·대기 시 표시)</summary>
    [ObservableProperty] private string _statusGuide = "";

    /// <summary>StatusToBrush / StatusToFill 변환기용 상태 키 (normal / warn / alarm)</summary>
    [ObservableProperty] private string _statusLevel = "normal";

    [ObservableProperty] private string _permissionHint = "";
    [ObservableProperty] private string _commandResult = "";

    public ControlPanelViewModel(ILineClient client, Session session) {
        _client = client;
        _session = session;

        _client.LineStatusReceived += (_, line) => ApplyLineStatus(line);
        _client.EventReceived += (_, ev) => ApplyLineEvent(ev);
        _session.Changed += (_, _) => UpdatePermission();

        if (_client.LastLine != null)
            ApplyLineStatus(_client.LastLine);
        else
            UpdateStatusGuide(null);

        UpdatePermission();
    }

    private void UpdatePermission() {
        CanOperate = _session.Can(Permission.OperateLine);
        PermissionHint = CanOperate ? "" : "보기 전용 계정이라 비상정지만 쓸 수 있습니다.";
    }

    private void ApplyLineStatus(LineStatus line) {
        Mode = line.Mode;
        Step = line.Step;
        UpdateStatusGuide(line.Message);
    }

    private void UpdateStatusGuide(string? lineMessage) {
        if (Mode == "estop") {
            StatusLevel = "alarm";
            StatusGuide = !string.IsNullOrWhiteSpace(lineMessage)
                ? lineMessage
                : Step == "reset"
                    ? "비상정지 해제 및 원점 복귀 중입니다. 잠시 기다려 주세요."
                    : "비상정지 상태 — 물리 비상정지 버튼을 돌려 복귀시킨 뒤 [비상정지 해제]를 누르세요.";
            return;
        }

        if (Mode == "paused") {
            StatusLevel = "warn";
            if (!string.IsNullOrWhiteSpace(lineMessage)) {
                StatusGuide = lineMessage;
            } else if (Step == "wait_operator") {
                StatusGuide = "작업자 확인 대기 — [재개]로 다시 시도하거나 [건너뛰기] / [회차 종료]를 선택하세요.";
            } else {
                StatusGuide = "일시정지 상태 — 현장 확인 후 [재개] 또는 [회차 종료]를 누르세요.";
            }
            return;
        }

        if (Mode == "draining") {
            StatusLevel = "warn";
            StatusGuide = !string.IsNullOrWhiteSpace(lineMessage)
                ? lineMessage
                : "마무리 중 — 새 건전지를 올리지 않고 벨트 위 건전지를 모두 분류한 뒤 종료합니다.";
            return;
        }

        // idle 또는 running 상태에서 서버가 남긴 안내/경고 메시지(예: 벨트 켜기 실패, 회차 종료 사유 등)가 있으면 표시
        if (!string.IsNullOrWhiteSpace(lineMessage)) {
            StatusLevel = Mode == "idle" && (lineMessage.Contains("실패") || lineMessage.Contains("못해") || lineMessage.Contains("이상"))
                ? "warn"
                : "normal";
            StatusGuide = lineMessage;
            return;
        }

        StatusLevel = "normal";
        StatusGuide = "";
    }

    private void ApplyLineEvent(LineEvent ev) {
        // 컨트롤러·장비에서 발생한 시작 거부, 비상정지 버튼 미복귀 해제 거부, 로봇팔2 분류 실패(ARM2_FAILED) 등을 즉시 하단에 표시
        if (ev.Level is "WARN" or "ERROR" &&
            (ev.Device is "controller" or "arm1" or "mega" || ev.Message.Contains("거부") || ev.Message.Contains("실패"))) {
            CommandResult = $"{DateTime.Now:HH:mm:ss}  [{Labels.Level(ev.Level)}] {ev.Message}";

            // 시작 거부나 해제 거부처럼 mode/message가 안 바뀌는 경우에도 상단 안내 박스에 즉시 반영
            if (ev.Message.Contains("start 거부") || ev.Message.Contains("해제 거부") || ev.Message.Contains("ARM2_FAILED")) {
                StatusLevel = ev.Level == "ERROR" ? "alarm" : "warn";
                StatusGuide = ev.Message;
            }
        }
    }

    // ── 버튼 명령 및 상태별 활성(CanExecute) 조건 (line_controller.py 규약 일치) ──

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => SendAsync(UiActions.Start, "시작");

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => SendAsync(UiActions.Pause, "일시정지");

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => SendAsync(UiActions.Resume, "재개");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() =>
        Confirm("지금 회차를 끝냅니다.\n새 건전지는 더 올리지 않고, 벨트 위에 있는 건전지까지만 모두 분류한 뒤 멈춥니다.")
            ? SendAsync(UiActions.Stop, "회차 종료")
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private Task SkipAsync() =>
        Confirm("집기에 실패한 이번 건전지를 건너뛰고 다음 칸으로 넘어갑니다.\n건너뛴 건전지는 결과 기록이 남지 않습니다. 계속할까요?")
            ? SendAsync(UiActions.Skip, "건너뛰기")
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private Task ResetAsync() =>
        Confirm("비상정지를 해제하고 로봇팔과 컨베이어를 원점으로 복귀시킵니다.\n\n" +
                "1. 물리 비상정지 버튼을 돌려 복귀(해제)시켰나요?\n" +
                "   (버튼이 눌려 있으면 서버에서 해제가 거부됩니다)\n" +
                "2. 벨트 위에 남아 있던 퍽을 회수했고 주변에 사람이 없나요?")
            ? SendAsync(UiActions.Reset, "비상정지 해제")
            : Task.CompletedTask;

    // 1) 시작: 대기(idle) 상태에서만 가능 (장비 이상 시 서버가 거부 이벤트 반환)
    private bool CanStart() => CanOperate && Mode == "idle";

    // 2) 일시정지: 가동 중(running) 또는 마무리 중(draining)에 가능
    private bool CanPause() => CanOperate && Mode is "running" or "draining";

    // 3) 재개: 일시정지(paused) 상태에서 가능 (일반 일시정지 및 wait_operator 재시도 모두 포함)
    private bool CanResume() => CanOperate && Mode == "paused";

    // 4) 회차 종료: 가동 중(running) 또는 일시정지(paused) 상태에서 가능 (이미 draining 중이면 불필요)
    private bool CanStop() => CanOperate && Mode is "running" or "paused";

    // 5) 건너뛰기: 로봇팔 1 집기 실패로 작업자 선택을 기다리는 상태(paused + wait_operator)에서만 활성
    private bool CanSkip() => CanOperate && Mode == "paused" && Step == "wait_operator" &&
                              (StatusGuide.Contains("pick") || StatusGuide.Contains("집기") || string.IsNullOrEmpty(StatusGuide));

    // 6) 비상정지 해제: 비상정지(estop) 상태이면서 이미 해제 진행 중(step == "reset")이 아닐 때만 가능
    private bool CanReset() => CanOperate && Mode == "estop" && Step != "reset";

    private static bool Confirm(string text) =>
        MessageBox.Show(text, "확인", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private async Task SendAsync(string action, string name) {
        try {
            await _client.SendCommandAsync(action);
            CommandResult = $"{DateTime.Now:HH:mm:ss}  {name} 명령을 보냈습니다.";
        } catch (Exception ex) {
            CommandResult = $"{DateTime.Now:HH:mm:ss}  {name} 실패: {ex.Message}";
        }
    }
}