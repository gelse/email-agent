using System.Net;
using Bunit;
using EmailAgent.Spa.Notifications;
using FluentAssertions;
using Xunit;

using NotificationsPage = EmailAgent.Spa.Pages.User.Notifications;

namespace EmailAgent.UnitTests.Spa;

public class NotificationsPageTests
{
    private static readonly Guid N1 = new("88888888-8888-8888-8888-888888888881");
    private static readonly Guid N2 = new("88888888-8888-8888-8888-888888888882");

    private const string ListJson =
        """{"items":[{"id":"88888888-8888-8888-8888-888888888881","kind":"action_invalid_args","title":"Action rejected","body":"bad args","related_type":"processed_message","related_id":"99999999-9999-9999-9999-999999999999","created_at":"2026-01-01T00:00:00+00:00","read_at":null},{"id":"88888888-8888-8888-8888-888888888882","kind":"info","title":"Read one","body":null,"related_type":null,"related_id":null,"created_at":"2026-01-02T00:00:00+00:00","read_at":"2026-01-03T00:00:00+00:00"}],"next_cursor":null}""";
    private const string EmptyJson = """{"items":[],"next_cursor":null}""";

    [Fact]
    public void NotificationsPage_EmptyState()
    {
        using var ctx = Ctx(EmptyJson);
        var cut = ctx.RenderComponent<NotificationsPage>();
        cut.WaitForAssertion(() => cut.Find("#notifications-empty").Should().NotBeNull());
    }

    [Fact]
    public void NotificationsPage_RendersList_WithUnreadBadge_AndMarkRead()
    {
        using var ctx = Ctx(ListJson);
        var cut = ctx.RenderComponent<NotificationsPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Action rejected"));
        cut.Find("#notifications-unread-badge").TextContent.Should().Contain("1 unread");
        cut.Markup.Should().Contain("action_invalid_args");
        cut.Find($"#notification-read-{N1:N}").Should().NotBeNull();
        cut.FindAll($"#notification-read-{N2:N}").Should().BeEmpty("read rows have no mark-read button");
    }

    [Fact]
    public void NotificationsPage_MarkRead_UpdatesRow()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Handler.Responder = (req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith($"/{N1}/read"))
                return Task.FromResult(SpaTestContext.Json("""{"id":"88888888-8888-8888-8888-888888888881","kind":"action_invalid_args","title":"Action rejected","body":"bad args","related_type":"processed_message","related_id":"99999999-9999-9999-9999-999999999999","created_at":"2026-01-01T00:00:00+00:00","read_at":"2026-01-04T00:00:00+00:00"}"""));
            return Task.FromResult(SpaTestContext.Json(ListJson));
        };

        var cut = ctx.RenderComponent<NotificationsPage>();
        cut.WaitForAssertion(() => cut.Find($"#notification-read-{N1:N}").Should().NotBeNull());
        cut.Find($"#notification-read-{N1:N}").Click();
        cut.WaitForAssertion(() => cut.FindAll($"#notification-read-{N1:N}").Should().BeEmpty());
    }

    [Fact]
    public void NotificationsPage_MarkAllRead_Reloads()
    {
        var reloaded = false;
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Handler.Responder = (req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/read-all"))
            { reloaded = true; return Task.FromResult(SpaTestContext.Json("""{"updated":1}""")); }
            if (req.Method == HttpMethod.Get && reloaded)
                return Task.FromResult(SpaTestContext.Json(EmptyJson));
            return Task.FromResult(SpaTestContext.Json(ListJson));
        };

        var cut = ctx.RenderComponent<NotificationsPage>();
        cut.WaitForAssertion(() => cut.Find("#notifications-mark-all-read").Should().NotBeNull());
        cut.Find("#notifications-mark-all-read").Click();
        cut.WaitForAssertion(() => cut.Find("#notifications-empty").Should().NotBeNull());
    }

    private static SpaTestContext Ctx(string json)
    {
        var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(json));
        return ctx;
    }
}
