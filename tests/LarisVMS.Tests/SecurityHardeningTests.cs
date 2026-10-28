using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class SecurityHardeningTests
{
    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // ── Media token purposes ──────────────────────────────────────────────

    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void ALiveTokenDoesNotOpenTalkSnapshotOrZones()
    {
        var camera = Guid.NewGuid();
        var live = MediaToken.Issue(camera, Key, TimeSpan.FromSeconds(60));

        Assert.True(MediaToken.TryValidate(live, camera, Key, out _));
        Assert.False(MediaToken.TryValidateFor(live, MediaToken.Purposes.Talk, camera, Key, out _));
        Assert.False(MediaToken.TryValidateFor(live, MediaToken.Purposes.Snapshot, camera, Key, out _));
        Assert.False(MediaToken.TryValidateFor(live, MediaToken.Purposes.MotionZones, camera, Key, out _));
    }

    [Fact]
    public void APurposeTokenOpensOnlyItsOwnRoute()
    {
        var camera = Guid.NewGuid();
        var talk = MediaToken.IssueFor(MediaToken.Purposes.Talk, camera, Key, TimeSpan.FromSeconds(60));

        Assert.True(MediaToken.TryValidateFor(talk, MediaToken.Purposes.Talk, camera, Key, out _));
        Assert.False(MediaToken.TryValidateFor(talk, MediaToken.Purposes.Snapshot, camera, Key, out var error));
        Assert.Equal("wrong token purpose", error);
        Assert.False(MediaToken.TryValidate(talk, camera, Key, out _));
        Assert.False(MediaToken.TryValidateFor(talk, MediaToken.Purposes.Talk, Guid.NewGuid(), Key, out _));
        Assert.False(MediaToken.TryValidateFor(talk + "00", MediaToken.Purposes.Talk, camera, Key, out _));
    }

    // ── Camera access: one-time fail-closed upgrade ───────────────────────

    private static RoleSeedService Seeder(ApplicationDbContext db) =>
        new(db, null!, null!, NullLogger<RoleSeedService>.Instance);

    private static IdentityRole AddRole(ApplicationDbContext db, string name, string? tag = null)
    {
        var role = new IdentityRole(name) { Id = Guid.NewGuid().ToString() };
        db.Roles.Add(role);
        if (tag is not null) db.RoleProfiles.Add(new RoleProfile { RoleId = role.Id, Tag = tag });
        return role;
    }

    private static void AddUser(ApplicationDbContext db, string id, params IdentityRole[] roles)
    {
        db.Users.Add(new ApplicationUser { Id = id, UserName = id });
        foreach (var r in roles) db.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = r.Id });
    }

    [Fact]
    public async Task UpgradeKeepsEveryExistingUsersAccessExactlyAsItWas()
    {
        using var db = NewDb();
        var super = AddRole(db, "Super Admin", RoleTags.SuperAdmin);
        var open = AddRole(db, "Open role");           // no rows, members relied on the open default
        var limited = AddRole(db, "Limited role");     // has a one-camera row
        var mixed = AddRole(db, "Mixed role");         // no rows, but one member is limited elsewhere
        var camera = Guid.NewGuid();
        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.Role, PrincipalId = limited.Id,
            ScopeType = CameraAccessScopeType.Camera, ScopeId = camera, Actions = CameraAccessActions.View
        });
        AddUser(db, "admin", super);
        AddUser(db, "a", open);
        AddUser(db, "b", limited);
        AddUser(db, "c", mixed, limited);
        AddUser(db, "d", mixed);
        AddUser(db, "e");
        await db.SaveChangesAsync();

        await Seeder(db).FailClosedUpgradeAsync(default);

        var rows = await db.CameraAccesses.ToListAsync();
        bool AllRow(CameraAccessPrincipalType type, string id) =>
            rows.Any(r => r.PrincipalType == type && r.PrincipalId == id && r.ScopeType == CameraAccessScopeType.All);

        Assert.True(AllRow(CameraAccessPrincipalType.Role, open.Id));
        Assert.False(AllRow(CameraAccessPrincipalType.Role, mixed.Id)); // would widen "c"
        Assert.False(AllRow(CameraAccessPrincipalType.Role, limited.Id));
        Assert.False(AllRow(CameraAccessPrincipalType.Role, super.Id));
        Assert.True(AllRow(CameraAccessPrincipalType.User, "d"));      // "d" relied on the open default
        Assert.True(AllRow(CameraAccessPrincipalType.User, "e"));      // no roles at all
        Assert.False(AllRow(CameraAccessPrincipalType.User, "c"));
        Assert.False(AllRow(CameraAccessPrincipalType.User, "a"));

        // Runs once.
        var count = rows.Count;
        await Seeder(db).FailClosedUpgradeAsync(default);
        Assert.Equal(count, await db.CameraAccesses.CountAsync());
    }

    [Fact]
    public async Task AFreshInstallGivesGuestViewerViewOnly()
    {
        using var db = NewDb();
        var guest = AddRole(db, "Guest/Viewer", RoleTags.GuestViewer);
        AddRole(db, "Operator", RoleTags.Operator);
        await db.SaveChangesAsync();

        await Seeder(db).FailClosedUpgradeAsync(default);

        var row = Assert.Single(await db.CameraAccesses.ToListAsync());
        Assert.Equal(guest.Id, row.PrincipalId);
        Assert.Equal(CameraAccessActions.View, row.Actions);
        Assert.Equal(CameraAccessScopeType.All, row.ScopeType);
    }

    // ── Export access ─────────────────────────────────────────────────────

    private sealed class FixedCameraAccess(HashSet<Guid>? exportable) : ICameraAccessService
    {
        public Task<HashSet<Guid>?> GetAccessibleCameraIdsAsync(ClaimsPrincipal user, CameraAccessActions action, CancellationToken ct = default)
            => Task.FromResult(exportable is null ? null : new HashSet<Guid>(exportable));
    }

    private sealed class FixedPermissions(bool admin) : IPermissionService
    {
        public Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default) => Task.FromResult(admin);
        public Task<bool> HasPermissionForRoleNameAsync(string roleName, string resource, string action, CancellationToken ct = default) => Task.FromResult(admin);
        public Task<PermissionSet> GetGrantedAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(new PermissionSet(admin, new HashSet<(string, string)>()));
    }

    private static ClaimsPrincipal User(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test"));

    private static ExportJob Job(string owner, params Guid[] cameras) => new()
    {
        Id = Guid.NewGuid(), RequestedByUserId = owner,
        Items = cameras.Select(c => new ExportJobItem { Id = Guid.NewGuid(), CameraId = c }).ToList()
    };

    [Fact]
    public async Task ExportsAreSeenOnlyByTheirOwnerOrSomeoneWhoCanExportEveryCamera()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var access = await ExportAccess.ForAsync(User("me"), new FixedCameraAccess([mine]), new FixedPermissions(false));

        Assert.True(access.CanSee(Job("me", other)));          // own job, even with a camera since revoked
        Assert.True(access.CanSee(Job("someone", mine)));
        Assert.False(access.CanSee(Job("someone", mine, other)));
        Assert.True(access.CanManage(Job("me", mine)));
        Assert.False(access.CanManage(Job("someone", mine)));
        Assert.False(access.CanExportCamera(other));

        var admin = await ExportAccess.ForAsync(User("root"), new FixedCameraAccess(null), new FixedPermissions(true));
        Assert.True(admin.CanManage(Job("someone", other)));
    }

    // ── Local data ACLs ───────────────────────────────────────────────────

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AProtectedFileAndAStagingFolderAreClosedToOrdinaryUsers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "larisvms-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            LocalDataAcl.RecreateProtectedDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "planted.txt"), "x");
            LocalDataAcl.RecreateProtectedDirectory(dir);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir)); // nothing planted survives

            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{}");
            Assert.Null(LocalDataAcl.ProtectFile(file));

            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
            foreach (var security in new System.Security.AccessControl.FileSystemSecurity[]
                     { new FileInfo(file).GetAccessControl(), new DirectoryInfo(dir).GetAccessControl() })
            {
                Assert.True(security.AreAccessRulesProtected);
                var sids = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference).ToList();
                Assert.DoesNotContain(users, sids);
                Assert.DoesNotContain(everyone, sids);
                Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!, sids);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ── Signed updates ────────────────────────────────────────────────────

    private static (string Pem, Func<string, string> Sign) NewSigningKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfoPem(), payload => Convert.ToBase64String(ecdsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Fact]
    public void AnUpdateIsTrustedOnlyWhenSignedByThePinnedKey()
    {
        var server = NewSigningKey();
        var attacker = NewSigningKey();
        var payload = UpdateSignature.UpdatePayload("win-x64", "1.2.3", "ABCDEF", null);

        // First signed offer on a machine with no key yet: trusted, and its key is to be pinned.
        Assert.True(UpdateSignature.Check(null, server.Pem, server.Sign(payload), payload, out var pin, out _));
        Assert.Equal(server.Pem, pin);

        // Pinned: the same key passes, without asking to pin again.
        Assert.True(UpdateSignature.Check(server.Pem, server.Pem, server.Sign(payload), payload, out pin, out _));
        Assert.Null(pin);

        // Unsigned, another key, a tampered hash: refused.
        Assert.False(UpdateSignature.Check(server.Pem, null, null, payload, out _, out _));
        Assert.False(UpdateSignature.Check(server.Pem, attacker.Pem, attacker.Sign(payload), payload, out _, out _));
        var tampered = UpdateSignature.UpdatePayload("win-x64", "1.2.3", "000000", null);
        Assert.False(UpdateSignature.Check(server.Pem, server.Pem, server.Sign(payload), tampered, out _, out _));
    }

    // ── Outbound guard (Web Push) ─────────────────────────────────────────

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://wns2-by3p.notify.windows.com/w/?token=x", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("https://10.0.0.1/abc", false)]
    [InlineData("https://127.0.0.1:4200/abc", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/abc", false)]
    [InlineData("http://fcm.googleapis.com/abc", false)]
    public void OnlyBrowserPushServicesAreAcceptedAsEndpoints(string url, bool expected) =>
        Assert.Equal(expected, LarisVMS.Infrastructure.Security.OutboundGuard.IsKnownPushService(new Uri(url)));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("142.250.72.10", true)]
    [InlineData("2607:f8b0:4005:80b::200a", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void OnlyPublicAddressesAreReachable(string address, bool expected) =>
        Assert.Equal(expected, LarisVMS.Infrastructure.Security.OutboundGuard.IsPublic(System.Net.IPAddress.Parse(address)));

    // ── Admin scope ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdminCannotClimbThroughTheAdminTools()
    {
        using var db = NewDb();
        var super = AddRole(db, "Super Admin", RoleTags.SuperAdmin);
        var siteAdmin = AddRole(db, "System Admin", RoleTags.SystemAdmin);
        var org = AddRole(db, "Org-wide");
        var operatorRole = AddRole(db, "Operator", RoleTags.Operator);
        await db.SaveChangesAsync();
        foreach (var (role, tier) in new[] { (siteAdmin, RoleScopeTier.Site), (org, RoleScopeTier.Org), (operatorRole, RoleScopeTier.Group) })
        {
            var profile = await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == role.Id);
            if (profile is null) db.RoleProfiles.Add(new RoleProfile { RoleId = role.Id, MaxScopeTier = tier });
            else profile.MaxScopeTier = tier;
        }
        AddUser(db, "site-admin", siteAdmin);
        await db.SaveChangesAsync();

        var scope = await AdminScope.ForAsync(db, new FixedPermissions(false), User("site-admin"));

        Assert.False(scope.IsSuperAdmin);
        Assert.False(scope.CanBindRole(super.Id));        // no Super Admin keys or AD links
        Assert.False(scope.CanBindRole(org.Id));          // wider than a Site-tier admin may assign
        Assert.True(scope.CanBindRole(operatorRole.Id));
        Assert.False(scope.CanEditRole(siteAdmin.Id));    // their own role
        Assert.True(scope.CanEditRole(operatorRole.Id));
        Assert.False(scope.CanSetTier(RoleScopeTier.Org));
        Assert.True(scope.CanSetTier(RoleScopeTier.Site));
        Assert.False(scope.CanGrant("Settings", "Edit")); // not theirs to give
    }

    // ── Origin check ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://vms.example.com", "vms.example.com", null, true)]
    [InlineData("https://vms.example.com:8444", "vms.example.com", null, true)]   // live view on its own port
    [InlineData("https://evil.example.net", "vms.example.com", null, false)]
    [InlineData("https://vms.example.com.evil.net", "vms.example.com", null, false)]
    [InlineData("https://portal.example.com", "10.0.0.5", "https://portal.example.com", true)]
    [InlineData("null", "vms.example.com", null, false)]
    public void OnlyThisSitesOriginMayUseTheCookie(string origin, string host, string? publicOrigin, bool allowed) =>
        Assert.Equal(allowed, LarisVMS.Web.Middleware.OriginCheckMiddleware.IsAllowed(origin, host, publicOrigin));

    [Theory]
    [InlineData("POST", "/api/exports", false, true)]
    [InlineData("DELETE", "/api/exports/1", false, true)]
    [InlineData("GET", "/api/exports", false, false)]
    [InlineData("GET", "/live/abc", true, true)]
    [InlineData("POST", "/api/nodes/heartbeat", false, false)]
    [InlineData("POST", "/Admin/Settings/Security", false, false)] // forms carry an antiforgery token
    public void StateChangingApiCallsAndWebSocketsAreChecked(string method, string path, bool webSocket, bool expected)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(expected, LarisVMS.Web.Middleware.OriginCheckMiddleware.NeedsCheck(context.Request, webSocket));
    }

    // ── Backups and SQL ───────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\nas\backups\larisvms", true)]
    [InlineData(@"D:\Backups\LarisVMS\daily", true)]
    [InlineData(@"\\attacker\share", false)]
    [InlineData(@"D:\BackupsElsewhere", false)]
    public void BackupsStayInsideTheAllowedRoots(string directory, bool allowed) =>
        Assert.Equal(allowed, LarisVMS.Infrastructure.Services.BackupService.IsUnderAny(directory, [@"\\nas\backups", @"D:\Backups\LarisVMS"]));

    [Theory]
    [InlineData(".", true)]
    [InlineData(@".\SQLEXPRESS", true)]
    [InlineData("localhost,1433", true)]
    [InlineData("tcp:127.0.0.1", true)]
    [InlineData("sql01.corp.example", false)]
    public void OnlyALocalSqlServerSkipsTheCertificateCheckByDefault(string server, bool local) =>
        Assert.Equal(local, LarisVMS.Infrastructure.Data.SqlServerOps.IsThisMachineServer(server));

    // ── Server certificate check ──────────────────────────────────────────

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned(string name, int days = 3650)
    {
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
    }

    [Fact]
    public void TrustOnFirstUsePinsTheFirstSelfSignedCertificateAndRefusesAnother()
    {
        var saves = 0;
        var trust = new ServerCertificateTrust(ServerCertificateCheck.TrustOnFirstUse, null, () => saves++);
        var server = SelfSigned("server");
        var impostor = SelfSigned("server");
        const System.Net.Security.SslPolicyErrors untrusted = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(trust.Validate(server, untrusted));            // first use: pinned
        Assert.Equal(1, saves);
        Assert.True(trust.Validate(server, untrusted));            // same one, even with errors (expiry)
        Assert.False(trust.Validate(impostor, untrusted));         // a different self-signed one
        Assert.True(trust.Validate(impostor, System.Net.Security.SslPolicyErrors.None)); // a CA-trusted one always works
        Assert.False(trust.Validate(server, untrusted));           // ...and replaces the pin

        trust.ResetPin();
        Assert.True(trust.Validate(server, untrusted));
    }

    [Fact]
    public void TheOtherModesDoWhatTheySay()
    {
        var cert = SelfSigned("server");
        var strict = new ServerCertificateTrust(ServerCertificateCheck.TrustedOnly, null);
        Assert.False(strict.Validate(cert, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(strict.Validate(cert, System.Net.Security.SslPolicyErrors.None));

        var off = new ServerCertificateTrust(ServerCertificateCheck.Off, "something-else");
        Assert.True(off.Validate(cert, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));

        Assert.Equal(ServerCertificateCheck.TrustOnFirstUse, ServerCertificateTrust.Parse(null));
        Assert.Equal(ServerCertificateCheck.TrustedOnly, ServerCertificateTrust.Parse("TrustedOnly"));
    }

    // ── CSP image sources ─────────────────────────────────────────────────

    [Theory]
    [InlineData("https://tile.openstreetmap.org/{z}/{x}/{y}.png", "https://tile.openstreetmap.org")]
    [InlineData("https://{s}.tile.example.org/{z}/{x}/{y}.png", "https://*.tile.example.org")]
    [InlineData("http://tiles.lan:8080/{z}/{x}/{y}.png", "http://tiles.lan:8080")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("https://a b/{z}", "")]
    public void TheTileServerIsTheOnlyOutsideImageSource(string tileUrl, string expected) =>
        Assert.Equal(expected, LarisVMS.Web.Middleware.ClientEndpointCspCache.TileSource(tileUrl));

    // The strict script policy runs no inline handlers, so none may come back: each one would silently
    // stop working once the policy is enforced. data-confirm / data-action (inline-actions.js) instead.
    [Fact]
    public void PagesHaveNoInlineEventHandlers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var web = Path.Combine(dir.FullName, "src", "LarisVMS.Web");
        var handler = new System.Text.RegularExpressions.Regex(@"\son[a-z]+\s*=\s*[""']",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        var found = new[] { "*.razor", "*.cshtml", "*.js" }
            .SelectMany(p => Directory.EnumerateFiles(web, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && handler.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(web, x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(found);
    }

    // Pages' own scripts go through <PageScript>, which version-stamps the address like the layout's,
    // so a browser never keeps running an old copy after an update.
    [Fact]
    public void PageScriptsAreVersionStamped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var web = Path.Combine(dir.FullName, "src", "LarisVMS.Web");
        var plain = new System.Text.RegularExpressions.Regex(@"<script\s[^>]*src\s*=\s*[""']/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        var found = Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => plain.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(web, x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void NodePromptsAreTextNotScript()
    {
        var message = LarisVMS.Web.Helpers.NodeConfirm.Delete("x'); alert(1); ('");
        Assert.DoesNotContain("confirm(", message);
        Assert.Contains("x'); alert(1); ('", message);
        Assert.Contains("\n\n", LarisVMS.Web.Helpers.NodeConfirm.Restart("n", 2));
    }

    [Fact]
    public void ViolationReportsKeepOnlyTheLatestFifty()
    {
        var reports = new LarisVMS.Web.Security.CspReports();
        for (var i = 0; i < 60; i++)
            reports.Add(new(DateTime.UtcNow, "script-src", $"inline {i}", ""));
        Assert.Equal(60, reports.Total);
        var recent = reports.Recent();
        Assert.Equal(50, recent.Count);
        Assert.Equal("inline 59", recent[0].Blocked);
    }
}
