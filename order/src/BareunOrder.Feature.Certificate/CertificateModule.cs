using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Certificate;

/// <summary>담당: (미정) — F6 검사 성적서</summary>
public static class CertificateModule
{
    public static IServiceCollection AddCertificateFeature(this IServiceCollection services)
    {
        services.AddSingleton<CertificateViewModel>();
        services.AddSingleton<IPage, CertificatePage>();
        return services;
    }
}

public sealed class CertificatePage : PageBase<CertificateView>
{
    public CertificatePage(CertificateViewModel vm) : base(vm) { }
    public override string Title => "검사 성적서";
    public override int Order => 60;
}
