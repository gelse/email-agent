using System.Net;
using Bunit;
using EmailAgent.Spa.Notifications;
using FluentAssertions;
using Xunit;

using ActionsPage = EmailAgent.Spa.Pages.User.Actions;

namespace EmailAgent.UnitTests.Spa;

public class ActionsPageTests
{
    private const string Dek = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private static readonly Guid ActionId = new("77777777-7777-7777-7777-777777777777");

    private const string EmptyListJson = """{"items":[],"next_cursor":null}""";
    private const string RowJson =
        """{"items":[{"id":"77777777-7777-7777-7777-777777777777","type_id":"imap-reply","display_name":"My Reply","require_confirmation":true,"dry_run_default":false,"created_at":"2026-01-01T00:00:00+00:00","config":{"host":"smtp.x","port":587,"use_ssl":false,"username":"u","from_address":"a@b.com","from_name":"A"}}],"next_cursor":null}""";

    [Fact]
    public void ActionsPage_WithoutDek_ShowsPrompt_AndHidesTable()
    {
        using var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));

        var cut = ctx.RenderComponent<ActionsPage>();

        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull());
        cut.FindAll("#actions-table").Should().BeEmpty();
        ctx.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ActionsPage_WithDek_EmptyState()
    {
        using var ctx = Unlocked(EmptyListJson);
        var cut = ctx.RenderComponent<ActionsPage>();
        cut.WaitForAssertion(() => cut.Find("#actions-empty").Should().NotBeNull());
        cut.Markup.Should().Contain("No actions yet");
        cut.Find("#add-action-open").Should().NotBeNull();
    }

    [Fact]
    public void ActionsPage_WithRows_RendersTable()
    {
        using var ctx = Unlocked(RowJson);
        var cut = ctx.RenderComponent<ActionsPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("My Reply"));
        cut.Markup.Should().Contain("imap-reply");
        cut.Markup.Should().Contain("Yes"); // require_confirmation
        cut.Find($"#action-edit-{ActionId:N}").Should().NotBeNull();
        cut.Find($"#action-delete-{ActionId:N}").Should().NotBeNull();
    }

    [Fact]
    public void ActionsPage_DekInvalid_Relocks()
    {
        using var ctx = Unlocked();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """{"type":"/errors/dek-invalid","title":"Bad Request","status":400,"detail":"bad key"}""",
            HttpStatusCode.BadRequest));

        var cut = ctx.RenderComponent<ActionsPage>();
        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull());
        ctx.Store.DekStore.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ActionsPage_DekRequired_Relocks()
    {
        using var ctx = Unlocked();
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(
            """{"type":"/errors/dek-required","title":"Bad Request","status":400,"detail":"missing"}""",
            HttpStatusCode.BadRequest));

        var cut = ctx.RenderComponent<ActionsPage>();
        cut.WaitForAssertion(() => cut.Find("#unlock-submit").Should().NotBeNull());
    }

    [Fact]
    public void ActionsPage_AddButton_OpensDialog()
    {
        using var ctx = Unlocked();
        var cut = ctx.RenderComponent<ActionsPage>();
        cut.WaitForAssertion(() => cut.Find("#add-action-open").Should().NotBeNull());
        cut.Find("#add-action-open").Click();
        cut.Find("#action-display-name").Should().NotBeNull();
        cut.Find("#action-type").Should().NotBeNull();
        cut.Find("#action-host").Should().NotBeNull();
    }

    private static SpaTestContext Unlocked(string listJson = EmptyListJson)
    {
        var ctx = new SpaTestContext();
        ctx.Store.Set(SpaTestContext.FakeJwt(role: "User"));
        ctx.Store.DekStore.SetDek(Dek);
        ctx.Handler.Responder = (_, _) => Task.FromResult(SpaTestContext.Json(listJson));
        return ctx;
    }
}
