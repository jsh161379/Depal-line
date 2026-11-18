using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Stats;

/// <summary>담당: (미정) — F4 통계</summary>
public static class StatsModule
{
    public static IServiceCollection AddStatsFeature(this IServiceCollection services)
    {
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<IPage, StatsPage>();
        return services;
    }
}

public sealed class StatsPage : PageBase<StatsView>
{
    public StatsPage(StatsViewModel vm) : base(vm) { }
    public override string Title => "통계";
    public override int Order => 40;
}
