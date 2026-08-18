using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Pages.Live;

namespace LarisVMS.Tests;

/// <summary>
/// Live/Index's redirect moved server-side in M14, reading the "lastViewId" user preference instead
/// of a client-side localStorage script — worth its own coverage since a subtly wrong fallback here
/// (an unparseable/stale saved id, or picking the wrong view when the saved one no longer exists)
/// would silently strand every visit to /Live on the wrong view.
/// </summary>
public class LiveIndexRedirectTests
{
    private sealed class FakeViewService(List<View> visible) : IViewService
    {
        public Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default) => Task.FromResult(visible);
        public Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default) => throw new NotImplementedException();
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

    private static IndexModel NewModel(List<View> visible, Dictionary<string, string>? saved = null)
    {
        var model = new IndexModel(new FakeViewService(visible), new FakePreferenceService(saved ?? []));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1")], "TestAuth"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private static View NewView(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public async Task NoViewsRendersThePageInsteadOfRedirecting()
    {
        var model = NewModel([]);

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
    }

    [Fact]
    public async Task NoSavedPreferenceFallsBackToTheFirstView()
    {
        var first = NewView("House");
        var model = NewModel([first, NewView("Garage")]);

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Views/Play", redirect.PageName);
        Assert.Equal(first.Id, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task ASavedPreferenceMatchingAVisibleViewWins()
    {
        var first = NewView("House");
        var saved = NewView("Garage");
        var model = NewModel([first, saved], new Dictionary<string, string> { ["lastViewId"] = saved.Id.ToString() });

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(saved.Id, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task ASavedPreferenceForAViewNoLongerVisibleFallsBackToTheFirstView()
    {
        // The view was deleted, or access was revoked, since the preference was last saved.
        var first = NewView("House");
        var model = NewModel([first], new Dictionary<string, string> { ["lastViewId"] = Guid.NewGuid().ToString() });

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(first.Id, redirect.RouteValues!["id"]);
    }

    [Fact]
    public async Task AnUnparseableSavedPreferenceFallsBackToTheFirstViewInsteadOfThrowing()
    {
        var first = NewView("House");
        var model = NewModel([first], new Dictionary<string, string> { ["lastViewId"] = "not-a-guid" });

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(first.Id, redirect.RouteValues!["id"]);
    }
}
