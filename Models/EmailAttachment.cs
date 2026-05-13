namespace SqlAccountingEmailWorker.Models;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
