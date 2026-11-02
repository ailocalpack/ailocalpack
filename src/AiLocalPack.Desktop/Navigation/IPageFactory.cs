using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Navigation;

/// <summary>Creates page view models with their services injected and extra arguments (a game) passed in.</summary>
public interface IPageFactory
{
    T Create<T>(params object[] args) where T : PageViewModel;
}

internal sealed class PageFactory(IServiceProvider services) : IPageFactory
{
    public T Create<T>(params object[] args) where T : PageViewModel =>
        ActivatorUtilities.CreateInstance<T>(services, args);
}
