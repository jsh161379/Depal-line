using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DepalAdmin.Feature.Process;

/// <summary>
/// 담당: 김초아 — 카메라 1대의 실시간 영상 (MJPEG, http://주소).
/// 카메라는 판정하는 프로그램(YOLO·라즈베리파이)이 열고, 이 화면은 그 프로그램이 내보내는 주소로 영상만 받는다.
/// 보는 화면이 하나라도 있을 때만 받는다 (AddViewer / RemoveViewer).
/// </summary>
public partial class LiveCamera : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const double StaleSeconds = 3;     // 이만큼 새 화면이 없으면 "멈춤"
    private const int RetryMs = 2000;          // 끊기면 다시 붙는 간격
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    private readonly Dispatcher _ui;
    private CancellationTokenSource? _cts;
    private int _viewers;
    private int _framesThisSecond;
    private DateTime _lastFrameAt = DateTime.MinValue;

    public string Key { get; }
    public string Title { get; }

    [ObservableProperty] private string _url;
    [ObservableProperty] private ImageSource? _frame;
    [ObservableProperty] private string _statusText = "꺼짐";
    [ObservableProperty] private string _statusKey = "neutral";
    [ObservableProperty] private string _fpsText = "";

    public LiveCamera(string key, string title, string url)
    {
        Key = key;
        Title = title;
        _url = url;
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool IsRunning => _cts != null;

    public void AddViewer()
    {
        if (++_viewers == 1) Start();
    }

    public void RemoveViewer()
    {
        if (_viewers == 0) return;
        if (--_viewers == 0) Stop();
    }

    /// <summary>1초마다 (ViewModel 타이머): 초당 장수 표시, 새 화면이 끊기면 "멈춤"</summary>
    public void Tick(DateTime now)
    {
        if (!IsRunning) return;
        FpsText = _framesThisSecond > 0 ? $"{_framesThisSecond}fps" : "";
        _framesThisSecond = 0;
        if (StatusKey == "ok" && (now - _lastFrameAt).TotalSeconds > StaleSeconds)
            SetStatus("멈춤", "warn");
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetStatus("꺼짐", "neutral");
        FpsText = "";
    }

    private void SetStatus(string text, string key)
    {
        StatusText = text;
        StatusKey = key;
    }

    /// <summary>화면 스레드에서 실행. 그사이 꺼졌으면(Stop) 무시</summary>
    private void Post(Action a) => _ui.InvokeAsync(() => { if (IsRunning) a(); });

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Post(() => SetStatus("연결 중", "warn"));
                using var resp = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await ReadFramesAsync(stream, ct);
                Post(() => SetStatus("끊김", "offline"));
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Post(() => SetStatus("끊김", "offline"));
            }

            try { await Task.Delay(RetryMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>MJPEG: JPEG 시작(FF D8) ~ 끝(FF D9)을 잘라 한 장씩 화면에 올림</summary>
    private async Task ReadFramesAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        var len = 0;
        var chunk = new byte[64 * 1024];

        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n <= 0) return;   // 서버가 닫음

            if (len + n > buf.Length)
            {
                if (len + n > MaxFrameBytes) { len = 0; continue; }   // 이상한 데이터 → 버림
                Array.Resize(ref buf, Math.Max(buf.Length * 2, len + n));
            }
            Buffer.BlockCopy(chunk, 0, buf, len, n);
            len += n;

            while (true)
            {
                var start = IndexOf(buf, len, 0xFF, 0xD8, 0);
                if (start < 0) { len = 0; break; }
                var end = IndexOf(buf, len, 0xFF, 0xD9, start + 2);
                if (end < 0)
                {
                    if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, len - start); len -= start; }
                    break;
                }

                var size = end + 2 - start;
                var jpeg = new byte[size];
                Buffer.BlockCopy(buf, start, jpeg, 0, size);
                var rest = len - (end + 2);
                Buffer.BlockCopy(buf, end + 2, buf, 0, rest);
                len = rest;

                ShowJpeg(jpeg);
            }
        }
    }

    private void ShowJpeg(byte[] jpeg)
    {
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(jpeg);
            bmp.EndInit();
            bmp.Freeze();
        }
        catch { return; }   // 깨진 한 장은 건너뜀

        Post(() =>
        {
            Frame = bmp;
            _lastFrameAt = DateTime.Now;
            _framesThisSecond++;
            if (StatusKey != "ok") SetStatus("정상", "ok");
        });
    }

    private static int IndexOf(byte[] b, int len, byte x, byte y, int from)
    {
        for (var i = Math.Max(from, 0); i < len - 1; i++)
            if (b[i] == x && b[i + 1] == y) return i;
        return -1;
    }
}

/// <summary>
/// 카메라 주소 설정: 실행 폴더의 cameras.json (없으면 기본값으로 만듦).
/// 영상을 내보내는 프로그램(YOLO PC·라즈베리파이) 주소가 정해지면 이 파일만 고치면 된다.
/// </summary>
public static class CameraConfig
{
    public const string FileName = "cameras.json";

    public static readonly (string Key, string Title)[] Order =
    {
        ("top", "상단 (위치·극 방향)"),
        ("left", "측면 L (외관)"),
        ("right", "측면 R (외관)"),
        ("pallet", "팔레트"),
    };

    private static Dictionary<string, string> Defaults() => new()
    {
        ["top"] = "http://127.0.0.1:8080/top",
        ["left"] = "http://127.0.0.1:8080/left",
        ["right"] = "http://127.0.0.1:8080/right",
        ["pallet"] = "http://127.0.0.1:8080/pallet",
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Dictionary<string, string> Load()
    {
        var urls = Defaults();
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (saved != null)
                    foreach (var (k, v) in saved)
                        if (!string.IsNullOrWhiteSpace(v)) urls[k] = v.Trim();
            }
            else
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(urls, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* 설정 파일 문제면 기본값 */ }
        return urls;
    }
}
