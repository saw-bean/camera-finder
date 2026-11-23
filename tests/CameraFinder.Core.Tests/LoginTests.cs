using System.Net;
using CameraFinder.Core.Discovery;
using CameraFinder.Core.Health;

static partial class Tests
{
    // ---------- Logins shared between ONVIF and video ----------

    public static void TestLoginRefusedByOnvifStillTriedForVideo()
    {
        var book = new LoginBook();
        var site = new Credentials("admin", "Site#2026");
        var old = new Credentials("admin", "12345");
        book.Refuse("cam", LoginService.Onvif, site);

        // ONVIF moves on to the next saved login; the video still tries the site login.
        Assert.Equal(old, book.Pick("cam", LoginService.Onvif, new[] { null, site, old }));
        Assert.Equal(site, book.Pick("cam", LoginService.Video, new[] { null, site, old }));
        // Another camera is not affected.
        Assert.Equal(site, book.Pick("other", LoginService.Onvif, new[] { site }));

        // Refused on both: nothing is tried automatically (no account lockouts) until the user types it in again.
        book.Refuse("cam", LoginService.Video, site);
        Assert.True(book.Pick("cam", LoginService.Video, new[] { site }) == null, "refused twice");
        book.Allow("cam", site);
        Assert.Equal(site, book.Pick("cam", LoginService.Onvif, new[] { site }));
        Assert.Equal(site, book.Pick("cam", LoginService.Video, new[] { site }));
        Assert.True(book.Pick("cam", LoginService.Video, new Credentials?[] { null, new("", "") }) == null, "empty logins are skipped");
    }

    public static void TestSeparateOnvifUsersDiagnosed()
    {
        var onvif = new OnvifReport { Answered = true, LoginAccepted = false, LoginError = "user name or password not accepted" };

        var same = Snap(onvif: onvif);
        same.VideoLoginAccepted = true;
        same.VideoLoginSameAsOnvif = true;
        var f = HealthCheck.Analyse(same).Findings.Single(x => x.Code == "onvif-login");
        Assert.Equal("The video accepts the login but ONVIF does not", f.Title);
        Assert.True(f.Advice[0].Contains("ONVIF user"), f.Advice[0]);
        var data = DiagnosisWriter.CollectedData(same);
        Assert.True(data.Contains("[Video login] accepted by the RTSP stream (same login as ONVIF)"), data);

        // Wrong password everywhere: the plain "refused" finding.
        var wrong = Snap(onvif: onvif);
        wrong.VideoLoginAccepted = false;
        wrong.VideoLoginSameAsOnvif = true;
        Assert.Equal("The camera refused the ONVIF login", HealthCheck.Analyse(wrong).Findings.Single(x => x.Code == "onvif-login").Title);
    }

    public static async Task TestDemoCameraWithSeparateOnvifUsers()
    {
        var demo = new DemoHealth(new DemoSite { ArpDelay = TimeSpan.Zero, JoinDelay = TimeSpan.Zero });
        var cam = IPAddress.Parse(DemoHealth.SeparateOnvifUsers);
        var site = new Credentials("admin", "admin");

        var refused = await demo.CollectAsync(cam, 80, site, null, CancellationToken.None);
        Assert.Equal(false, refused.LoginAccepted);
        var accepted = await demo.CollectAsync(cam, 80, DemoHealth.OnvifOnlyLogin, null, CancellationToken.None);
        Assert.Equal(true, accepted.LoginAccepted);

        // The video plays with the site login ONVIF refused.
        var stats = new LiveStats();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int seconds = 0;
        try { await demo.RunAsync(RtspPaths.Guess(cam.ToString(), "Avigilon", null)!, site, null, stats, _ => seconds++, cts.Token); }
        catch (OperationCanceledException) { }
        Assert.True(seconds > 0, "video played with the login ONVIF refused");
    }
}
