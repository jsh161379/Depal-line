using System.Windows.Controls;
using System.Windows.Threading;

namespace DepalAdmin.Feature.Chat;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not ChatViewModel vm) return;

            vm.ScrollToEndRequested += (_, _) =>
                Dispatcher.InvokeAsync(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);

            // "부를 사람"을 고른 뒤 바로 이어서 쓸 수 있게 입력칸 끝으로
            vm.FocusDraftRequested += (_, _) =>
                Dispatcher.InvokeAsync(() =>
                {
                    DraftBox.Focus();
                    DraftBox.CaretIndex = DraftBox.Text.Length;
                }, DispatcherPriority.Background);
        };
    }
}
