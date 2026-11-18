using BareunOrder.Core.Data;
using BareunOrder.Core.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace BareunOrder.Feature.Messages;

/// <summary>담당: 조시현 — F7 라인 메시지</summary>
public static class MessagesModule
{
    public static IServiceCollection AddMessagesFeature(this IServiceCollection services)
    {
        services.AddSingleton<MessagesViewModel>();
        services.AddSingleton<IPage, MessagesPage>();
        return services;
    }
}

public sealed class MessagesPage : PageBase<MessagesView>
{
    public MessagesPage(MessagesViewModel vm) : base(vm) { }
    public override string Title => ChatStore.TabTitle;   // "라인 메시지"
    public override int Order => 70;
}
