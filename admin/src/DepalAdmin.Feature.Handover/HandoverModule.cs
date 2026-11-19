using DepalAdmin.Core.Handover;
using DepalAdmin.Core.Pages;
using DepalAdmin.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DepalAdmin.Feature.Handover;

/// <summary>
/// 담당: 유동호 — 인수인계.
/// IHandoverStore는 보고서(조시현)도 읽으므로 여기서 등록한다.
/// </summary>
public static class HandoverModule {
    public static IServiceCollection AddHandoverFeature(this IServiceCollection services) {
        services.AddSingleton<IHandoverStore>(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return settings.UseFakeDb
                ? new JsonHandoverStore(settings)
                : new MySqlHandoverStore(settings);
        });
        services.AddSingleton<HandoverViewModel>();
        services.AddSingleton<IPage, HandoverPage>();
        return services;
    }
}

public sealed class HandoverPage : PageBase<HandoverView> {
    public HandoverPage(HandoverViewModel vm) : base(vm) { }
    public override string Title => "인수인계";
    public override int Order => 70;
}