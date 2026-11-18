using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Customers;

/// <summary>담당: 조시현 — F5 고객·문의</summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersFeature(this IServiceCollection services)
    {
        // 문의 받기·답변 보내기는 Core(OrderStore)가 한다. 이 탭은 화면, 문의 기록 파일, 고객 메모만.
        services.AddSingleton<InquiryArchive>();
        services.AddSingleton<CustomerMemoStore>();
        services.AddSingleton<CustomersViewModel>();
        services.AddSingleton<IPage, CustomersPage>();
        return services;
    }
}

public sealed class CustomersPage : PageBase<CustomersView>
{
    public CustomersPage(CustomersViewModel vm) : base(vm) { }
    public override string Title => "고객·문의";
    public override int Order => 50;
}
