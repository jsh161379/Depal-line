using System.Windows;

namespace DepalAdmin.Feature.Process;

/// <summary>카메라 1대 크게 보기 창. 열려 있는 동안 영상을 받는다.</summary>
public partial class CameraWindow : Window
{
    private readonly LiveCamera _camera;

    public CameraWindow(LiveCamera camera)
    {
        InitializeComponent();
        _camera = camera;
        DataContext = camera;
        Owner = Application.Current?.MainWindow;
        _camera.AddViewer();
        Closed += (_, _) => _camera.RemoveViewer();
    }
}
