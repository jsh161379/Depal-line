using System.Windows;
using BareunOrder.Core.Comm;
using BareunOrder.Core.Data;
using BareunOrder.Core.Pages;
using BareunOrder.Core.Settings;
using BareunOrder.Feature.Customers;
using BareunOrder.Feature.Messages;
using BareunOrder.Feature.Orders;
using BareunOrder.Feature.Certificate;
using BareunOrder.Feature.Shipping;
using BareunOrder.Feature.Stats;
using BareunOrder.Feature.Stock;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Shell;

/// <summary>담당: (미정) — 시작 코드. 모든 탭을 여기서 한 번씩 등록한다.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        var settings = AppSettings.Load(AppContext.BaseDirectory);
        if (settings.LoadWarning != null)   // 설정 파일이 깨졌으면 켜자마자 알림 (포트폴리오 ㊳)
            MessageBox.Show(settings.LoadWarning + "\n\n고친 뒤 프로그램을 다시 켜세요.", "설정 파일 확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        var services = new ServiceCollection();

        // ── 공통 ──
        services.AddSingleton(settings);
        services.AddSingleton<Navigator>();
        services.AddSingleton<IOrderClient>(_ => settings.UseFakeOrders
            ? new FakeOrderClient()
            : new MqttOrderClient(settings));
        services.AddSingleton<OrderStore>();
        services.AddSingleton<IChatClient>(_ => settings.UseFakeOrders
            ? new FakeChatClient()
            : new MqttChatClient(settings));
        services.AddSingleton<ChatStore>();
        services.AddSingleton<LineSupplyReader>();   // 라인 A 등급 남은 수 (F2·F3 공통, 규약 v1.0.2 6장)

        // ── 탭 (순서는 각 페이지의 Order) ──
        services.AddOrdersFeature();      // F1 주문
        services.AddShippingFeature();    // F2 출고·배송
        services.AddStockFeature();       // F3 재고
        services.AddStatsFeature();       // F4 통계
        services.AddCustomersFeature();   // F5 고객·문의
        services.AddCertificateFeature(); // F6 검사 성적서
        services.AddMessagesFeature();    // F7 라인 메시지

        // ── 메인 창 ──
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var store = _services.GetRequiredService<OrderStore>();
        store.Write($"설정: {settings.LoadedFrom}{(settings.UseFakeOrders ? " (가짜 모드)" : "")}" +
                    (settings.LoadWarning != null ? " — 설정 파일 경고 있음" : ""), "시스템");
        await store.StartAsync();
        await _services.GetRequiredService<ChatStore>().StartAsync();
        await _services.GetRequiredService<LineSupplyReader>().StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services != null)
        {
            var store = _services.GetRequiredService<OrderStore>();
            var chat = _services.GetRequiredService<ChatStore>();
            _services.GetRequiredService<LineSupplyReader>().Stop();
            Task.Run(async () =>
            {
                await chat.StopAsync();
                await store.StopAsync();
            }).Wait(TimeSpan.FromSeconds(2));
            _services.Dispose();
        }
        base.OnExit(e);
    }
}
