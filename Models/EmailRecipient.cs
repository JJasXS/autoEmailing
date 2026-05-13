namespace SqlAccountingEmailWorker.Models;

public sealed class EmailRecipient
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
}
