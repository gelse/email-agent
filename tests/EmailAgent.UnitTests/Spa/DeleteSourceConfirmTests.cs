using System.Net;
using Bunit;
using EmailAgent.Spa.Pages.User;
using EmailAgent.Spa.Sources;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M2T6 bUnit coverage for the delete confirmation modal (task test list):
/// the confirm button DELETEs and raises <c>OnDeleted</c> so the parent
/// reloads and the row disappears, cancel never calls the API, an API failure
/// stays inline in the still-open modal, and 404 counts as deleted (the goal
/// state already holds).
/// </summary>
public class DeleteSourceConfirmTests
{
    private static readonly Guid SourceId = new("44444444-4444-4444-4444-444444444444");

    private const string DeletePath = "/v1/user/sources/44444444-4444-4444-4444-444444444444";

    [Fact]
    public void DeleteSourceConfirm_Confirm_CallsDelete_AndRaisesOnDeleted()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.NoContent())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-source-title").TextContent.Should().Be("Delete source");
        cut.Find("#delete-source-confirm").TextContent.Should().Be("Delete");

        cut.Find("#delete-source-confirm").Click();

        cut.WaitForAssertion(() => deleted.Should().BeTrue(
            "the parent reloads its list after the API accepts the delete"));
        var call = ctx.Handler.Requests.Should().ContainSingle(
            r => r.Method == "DELETE").Which;
        call.Path.Should().Be(DeletePath);
    }

    [Fact]
    public void DeleteSourceConfirm_Cancel_RaisesOnCancel_WithoutApiCall()
    {
        using var ctx = new SpaTestContext();

        var cancelled = false;
        var cut = RenderModal(ctx, onCancel: () => cancelled = true);

        cut.Find("#delete-source-cancel").Click();

        cancelled.Should().BeTrue("cancel closes the modal without deleting");
        ctx.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void DeleteSourceConfirm_ApiError_ShowsInlineAlert_AndKeepsModalOpen()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/source-has-messages","title":"Conflict","status":409,"detail":"Delete its processed messages first."}
                """,
                HttpStatusCode.Conflict))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-source-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#delete-source-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("Delete its processed messages first.");
        });
        deleted.Should().BeFalse("a rejected delete never tells the parent to drop the row");
        cut.FindAll("#delete-source-title").Should().NotBeEmpty(
            "the modal stays open so the failure is visible in context");
    }

    [Fact]
    public void DeleteSourceConfirm_NotFound404_TreatedAsDeleted()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/source-not-found","title":"Not Found","status":404,"detail":"The source does not exist."}
                """,
                HttpStatusCode.NotFound))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-source-confirm").Click();

        cut.WaitForAssertion(() => deleted.Should().BeTrue(
            "404 means the source is already gone — the goal state holds"));
        cut.FindAll("#delete-source-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Mounts the modal over the fixture source with both callbacks observed.</summary>
    private static IRenderedComponent<DeleteSourceConfirm> RenderModal(
        SpaTestContext ctx, Action? onDeleted = null, Action? onCancel = null)
    {
        onDeleted ??= static () => { };
        onCancel ??= static () => { };

        return ctx.RenderComponent<DeleteSourceConfirm>(parameters => parameters
            .Add(dialog => dialog.Source, new SourceDto
            {
                Id = SourceId,
                DisplayName = "work imap",
            })
            .Add(dialog => dialog.OnDeleted, EventCallback.Factory.Create(ctx, onDeleted))
            .Add(dialog => dialog.OnCancel, EventCallback.Factory.Create(ctx, onCancel)));
    }
}
