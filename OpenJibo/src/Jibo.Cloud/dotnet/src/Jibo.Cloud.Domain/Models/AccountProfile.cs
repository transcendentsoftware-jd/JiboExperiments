namespace Jibo.Cloud.Domain.Models;

public sealed class AccountProfile
{
    public string AccountId { get; init; } = "usr_openjibo_owner";
    public string Email { get; init; } = "owner@openjibo.local";
    public string FirstName { get; init; } = "Jibo";
    public string LastName { get; init; } = "Owner";
    public string AccessKeyId { get; init; } = "openjibo-access-key";
    public string SecretAccessKey { get; init; } = "openjibo-secret-access-key";
}
