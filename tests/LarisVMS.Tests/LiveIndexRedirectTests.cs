using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Pages.Live;
using PlayViewModelBuilder = LarisVMS.Web.Pages.Views.PlayViewModelBuilder;

namespace LarisVMS.Tests;

/// <summary>
/// Live/Index's view choice moved server-side in M14, reading the "lastViewId" user preference instead
/// of a client-side localStorage script — worth its own coverage since a subtly wrong fallback here
/// (an unparseable/stale saved id, or picking the wrong view when the saved one no longer exists)
/// would silently strand every visit to /Live on the wrong view.
/// </summary>
public class LiveIndexRedirectTests
{
    /// <summary>Records which view the page asked PlayViewModelBuilder for. Returning null there makes
    /// the builder stop before touching its other services (and the page fall back to /Views).</summary>
    private sealed class FakeViewService(List<View> visible) : IViewService
    {
        public Guid? Requested { get; private set; }
        public Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default) => Task.FromResult(visible);
        public Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default)
        {
            Requested = id;
            return Task.FromResult<View?>(null);
        }
        public Task<View> CreateAsync(string name, string ownerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAsync(Guid id, string userId, string name, bool isShared, string layoutJson, int sequenceIntervalSeconds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<View>> GetTourViewsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakePreferenceService(Dictionary<string, string> stored) : IUserPreferenceService
    {
        public Task<Dictionary<string, string>> GetAllAsync(string userId, CancellationToken ct = default) => Task.FromResult(stored);
        public Task SetAsync(string userId, string key, string value, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static (IndexModel Model, FakeViewService Views) NewModel(List<View> visible, Dictionary<string, string>? saved = null)
    {
        var views = new FakeViewService(visible);
        var builder = new PlayViewModelBuilder(views, null!, null!, null!, null!);
        var model = new IndexModel(views, new FakePreferenceService(saved ?? []), builder);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1")], "TestAuth"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return (model, views);
    }

    private static View NewView(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public async Task NoViewsRendersThePageInsteadOfRedirecting()
    {
        var (model, views) = NewModel([]);

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Null(views.Requested);
    }

    [Fact]
    public async Task NoSavedPreferenceFallsBackToTheFirstView()
    {
        var first = NewView("House");
        var (model, views) = NewModel([first, NewView("Garage")]);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(first.Id, views.Requested);
    }

    [Fact]
    public async Task ASavedPreferenceMatchingAVisibleViewWins()
    {
        var first = NewView("House");
        var saved = NewView("Garage");
        var (model, views) = NewModel([first, saved], new Dictionary<string, string> { ["lastViewId"] = saved.Id.ToString() });

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(saved.Id, views.Requested);
    }

    [Fact]
    public async Task ASavedPreferenceForAViewNoLongerVisibleFallsBackToTheFirstView()
    {
        // The view was deleted, or access was revoked, since the preference was last saved.
        var first = NewView("House");
        var (model, views) = NewModel([first], new Dictionary<string, string> { ["lastViewId"] = Guid.NewGuid().ToString() });

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(first.Id, views.Requested);
    }

    [Fact]
    public async Task AnUnparseableSavedPreferenceFallsBackToTheFirstViewInsteadOfThrowing()
    {
        var first = NewView("House");
        var (model, views) = NewModel([first], new Dictionary<string, string> { ["lastViewId"] = "not-a-guid" });

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(first.Id, views.Requested);
    }
}
