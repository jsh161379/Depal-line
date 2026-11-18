using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Stock;

/// <summary>담당: (미정) — F3 재고</summary>
public static class StockModule
{
    public static IServiceCollection AddStockFeature(this IServiceCollection services)
    {
        services.AddSingleton<StockViewModel>();
        services.AddSingleton<IPage, StockPage>();
        return services;
    }
}

public sealed class StockPage : PageBase<StockView>
{
    public StockPage(StockViewModel vm) : base(vm) { }
    public override string Title => "재고";
    public override int Order => 30;
}
