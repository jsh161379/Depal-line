using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace BareunOrder.Feature.Messages;

public partial class MessagesView : UserControl
{
    public MessagesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>새 메시지가 오면 맨 아래로 내린다</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MessagesViewModel vm)
        {
            vm.FocusInputRequested += () =>
            {
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            };
            vm.Lines.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && vm.Lines.Count > 0)
                    MessageList.ScrollIntoView(vm.Lines[^1]);
            };
        }
    }
}
