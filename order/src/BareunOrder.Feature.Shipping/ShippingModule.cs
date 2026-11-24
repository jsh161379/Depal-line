using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Shipping;

/// <summary>담당: 노원종 — F2 출고·배송</summary>
public static class ShippingModule
{
    public static IServiceCollection AddShippingFeature(this IServiceCollection services)
    {
        services.AddSingleton<IShippingDialogs, ShippingDialogs>();
        services.AddSingleton<ShippingViewModel>();
        services.AddSingleton<IPage, ShippingPage>();
        return services;
    }
}

public sealed class ShippingPage : PageBase<ShippingView>
{
    public ShippingPage(ShippingViewModel vm) : base(vm) { }
    public override string Title => "출고·배송";
    public override int Order => 20;
}
