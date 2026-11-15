using Bifrost.Core.Api;
using Bifrost.Core.Models;
using Bifrost.Server.People;
using Bifrost.Server.Persistence;

namespace Bifrost.Server.Api;

/// <summary>Managing people (design §5). Admin only.</summary>
internal sealed class PeopleEndpoints(PeopleService people)
{
    public void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/people").AddEndpointFilter(RequireAdmin.Filter);
        admin.MapGet("/", List);
        admin.MapPost("/", Create);
        admin.MapPatch("/{id:guid}", Update);
        admin.MapDelete("/{id:guid}", Delete);
    }

    internal static PersonDto ToDto(PersonEntity p) => new(p.Id, p.DisplayName, p.Role, p.CreatedAt);

    private async Task<IResult> List() => Results.Ok((await people.ListPeopleAsync()).Select(ToDto));

    private Task<IResult> Create(HttpContext context, CreatePersonRequest request) => Guarded(async () =>
    {
        var person = await people.CreatePersonAsync(context.Principal(), request.DisplayName ?? "", request.Role ?? PersonRole.Member);
        return Results.Created($"/api/v1/people/{person.Id}", ToDto(person));
    });

    private Task<IResult> Update(HttpContext context, Guid id, UpdatePersonRequest request) => Guarded(async () =>
        Results.Ok(ToDto(await people.UpdatePersonAsync(context.Principal(), id, request.DisplayName, request.Role))));

    private Task<IResult> Delete(HttpContext context, Guid id, string? confirm) => Guarded(async () =>
    {
        if (await people.GetPersonAsync(id) is null) throw new PeopleException("person_not_found");
        if (confirm != "true")
        {
            return Problem.Create(400, "confirmation_required", "Deleting a person removes their devices. Repeat with ?confirm=true.");
        }
        await people.DeletePersonAsync(context.Principal(), id);
        return Results.NoContent();
    });

    internal static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PeopleException e)
        {
            return e.Code switch
            {
                "invalid_name" => Problem.Create(400, e.Code, "The name must be 1–40 characters without slashes or control characters."),
                "name_taken" => Problem.Create(409, e.Code, "Another person already has that name."),
                "last_admin" => Problem.Create(409, e.Code, "There must always be at least one admin."),
                "local_admin" => Problem.Create(409, e.Code, "The admin who owns this computer's device can't be demoted or deleted."),
                "local_device" => Problem.Create(409, e.Code, "This computer's device can't be revoked."),
                "person_not_found" => Problem.Create(404, e.Code, "No such person."),
                _ => Problem.Create(400, e.Code, "The request was refused."),
            };
        }
    }
}
