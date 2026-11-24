using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Shipping;

/// <summary>선택한 주문을 한 건씩 확인한다. 완료하면 다음 미확인 주문으로 이동하고 마지막에 닫는다.</summary>
public sealed class ShippingInstructionReview : DockPanel
{
    private readonly IReadOnlyList<ShippingRow> _rows;
    private readonly string _staff;
    private readonly bool _demo;
    private readonly Action _print;
    private readonly Action _close;
    private readonly DateTime _issuedAt;
    private readonly ContentControl _content = new();
    private readonly TextBlock _progress = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _previous;
    private readonly Button _next;

    public int CurrentIndex { get; private set; }
    public string ProgressText => _progress.Text;
    public string CurrentOrderNo => _rows[CurrentIndex].Order.No;

    public ShippingInstructionReview(IReadOnlyList<ShippingRow> rows, string staff, bool demo,
        Action print, Action close, DateTime? issuedAt = null)
    {
        if (rows.Count == 0) throw new ArgumentException("확인할 주문이 없습니다.", nameof(rows));
        _rows = rows.ToArray(); _staff = staff; _demo = demo; _print = print; _close = close;
        _issuedAt = issuedAt ?? DateTime.Now;
        Background = new SolidColorBrush(Color.FromRgb(238, 240, 243));
        TextElement.SetFontFamily(this, new FontFamily("Malgun Gothic"));
        var navigation = new DockPanel { Margin = new Thickness(16, 12, 16, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _previous = MakeButton("이전 주문", () => { if (CurrentIndex > 0) { CurrentIndex--; ShowCurrent(); } });
        _next = MakeButton("다음 미확인 주문", GoToNext);
        buttons.Children.Add(_previous); buttons.Children.Add(_next);
        buttons.Children.Add(MakeButton("목록으로 돌아가기", close));
        SetDock(buttons, Dock.Right);
        navigation.Children.Add(buttons); navigation.Children.Add(_progress);
        SetDock(navigation, Dock.Top);
        Children.Add(navigation); Children.Add(_content);
        CurrentIndex = FindUnconfirmed(-1);
        if (CurrentIndex < 0) CurrentIndex = 0;
        ShowCurrent();
    }

    private int FindUnconfirmed(int after)
    {
        for (var step = 1; step <= _rows.Count; step++)
        {
            var index = (after + step) % _rows.Count;
            if (_rows[index].Order.Status == OrderStatus.Inspect && !_rows[index].Checklist.IsComplete) return index;
        }
        return -1;
    }
    private void GoToNext()
    {
        var next = FindUnconfirmed(CurrentIndex);
        if (next < 0) return;
        CurrentIndex = next;
        ShowCurrent();
    }
    private void ShowCurrent()
    {
        UpdateProgress();
        _previous.IsEnabled = CurrentIndex > 0;
        _next.IsEnabled = FindUnconfirmed(CurrentIndex) is var next && next >= 0 && next != CurrentIndex;
        var document = ShippingDocuments.BuildInstructionPreview(new[] { _rows[CurrentIndex] }, _staff, _demo,
            _issuedAt, OnCompleted);
        _content.Content = new ShippingDocumentPreview(document, _print, _rows.Count);
    }
    private void OnCompleted()
    {
        UpdateProgress();
        if (_rows.All(r => r.Order.Status != OrderStatus.Inspect || r.Checklist.IsComplete)) { _close(); return; }
        GoToNext();
    }
    private void UpdateProgress()
    {
        var completed = _rows.Count(r => r.Order.Status != OrderStatus.Inspect || r.Checklist.IsComplete);
        _progress.Text = $"선택 {_rows.Count}건 · 확인 {completed}/{_rows.Count}건 · 현재 {CurrentIndex + 1}번째";
    }
    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text, FontSize = 13, MinHeight = 36,
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }
}
