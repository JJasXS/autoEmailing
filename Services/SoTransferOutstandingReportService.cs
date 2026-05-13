using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>
/// Loads Sales Order detail lines with outstanding quantity and related <c>ST_XTRANS</c> rows (DO/IV/DN).
/// </summary>
public sealed class SoTransferOutstandingReportService
{
    private readonly FirebirdUserReader _firebird;
    private readonly ILogger<SoTransferOutstandingReportService> _logger;

    public SoTransferOutstandingReportService(
        FirebirdUserReader firebird,
        ILogger<SoTransferOutstandingReportService> logger)
    {
        _firebird = firebird;
        _logger = logger;
    }

    /// <summary>Outstanding SO detail lines: <c>SQTY - SUM(ABS(ST_XTRANS.SQTY)) &lt;&gt; 0</c>.</summary>
    public async Task<IReadOnlyList<SoTransferOutstandingBlock>> LoadReportAsync(CancellationToken cancellationToken)
    {
        const string mainSql = """
            SELECT
              so.DOCKEY AS SO_DOCKEY,
              so.DOCNO,
              so.COMPANYNAME,
              dtl.DTLKEY AS SODTL_DTLKEY,
              ROW_NUMBER() OVER (PARTITION BY so.DOCKEY ORDER BY dtl.DTLKEY) AS LINE_SEQ,
              dtl.ITEMCODE,
              dtl.DESCRIPTION,
              COALESCE(dtl.UNITPRICE, 0) AS UNIT_PRICE,
              dtl.SQTY AS ORIG_QTY,
              dtl.DELIVERYDATE,
              COALESCE((
                SELECT SUM(ABS(x2.SQTY))
                FROM ST_XTRANS x2
                WHERE x2.FROMDOCTYPE = 'SO'
                  AND x2.FROMDOCKEY = so.DOCKEY
                  AND x2.FROMDTLKEY = dtl.DTLKEY
              ), 0) AS TOTAL_TRANSFER_QTY
            FROM SL_SO so
            INNER JOIN SL_SODTL dtl ON dtl.DOCKEY = so.DOCKEY
            WHERE (dtl.SQTY - COALESCE((
              SELECT SUM(ABS(x3.SQTY))
              FROM ST_XTRANS x3
              WHERE x3.FROMDOCTYPE = 'SO'
                AND x3.FROMDOCKEY = so.DOCKEY
                AND x3.FROMDTLKEY = dtl.DTLKEY
            ), 0)) <> 0
            ORDER BY so.DOCDATE, so.DOCNO, dtl.DTLKEY
            """;

        const string transSql = """
            SELECT
              x.FROMDOCKEY,
              x.FROMDTLKEY,
              x.TODOCTYPE,
              ABS(x.SQTY) AS LINE_QTY,
              CASE x.TODOCTYPE
                WHEN 'DO' THEN do.DOCNO
                WHEN 'IV' THEN iv.DOCNO
                WHEN 'DN' THEN dn.DOCNO
                ELSE COALESCE(x.TODOCTYPE, '?') || ':' || COALESCE(CAST(x.TODOCKEY AS VARCHAR(32)), '')
              END AS TRANS_DOCNO,
              CASE x.TODOCTYPE
                WHEN 'DO' THEN do.DOCDATE
                WHEN 'IV' THEN iv.DOCDATE
                WHEN 'DN' THEN dn.DOCDATE
                ELSE NULL
              END AS TRANS_DOCDATE
            FROM ST_XTRANS x
            LEFT JOIN SL_DO do ON x.TODOCTYPE = 'DO' AND x.TODOCKEY = do.DOCKEY
            LEFT JOIN SL_IV iv ON x.TODOCTYPE = 'IV' AND x.TODOCKEY = iv.DOCKEY
            LEFT JOIN SL_DN dn ON x.TODOCTYPE = 'DN' AND x.TODOCKEY = dn.DOCKEY
            WHERE x.FROMDOCTYPE = 'SO'
              AND EXISTS (
                SELECT 1
                FROM SL_SODTL d
                WHERE d.DOCKEY = x.FROMDOCKEY
                  AND d.DTLKEY = x.FROMDTLKEY
                  AND (d.SQTY - COALESCE((
                    SELECT SUM(ABS(x4.SQTY))
                    FROM ST_XTRANS x4
                    WHERE x4.FROMDOCTYPE = 'SO'
                      AND x4.FROMDOCKEY = d.DOCKEY
                      AND x4.FROMDTLKEY = d.DTLKEY
                  ), 0)) <> 0
              )
            """;

        var cs = await _firebird.ResolveConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new FbConnection(cs);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var mains = new List<(string dk, string dtk, SoTransferOutstandingBlock block)>();
        await using (var cmd = new FbCommand(mainSql, conn))
        await using (var rd = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var docKey = rd["SO_DOCKEY"]?.ToString() ?? "";
                var dtlKey = rd["SODTL_DTLKEY"]?.ToString() ?? "";
                var docNo = rd["DOCNO"]?.ToString()?.Trim() ?? "";
                var company = rd["COMPANYNAME"]?.ToString()?.Trim() ?? "";
                var lineSeq = ReadInt32(rd, "LINE_SEQ");
                if (lineSeq <= 0)
                    lineSeq = ReadInt32(rd, "SODTL_DTLKEY");
                var itemCode = rd["ITEMCODE"]?.ToString()?.Trim() ?? "";
                var desc = rd["DESCRIPTION"]?.ToString()?.Trim() ?? "";
                var unitPrice = ToDecimal(rd["UNIT_PRICE"]);
                var orig = ToDecimal(rd["ORIG_QTY"]);
                var totalT = ToDecimal(rd["TOTAL_TRANSFER_QTY"]);
                var os = orig - totalT;
                var deliv = ReadDate(rd, "DELIVERYDATE");

                mains.Add((docKey, dtlKey, new SoTransferOutstandingBlock
                {
                    SoDocKey = docKey,
                    SoDocNo = docNo,
                    CompanyName = company,
                    LineSeq = lineSeq,
                    ItemCode = itemCode,
                    Description = desc,
                    UnitPrice = unitPrice,
                    OrigQty = orig,
                    OutstandingQty = os,
                    DeliveryDate = deliv,
                    Transfers = Array.Empty<SoTransferDocumentLine>()
                }));
            }
        }

        var transferMap = new Dictionary<string, List<SoTransferDocumentLine>>(StringComparer.Ordinal);
        await using (var cmd = new FbCommand(transSql, conn))
        await using (var rd = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fk = rd["FROMDOCKEY"]?.ToString() ?? "";
                var dk = rd["FROMDTLKEY"]?.ToString() ?? "";
                var key = JoinKey(fk, dk);
                if (!transferMap.TryGetValue(key, out var list))
                {
                    list = new List<SoTransferDocumentLine>();
                    transferMap[key] = list;
                }

                var toType = rd["TODOCTYPE"]?.ToString()?.Trim() ?? "";
                list.Add(new SoTransferDocumentLine
                {
                    ToDocType = toType,
                    TransferDocNo = rd["TRANS_DOCNO"]?.ToString()?.Trim() ?? "",
                    TransferDocDate = ReadDate(rd, "TRANS_DOCDATE"),
                    TransferQty = ToDecimal(rd["LINE_QTY"])
                });
            }
        }

        foreach (var kv in transferMap.Values)
        {
            kv.Sort(static (a, b) =>
            {
                var ad = a.TransferDocDate?.Ticks ?? 0;
                var bd = b.TransferDocDate?.Ticks ?? 0;
                var c = ad.CompareTo(bd);
                if (c != 0)
                    return c;
                return string.Compare(a.TransferDocNo, b.TransferDocNo, StringComparison.OrdinalIgnoreCase);
            });
        }

        var blocks = new List<SoTransferOutstandingBlock>();
        foreach (var m in mains)
        {
            var key = JoinKey(m.dk, m.dtk);
            transferMap.TryGetValue(key, out var tlist);
            var transfers = (IReadOnlyList<SoTransferDocumentLine>)(tlist?.ToArray() ?? Array.Empty<SoTransferDocumentLine>());
            blocks.Add(new SoTransferOutstandingBlock
            {
                SoDocKey = m.block.SoDocKey,
                SoDocNo = m.block.SoDocNo,
                CompanyName = m.block.CompanyName,
                LineSeq = m.block.LineSeq,
                ItemCode = m.block.ItemCode,
                Description = m.block.Description,
                UnitPrice = m.block.UnitPrice,
                OrigQty = m.block.OrigQty,
                OutstandingQty = m.block.OutstandingQty,
                DeliveryDate = m.block.DeliveryDate,
                Transfers = transfers
            });
        }

        _logger.LogInformation("SO transfer outstanding: {Main} line(s), {Tx} transfer row(s).", blocks.Count,
            transferMap.Values.Sum(static l => l.Count));

        return blocks;
    }

    private static string JoinKey(string docKey, string dtlKey) => docKey.Trim() + "\u001f" + dtlKey.Trim();

    private static int ReadInt32(DbDataReader rd, string name)
    {
        var ord = FindOrdinal(rd, name);
        if (ord < 0 || rd.IsDBNull(ord))
            return 0;
        return Convert.ToInt32(rd.GetValue(ord), CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadDate(DbDataReader rd, string name)
    {
        var ord = FindOrdinal(rd, name);
        if (ord < 0 || rd.IsDBNull(ord))
            return null;
        var v = rd.GetValue(ord);
        return v switch
        {
            DateTime dt => dt,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            _ => DateTime.TryParse(v?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p)
                ? p
                : null
        };
    }

    private static int FindOrdinal(DbDataReader rd, string name)
    {
        for (var i = 0; i < rd.FieldCount; i++)
        {
            if (string.Equals(rd.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static decimal ToDecimal(object? v)
    {
        if (v is null or DBNull)
            return 0m;
        return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    }
}
