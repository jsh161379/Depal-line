using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DepalAdmin.Feature.Handover;

public partial class HandoverView : UserControl {
    public HandoverView() {
        InitializeComponent();
    }

    /// <summary>
    /// 목록 행·버튼·스크롤바·입력칸·날짜선택기를 클릭한 경우를 제외하고
    /// 빈 영역을 클릭하면 선택을 해제해 즉시 인수인계 작성 모드로 돌아간다.
    /// </summary>
    private void OnRootPreviewMouseDown(object sender, MouseButtonEventArgs e) {
        if (DataContext is not HandoverViewModel { Selected: not null } vm)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        // 1) 목록 행 또는 열 머리글(정렬) 클릭 시 선택 유지
        if (FindParent<DataGridRow>(source) != null || FindParent<DataGridColumnHeader>(source) != null)
            return;

        // 2) 버튼([읽음], [새 메모 쓰기], [불러오기]), 스크롤바, 텍스트 상자(본문 복사·검색), 날짜 선택기 클릭 시 유지
        if (FindParent<ButtonBase>(source) != null
            || FindParent<ScrollBar>(source) != null
            || FindParent<TextBoxBase>(source) != null
            || FindParent<DatePicker>(source) != null)
            return;

        vm.Selected = null;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject {
        while (child != null) {
            if (child is T matched)
                return matched;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}