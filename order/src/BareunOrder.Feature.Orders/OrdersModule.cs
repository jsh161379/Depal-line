using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Orders;

/// <summary>담당: (미정) — F1 주문</summary>
public static class OrdersModule
{
    public static IServiceCollection AddOrdersFeature(this IServiceCollection services)
    {
        services.AddSingleton<OrdersViewModel>();
        services.AddSingleton<IPage, OrdersPage>();
        return services;
    }
}

public sealed class OrdersPage : PageBase<OrdersView>
{
    public OrdersPage(OrdersViewModel vm) : base(vm) { }
    public override string Title => "주문";
    public override int Order => 10;
}
