using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Web.Security;

namespace OpenBook.Web.Endpoints;

/// <summary>Plain HTTP endpoints for things a static page can't do itself: sign-out and file transfer.</summary>
public static class AccountEndpoints
{
    public const long MaxBackupUploadBytes = 512L * 1024 * 1024;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery, AuthService auth) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
                return Results.BadRequest();
            if (http.User.GetSessionId() is { } sessionId)
                await auth.EndSessionAsync(http.User.GetUserId(), sessionId, audit: false, http.RequestAborted);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization();

        // Signs out one of the user's own sessions; signing out this one is the same as signing out.
        app.MapPost("/account/sessions/{id:guid}/end", async (Guid id, HttpContext http, IAntiforgery antiforgery, AuthService auth) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
                return Results.BadRequest();
            await auth.EndSessionAsync(http.User.GetUserId(), id, ct: http.RequestAborted);
            if (http.User.GetSessionId() == id)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login");
            }

            return Results.Redirect("/account/password?ended=true");
        }).RequireAuthorization();

        // Switches the signed-in person between the simple and full views, then goes back to the page they were on.
        // Form-bound, so the antiforgery token is validated automatically.
        app.MapPost("/account/view", async (IFormCollection form, ViewModeService views, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ViewMode>(form["view"], ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
                return Results.BadRequest();
            await views.SetAsync(mode, ct);
            return Results.Redirect(WebSecurity.SafeReturnUrl(form["returnUrl"]));
        }).RequireAuthorization();

        // The full view's menu as a bar of areas or as drop-downs: the signed-in person's own choice.
        app.MapPost("/account/menu", async (IFormCollection form, ViewModeService views, CancellationToken ct) =>
        {
            if (!Enum.TryParse<MenuStyle>(form["menu"], ignoreCase: true, out var menu) || !Enum.IsDefined(menu))
                return Results.BadRequest();
            await views.SetMenuAsync(menu, ct);
            return Results.Redirect(WebSecurity.SafeReturnUrl(form["returnUrl"]));
        }).RequireAuthorization();

        app.MapGet("/backup/download",async (IBackupService backups, CancellationToken ct) =>
        {
            BackupFile file;
            try
            {
                file = await backups.CreateAsync(ct);
            }
            catch (DomainException ex)
            {
                return BackToBackupPage(ex.Code);
            }

            var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Delete, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/vnd.sqlite3", file.FileName);
        }).RequireAuthorization();

        // Form-bound, so the antiforgery token from the page is validated automatically.
        app.MapPost("/backup/schedule", async (IFormCollection form, ScheduledBackupService schedules, CancellationToken ct) =>
        {
            try
            {
                var details = new BackupScheduleDetails(
                    form["enabled"] == "true",
                    Enum.TryParse<BackupFrequency>(form["frequency"], out var frequency) ? frequency : BackupFrequency.Daily,
                    Enum.TryParse<DayOfWeek>(form["weekday"], out var weekday) ? weekday : DayOfWeek.Sunday,
                    int.TryParse(form["hour"], System.Globalization.CultureInfo.InvariantCulture, out var hour) ? hour : 2,
                    Enum.TryParse<BackupDestination>(form["destination"], out var destination) ? destination : BackupDestination.Folder,
                    form["folderPath"], form["serviceUrl"], form["region"], form["bucket"], form["prefix"], form["accessKeyId"],
                    int.TryParse(form["keep"], System.Globalization.CultureInfo.InvariantCulture, out var keep) ? keep : BackupSchedule.DefaultKeep);
                var secret = form["secretKey"].ToString();
                await schedules.UpdateAsync(details, secret.Length == 0 ? null : secret, ct);
                return Results.Redirect("/backup?scheduled=saved");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/backup?scheduleError={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/backup/schedule/run", async (IFormCollection form, ScheduledBackupService schedules, CancellationToken ct) =>
        {
            try
            {
                var result = await schedules.RunNowAsync(ct);
                return Results.Redirect(result.Succeeded ? "/backup?scheduled=ran" : $"/backup?scheduleError={Uri.EscapeDataString(result.Error ?? "The backup failed.")}");
            }
            catch (DomainException ex)
            {
                return Results.Redirect($"/backup?scheduleError={Uri.EscapeDataString(ex.Message)}");
            }
        }).RequireAuthorization();

        app.MapPost("/backup/restore", async (
            IFormFile? file,
            [FromForm] string? password,
            [FromForm] bool? confirm,
            HttpContext http,
            AuthService auth,
            IBackupService backups,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return BackToBackupPage("backup.file_required");
            if (confirm != true)
                return BackToBackupPage("backup.confirm_required");
            if (!await auth.VerifyPasswordAsync(http.User.GetUserId(), password ?? string.Empty, ct))
                return BackToBackupPage("auth.wrong_password");

            var upload = Path.Combine(Path.GetTempPath(), $"openbook-restore-{Guid.NewGuid():N}.db");
            try
            {
                await using (var target = File.Create(upload))
                    await file.CopyToAsync(target, ct);
                await backups.RestoreAsync(upload, file.FileName, ct);
            }
            catch (DomainException ex)
            {
                return BackToBackupPage(ex.Code);
            }
            finally
            {
                File.Delete(upload);
            }

            // The restored file has its own users and security stamps.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login?restored=true");
        })
        .RequireAuthorization()
        .WithMetadata(new RequestSizeLimitAttribute(MaxBackupUploadBytes));

        return app;
    }

    private static IResult BackToBackupPage(string errorCode) => Results.Redirect($"/backup?error={Uri.EscapeDataString(errorCode)}");
}
