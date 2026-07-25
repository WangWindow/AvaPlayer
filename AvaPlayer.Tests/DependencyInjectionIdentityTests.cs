using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Settings;
using AvaPlayer.Application.Tests.Fakes;
using AvaPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

public sealed class DependencyInjectionIdentityTests
{
    private static ServiceProvider CreateScopedLyricsProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(new FakeSettingsService());
        services.AddSingleton<ILogger<LyricsViewModel>>(NullLogger<LyricsViewModel>.Instance);
        services.AddSingleton<ILogger<LyricsPreferenceService>>(NullLogger<LyricsPreferenceService>.Instance);
        services.AddScoped<ILyricPreferencesService, LyricsPreferenceService>();
        services.AddScoped<LyricsViewModel>();
        services.AddScoped<ILyricPresentationService>(sp => sp.GetRequiredService<LyricsViewModel>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Same_scope_resolves_identical_LyricsViewModel_and_ILyricPresentationService()
    {
        using var provider = CreateScopedLyricsProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var vm = sp.GetRequiredService<LyricsViewModel>();
        var asService = sp.GetRequiredService<ILyricPresentationService>();

        Assert.Same(vm, asService);
    }

    [Fact]
    public void Different_scopes_resolve_different_LyricsViewModel_instances()
    {
        using var provider = CreateScopedLyricsProvider();

        LyricsViewModel vm1;
        using (var scope1 = provider.CreateScope())
        {
            vm1 = scope1.ServiceProvider.GetRequiredService<LyricsViewModel>();
        }

        using var scope2 = provider.CreateScope();
        var vm2 = scope2.ServiceProvider.GetRequiredService<LyricsViewModel>();

        Assert.NotSame(vm1, vm2);
    }

    [Fact]
    public void Different_scopes_resolve_different_ILyricPresentationService_instances()
    {
        using var provider = CreateScopedLyricsProvider();

        ILyricPresentationService svc1;
        using (var scope1 = provider.CreateScope())
        {
            svc1 = scope1.ServiceProvider.GetRequiredService<ILyricPresentationService>();
        }

        using var scope2 = provider.CreateScope();
        var svc2 = scope2.ServiceProvider.GetRequiredService<ILyricPresentationService>();

        Assert.NotSame(svc1, svc2);
    }

    [Fact]
    public void Same_scope_identity_holds_after_multiple_resolutions()
    {
        using var provider = CreateScopedLyricsProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var vm1 = sp.GetRequiredService<LyricsViewModel>();
        var svc1 = sp.GetRequiredService<ILyricPresentationService>();
        var vm2 = sp.GetRequiredService<LyricsViewModel>();
        var svc2 = sp.GetRequiredService<ILyricPresentationService>();

        Assert.Same(vm1, vm2);
        Assert.Same(svc1, svc2);
        Assert.Same(vm1, svc1);
        Assert.Same(vm2, svc2);
    }
}
