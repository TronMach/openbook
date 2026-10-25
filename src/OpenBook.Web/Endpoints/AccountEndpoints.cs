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
        app.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
                return Results.BadRequest();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization();

        app.MapGet("/backup/download", async (IBackupService backups, CancellationToken ct) =>
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
