using System.Windows;
using System.Windows.Controls;

namespace DepalAdmin.Feature.Process;

public partial class ProcessView : UserControl
{
    public ProcessView()
    {
        InitializeComponent();
        // 라인 현황이 보일 때만 카메라 영상을 받음 (다른 탭이면 끔 → 가벼움)
        IsVisibleChanged += (_, e) => (DataContext as ProcessViewModel)?.SetViewing(e.NewValue is true);
    }
}
