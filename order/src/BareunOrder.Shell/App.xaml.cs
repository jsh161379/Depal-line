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

        await _services.GetRequiredService<OrderStore>().StartAsync();
        await _services.GetRequiredService<ChatStore>().StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services != null)
        {
            var store = _services.GetRequiredService<OrderStore>();
            var chat = _services.GetRequiredService<ChatStore>();
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
