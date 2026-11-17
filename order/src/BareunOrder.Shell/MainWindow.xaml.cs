using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BareunOrder.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => OnStateChanged();
    }

    // ── 직접 만든 제목 줄 버튼 ──
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 기본 제목 줄을 없애면 최대화 때 창이 화면 밖으로 몇 픽셀 넘친다 → 그만큼 안쪽 여백을 준다.
    /// 최대화 버튼 모양도 □ ↔ 겹친 □ 로 바꾼다.
    /// </summary>
    private void OnStateChanged()
    {
        var max = WindowState == WindowState.Maximized;
        if (max)
        {
            // 넘치는 두께 = 크기 조절 테두리 + 덧댄 테두리(WPF엔 속성이 없어 윈도우에 직접 물어봄, 픽셀 → WPF 단위)
            var b = SystemParameters.WindowResizeBorderThickness;
            var pad = GetSystemMetrics(SM_CXPADDEDBORDER) / VisualTreeHelper.GetDpi(this).DpiScaleX;
            Root.Margin = new Thickness(b.Left + pad, b.Top + pad, b.Right + pad, b.Bottom + pad);
        }
        else Root.Margin = new Thickness(0);
        MaxButton.Content = max ? "\uE923" : "\uE922";
        MaxButton.ToolTip = max ? "이전 크기로" : "최대화";
    }

    private const int SM_CXPADDEDBORDER = 92;
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>F1~F6 → 탭 이동 (어느 화면에 있든)</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key >= Key.F1 && e.Key <= Key.F12)
        {
            _vm.SelectByIndex(e.Key - Key.F1);
            e.Handled = true;
        }
    }
}
