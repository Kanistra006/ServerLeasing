using FluentValidation;
using ServerLeasing.Models;

namespace ServerLeasing.Validators;

public class ServerValidator : AbstractValidator<Server>
{
    public ServerValidator()
    {
        RuleFor(x => x.Id).NotNull();
    }
}