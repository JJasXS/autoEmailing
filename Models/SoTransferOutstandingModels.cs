namespace SqlAccountingEmailWorker.Models;

/// <summary>One Sales Order detail line (outstanding) with transfer sub-rows, aligned to ERP layout.</summary>
public sealed class SoTransferOutstandingBlock
{
    public required string SoDocKey { get; init; }
    public required string SoDocNo { get; init; }
    public required string CompanyName { get; init; }

    /// <summary>Line sequence within the SO (1, 2, 3…).</summary>
    public int LineSeq { get; init; }

    public required string ItemCode { get; init; }
    public required string Description { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal OrigQty { get; init; }
    public decimal OutstandingQty { get; init; }
    public DateTime? DeliveryDate { get; init; }

    /// <summary>Transfer lines for this SO detail, ordered by document date then doc no.</summary>
    public required IReadOnlyList<SoTransferDocumentLine> Transfers { get; init; }
}

public sealed class SoTransferDocumentLine
{
    public required string ToDocType { get; init; }
    public required string TransferDocNo { get; init; }
    public DateTime? TransferDocDate { get; init; }
    public decimal TransferQty { get; init; }
}
