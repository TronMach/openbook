using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Capture;
using OpenBook.Application.Companies;
using OpenBook.Domain.Capture;
using OpenBook.Domain.Common;

namespace OpenBook.Api;

public sealed record RegisterDeviceRequest(string? Name);

public sealed record CaptureDeviceResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastSyncAt, bool IsActive)
{
    public static CaptureDeviceResponse From(CaptureDevice d) => new(d.Id, d.Name, d.CreatedAt, d.LastSyncAt, d.IsActive);
}

public sealed record SyncRequest(Guid DeviceId, IReadOnlyList<IncomingDraft> Drafts);

/// <summary>Offline capture: devices register, download the lists their forms need, and send drafts made offline.</summary>
public static class CaptureApi
{
    public static void MapCaptureApi(this RouteGroupBuilder api)
    {
        var capture = api.MapGroup("/capture");

        capture.MapGet("/devices", async (ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
            (await s.ListDevicesAsync(UserId(principal), ct)).Select(CaptureDeviceResponse.From));
        capture.MapPost("/devices", async (RegisterDeviceRequest r, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
        {
            var device = await s.RegisterDeviceAsync(UserId(principal), r.Name ?? "This device", AuthService.SessionIdOf(principal), ct);
            return Results.Created($"/api/capture/devices/{device.Id}", CaptureDeviceResponse.From(device));
        });
        capture.MapDelete("/devices/{id:guid}", async (Guid id, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
        {
            await s.RevokeDeviceAsync(id, UserId(principal), AuthService.SessionIdOf(principal), ct);
            return Results.NoContent();
        });

        // "device" is the device asking, so a removed one learns it here too, whether or not it has anything to send.
        capture.MapGet("/lists", async (Guid? device, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
        {
            try
            {
                if (device is { } id)
                    await s.CheckDeviceAsync(UserId(principal), id, AuthService.SessionIdOf(principal), ct);
                return Results.Ok((await s.ListsAsync(MayEnter(principal), ct)) with { Me = principal.Identity?.Name });
            }
            catch (DomainException ex) when (ex.Code is "capture.device_revoked" or "capture.device_other_user")
            {
                return DeviceRefused(ex);
            }
        });

        // A revoked device gets 410 Gone, its signal to wipe what it holds.
        capture.MapPost("/sync", async (SyncRequest r, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
        {
            try
            {
                await s.CheckDeviceAsync(UserId(principal), r.DeviceId, AuthService.SessionIdOf(principal), ct);
                return Results.Ok(await s.SyncAsync(UserId(principal), r.DeviceId, r.Drafts ?? [], MayEnter(principal), ct));
            }
            catch (DomainException ex) when (ex.Code is "capture.device_revoked" or "capture.device_other_user")
            {
                return DeviceRefused(ex);
            }
        });

        capture.MapGet("/drafts", async (ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
            (await s.NeedingAttentionAsync(UserId(principal), ct)).Select(x => new { x.Draft.Id, x.Draft.Kind, x.Draft.CapturedAt, x.Draft.Error, Device = x.Device.Name }));
        capture.MapPost("/drafts/{id:guid}/retry", async (Guid id, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
            await s.RetryAsync(id, UserId(principal), MayEnter(principal), ct));
        capture.MapPost("/drafts/{id:guid}/dismiss", async (Guid id, ClaimsPrincipal principal, CaptureService s, CancellationToken ct) =>
        {
            await s.DismissAsync(id, UserId(principal), ct);
            return Results.NoContent();
        });
    }

    /// <summary>What a device may enter is what its person (or limited key) may enter in each area, not just "capture".</summary>
    private static IResult DeviceRefused(DomainException ex) => Results.Json(new { code = ex.Code, message = ex.Message },
        statusCode: ex.Code == "capture.device_revoked" ? StatusCodes.Status410Gone : StatusCodes.Status409Conflict);

    private static Func<OpenBook.Domain.Capture.CaptureKind, bool> MayEnter(ClaimsPrincipal principal) =>
        kind => AuthClaims.MayWrite(principal, CaptureService.AreaOf(kind));


    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();
}
