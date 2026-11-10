using System.Net;
using Bunit;
using EmailAgent.Spa.Models;
using EmailAgent.Spa.Pages.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace EmailAgent.UnitTests.Spa;

/// <summary>
/// M3T5 bUnit coverage for the delete confirmation modal (task test list):
/// the confirm button DELETEs and raises <c>OnDeleted</c> so the parent reloads
/// and the row disappears, cancel never calls the API, an API failure stays
/// inline in the still-open modal, and 404 counts as deleted (the goal state
/// already holds).
/// </summary>
public class DeleteModelConfirmTests
{
    private static readonly Guid ModelId = new("44444444-4444-4444-4444-444444444444");

    private const string DeletePath = "/v1/admin/models/44444444-4444-4444-4444-444444444444";

    [Fact]
    public void DeleteModelConfirm_Confirm_CallsDelete_AndRaisesOnDeleted()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.NoContent())
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-model-title").TextContent.Should().Be("Delete model");
        cut.Find("#delete-model-confirm").TextContent.Should().Be("Delete");
        cut.Markup.Should().Contain("mini chat");

        cut.Find("#delete-model-confirm").Click();

        cut.WaitForAssertion(() => deleted.Should().BeTrue(
            "the parent reloads its list after the API accepts the delete"));
        var call = ctx.Handler.Requests.Should().ContainSingle(
            r => r.Method == "DELETE").Which;
        call.Path.Should().Be(DeletePath);
    }

    [Fact]
    public void DeleteModelConfirm_Cancel_RaisesOnCancel_WithoutApiCall()
    {
        using var ctx = new SpaTestContext();

        var cancelled = false;
        var cut = RenderModal(ctx, onCancel: () => cancelled = true);

        cut.Find("#delete-model-cancel").Click();

        cancelled.Should().BeTrue("cancel closes the modal without deleting");
        ctx.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void DeleteModelConfirm_ApiError_ShowsInlineAlert_AndKeepsModalOpen()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/forbidden","title":"Forbidden","status":403,"detail":"This account is disabled."}
                """,
                HttpStatusCode.Forbidden))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-model-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#delete-model-error");
            error.GetAttribute("role").Should().Be("alert");
            error.TextContent.Should().Be("This account is disabled.");
        });
        deleted.Should().BeFalse("a rejected delete never tells the parent to drop the row");
        cut.FindAll("#delete-model-title").Should().NotBeEmpty(
            "the modal stays open so the failure is visible in context");
    }

    [Fact]
    public void DeleteModelConfirm_NotFound404_TreatedAsDeleted()
    {
        using var ctx = new SpaTestContext();
        ctx.Handler.Responder = (request, _) => request.Method == HttpMethod.Delete
            ? Task.FromResult(SpaTestContext.Json(
                """
                {"type":"/errors/model-not-found","title":"Model not found","status":404,"detail":"No model with id 44444444-4444-4444-4444-444444444444."}
                """,
                HttpStatusCode.NotFound))
            : Task.FromResult(SpaTestContext.Json("{}"));

        var deleted = false;
        var cut = RenderModal(ctx, onDeleted: () => deleted = true);

        cut.Find("#delete-model-confirm").Click();

        cut.WaitForAssertion(() => deleted.Should().BeTrue(
            "404 means the model is already gone — the goal state holds"));
        cut.FindAll("#delete-model-error").Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Mounts the modal over the fixture model with both callbacks observed.</summary>
    private static IRenderedComponent<DeleteModelConfirm> RenderModal(
        SpaTestContext ctx, Action? onDeleted = null, Action? onCancel = null)
    {
        onDeleted ??= static () => { };
        onCancel ??= static () => { };

        return ctx.RenderComponent<DeleteModelConfirm>(parameters => parameters
            .Add(dialog => dialog.Model, new ModelDto
            {
                Id = ModelId,
                DisplayName = "mini chat",
                Provider = "openai-compatible",
                ModelName = "gpt-4o-mini",
            })
            .Add(dialog => dialog.OnDeleted, EventCallback.Factory.Create(ctx, onDeleted))
            .Add(dialog => dialog.OnCancel, EventCallback.Factory.Create(ctx, onCancel)));
    }
}
