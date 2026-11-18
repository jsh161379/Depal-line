using System.ComponentModel;
using System.Windows;

namespace BareunOrder.Core.Pages;

/// <summary>
/// 위쪽 탭에 뜨는 페이지 1개 (관리자 프로그램 DepalAdmin과 같은 방식).
/// 각 담당자는 자기 프로젝트에 PageBase를 상속한 클래스만 만들면
/// 메인 창(Shell)이 알아서 탭에 붙이고 F키를 매긴다 (Order 순서대로 F1, F2, …).
/// </summary>
public interface IPage
{
    /// <summary>탭 이름 (짧게). Navigator.Navigate()에도 이 이름을 쓴다.</summary>
    string Title { get; }

    /// <summary>탭 순서 (작을수록 왼쪽). README의 탭 순서표를 따른다.</summary>
    int Order { get; }

    object ViewModel { get; }

    FrameworkElement View { get; }
}

/// <summary>ViewModel이 이걸 구현하면 탭 옆에 숫자 배지가 뜬다 (0이면 안 보임).</summary>
public interface IHasBadge : INotifyPropertyChanged
{
    int BadgeCount { get; }
}

public abstract class PageBase<TView> : IPage where TView : FrameworkElement, new()
{
    private TView? _view;

    protected PageBase(object viewModel) => ViewModel = viewModel;

    public abstract string Title { get; }
    public abstract int Order { get; }
    public object ViewModel { get; }

    public FrameworkElement View => _view ??= new TView { DataContext = ViewModel };
}

/// <summary>
/// 탭 이동. 다른 사람 화면으로 넘어가고 싶을 때 쓴다.
///   navigator.Navigate("주문");
/// </summary>
public sealed class Navigator
{
    /// <summary>지금 보고 있는 탭 이름</summary>
    public string? CurrentTitle { get; private set; }

    /// <summary>탭을 옮겨 달라는 요청 (메인 창이 듣는다)</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>보고 있는 탭이 바뀜 (예: 라인 메시지 탭을 열면 안 읽은 수를 0으로)</summary>
    public event Action<string?>? CurrentChanged;

    public void Navigate(string title) => NavigateRequested?.Invoke(title);

    /// <summary>메인 창만 부른다</summary>
    public void SetCurrent(string? title)
    {
        CurrentTitle = title;
        CurrentChanged?.Invoke(title);
    }
}
