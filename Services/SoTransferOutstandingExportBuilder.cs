using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>Excel + PDF for SO outstanding transfer. PDF: with transfers, first body row shows total Tfer qty then one row per transfer.</summary>
public sealed class SoTransferOutstandingExportBuilder
{
    /// <summary>Banner title on exports (print / PDF).</summary>
    private const string ReportListingTitle = "Outstanding sales order listing";
    /// <summary>PDF table columns (group header row shows SO + company).</summary>
    private const int ColCount = 11;
    private const int ExcelFlatColCount = 12;
    /// <summary>PDF sub-rows: blank Seq.–Description; repeat Ext. No, Orig; then this transfer’s Tfer + doc + line O/S + delivery.</summary>
    private const int LeftCols = 3;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>PDF columns: no unit price; <c>Ext. No</c> = <c>SL_SO.DOCNOEX</c>; orig / transfer / doc then O/S + delivery after transfer doc no.</summary>
    private static readonly string[] Headers =
    [
        "SO Doc Date",
        "Seq.",
        "Code",
        "Description",
        "Ext. No",
        "Orig Qty",
        "Tfer Qty",
        "Date",
        "Doc No",
        "O/Stding",
        "Delivy date"
    ];

    /// <summary>Excel: transfer doc date last, beside transfer doc no; <c>Ext. No</c> = <c>DOCNOEX</c>.</summary>
    private static readonly string[] ExcelFlatHeaders =
    [
        "SO No",
        "SO Doc Date",
        "Company Name",
        "Item Code",
        "Description",
        "Ext. No",
        "Orig. Qty",
        "Transfer Qty",
        "O/S Qty",
        "Delivery Date",
        "Transfer Doc Date",
        "Transfer Doc No"
    ];

    public byte[] BuildExcel(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("SO Transfer");

        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        var r = 1;
        for (var c = 0; c < ExcelFlatHeaders.Length; c++)
            ws.Cell(r, c + 1).Value = ExcelFlatHeaders[c];
        var headerRow = ws.Range(r, 1, r, ExcelFlatColCount);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        headerRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRow.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        r++;

        foreach (var b in blocks)
        {
            var transfers = b.Transfers;
            if (transfers.Count == 0)
            {
                WriteExcelFlatRow(ws, r, b, null);
                r++;
            }
            else
            {
                foreach (var t in transfers)
                {
                    WriteExcelFlatRow(ws, r, b, t);
                    r++;
                }
            }
        }

        ApplyExcelFlatColumnWidths(ws);
        ws.Column(5).Style.Alignment.WrapText = true;
        ws.Column(1).Style.NumberFormat.Format = "@";
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Excel flat row: transfer date in col 11, transfer doc no in col 12.</summary>
    private static void WriteExcelFlatRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, SoTransferDocumentLine? t)
    {
        var c = 1;
        ws.Cell(r, c++).Value = b.SoDocNo;

        var soDateCell = ws.Cell(r, c++);
        if (b.SoDocDate is { } sd)
            soDateCell.Value = sd.Date;
        else
            soDateCell.Clear();

        ws.Cell(r, c++).Value = b.CompanyName;
        ws.Cell(r, c++).Value = b.ItemCode;
        ws.Cell(r, c++).Value = b.Description;
        ws.Cell(r, c++).Value = b.SoDocNoEx;
        ws.Cell(r, c++).Value = b.OrigQty;
        ws.Cell(r, c++).Value = t is null ? (decimal?)null : t.TransferQty;
        ws.Cell(r, c++).Value = b.OutstandingQty;

        var deliveryCell = ws.Cell(r, c++);
        if (b.DeliveryDate is { } del)
            deliveryCell.Value = del.Date;
        else
            deliveryCell.Clear();

        var transferDateCell = ws.Cell(r, c++);
        if (t?.TransferDocDate is { } transferDate)
            transferDateCell.Value = transferDate.Date;
        else
            transferDateCell.Clear();

        ws.Cell(r, c).Value = t?.TransferDocNo ?? "";

        StyleExcelFlatNumericCols(ws, r);
        StyleExcelFlatDateCols(ws, r);
        ws.Range(r, 1, r, ExcelFlatColCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void StyleExcelFlatNumericCols(IXLWorksheet ws, int r)
    {
        ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.00";
    }

    private static void StyleExcelFlatDateCols(IXLWorksheet ws, int r)
    {
        const string dFmt = "dd/MM/yyyy";
        ws.Cell(r, 2).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 10).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 11).Style.DateFormat.Format = dFmt;
    }

    /// <summary>Minimum widths so price/date/qty columns do not visually merge (flat import layout).</summary>
    private static void ApplyExcelFlatColumnWidths(IXLWorksheet ws)
    {
        // ClosedXML column width is roughly “characters” for Calibri 11.
        double[] mins = [12, 11, 28, 14, 40, 16, 11, 11, 11, 12, 11, 16];
        for (var i = 0; i < mins.Length; i++)
        {
            var col = ws.Column(i + 1);
            col.Width = Math.Max(col.Width, mins[i]);
        }
    }

    private static string FormatShortDate(DateTime? d) =>
        d is { } dt ? dt.ToString("dd/MM/yy", DisplayCulture) : "";

    public byte[] BuildPdf(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        var asAt = DateTime.Today.ToString("dd/MM/yyyy", DisplayCulture);
        var runAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", DisplayCulture);
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(20);
                page.Size(PageSizes.A4.Landscape());
                page.DefaultTextStyle(x => x.FontSize(7));

                page.Header().Column(col =>
                {
                    col.Item().Text(ReportListingTitle).SemiBold().FontSize(11).AlignLeft();
                    col.Item().Text($"As at {asAt}").FontSize(9).AlignLeft();
                });

                page.Footer().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().AlignLeft().Text($"Run (generated): {runAt}")
                        .FontSize(8).FontColor(Colors.Grey.Darken2);
                    row.ConstantItem(100).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken2));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                    });
                });

                page.Content().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(44);
                        c.ConstantColumn(28);
                        c.ConstantColumn(64);
                        c.RelativeColumn(2.4f);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(64);
                        c.ConstantColumn(52);
                    });

                    table.Header(h =>
                    {
                        foreach (var name in Headers)
                            h.Cell().Element(HCell).Text(name);
                    });

                    string? lastSoKey = null;
                    foreach (var b in blocks)
                    {
                        if (!string.Equals(b.SoDocKey, lastSoKey, StringComparison.Ordinal))
                        {
                            table.Cell().ColumnSpan(ColCount).Element(x => x
                                .Background(Colors.Grey.Lighten3)
                                .PaddingVertical(4)
                                .BorderBottom(0.5f)
                                .BorderColor(Colors.Grey.Lighten1)
                                .AlignLeft()
                                .DefaultTextStyle(s => s.SemiBold().FontSize(9))
                                .Text($"{b.SoDocNo}    {b.CompanyName}"));
                            lastSoKey = b.SoDocKey;
                        }

                        var transfers = b.Transfers;
                        if (transfers.Count == 0)
                            WritePdfLineWithoutTransfers(table, b);
                        else
                        {
                            WritePdfTransferTotalRow(table, b, SumTransferQty(transfers));
                            foreach (var t in transfers)
                                WritePdfTransferDetailRow(table, b, t);
                        }
                    }
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static IContainer HCell(IContainer x) =>
        x.DefaultTextStyle(s => s.SemiBold())
            .AlignLeft()
            .PaddingVertical(3)
            .BorderBottom(1)
            .BorderColor(Colors.Blue.Darken4);

    private static decimal SumTransferQty(IReadOnlyList<SoTransferDocumentLine> transfers)
    {
        decimal s = 0;
        foreach (var t in transfers)
            s += t.TransferQty;
        return s;
    }

    /// <summary>SO line with no transfer rows — empty transfer columns.</summary>
    private static void WritePdfLineWithoutTransfers(TableDescriptor table, SoTransferOutstandingBlock b)
    {
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.SoDocDate));
        table.Cell().Element(BodyCell).Text(b.LineSeq.ToString(DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        table.Cell().Element(BodyCell).Text(b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text(b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.DeliveryDate));
    }

    /// <summary>First row under the line when transfers exist: same line fields, Tfer = sum of all transfer qty, no transfer doc date/no.</summary>
    private static void WritePdfTransferTotalRow(TableDescriptor table, SoTransferOutstandingBlock b, decimal totalTransferQty)
    {
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.SoDocDate));
        table.Cell().Element(BodyCell).Text(b.LineSeq.ToString(DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        table.Cell().Element(BodyCell).Text(b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(totalTransferQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text(b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.DeliveryDate));
    }

    private static void WritePdfTransferDetailRow(TableDescriptor table, SoTransferOutstandingBlock b, SoTransferDocumentLine t)
    {
        table.Cell().Element(SubCell).Text(FormatShortDate(b.SoDocDate));
        for (var i = 0; i < LeftCols; i++)
            table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(b.SoDocNoEx);
        table.Cell().Element(SubCell).Text(b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(SubCell).Text(t.TransferQty.ToString("N2", DisplayCulture));
        table.Cell().Element(SubCell).Text(FormatShortDate(t.TransferDocDate));
        table.Cell().Element(SubCell).Text(t.TransferDocNo);
        table.Cell().Element(SubCell).Text(b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(SubCell).Text(FormatShortDate(b.DeliveryDate));
    }

    private static IContainer BodyCell(IContainer x) =>
        x.AlignLeft().PaddingVertical(2).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    private static IContainer SubCell(IContainer x) =>
        x.AlignLeft().Background(Colors.Grey.Lighten4).PaddingVertical(2).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }
}
