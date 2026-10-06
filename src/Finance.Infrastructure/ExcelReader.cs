using ClosedXML.Excel;
using ClosedXML.Graphics;
using Finance.Core;
using System.Globalization;

namespace Finance.Infrastructure;

public static class ExcelReader
{
    private static readonly IXLGraphicEngine Engine = CreateEngine();
    public static LoadOptions Options => new() { GraphicEngine = Engine };
    private static IXLGraphicEngine CreateEngine()
    {
        // Use the font shipped by the pinned ClosedXML release, without enumerating OS fonts.
        using var font = typeof(DefaultGraphicEngine).Assembly.GetManifestResourceStream("ClosedXML.Graphics.Fonts.CarlitoBare-Regular.ttf")
            ?? throw new InvalidOperationException("Font incorporato ClosedXML assente.");
        return DefaultGraphicEngine.CreateOnlyWithFonts(font);
    }
    public static IReadOnlyList<BankMovement> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream, Options);
            foreach (var sheet in workbook.Worksheets)
            {
                var header = sheet.RowsUsed().FirstOrDefault(r => r.CellsUsed().Any(c => c.GetString().Trim() == "Data_Valuta"));
                if (header is null) continue;
                var columns = header.CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber);
                string[] required = ["Data_Valuta", "Entrate", "Uscite", "Descrizione_Completa", "Stato"];
                if (required.Any(k => !columns.ContainsKey(k))) throw new FinanceException("EXCEL_FORMAT", "Intestazioni Excel incomplete.");
                var result = new List<BankMovement>();
                foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() > header.RowNumber()))
                {
                    if (!row.Cell(columns["Stato"]).GetString().Trim().Equals("Contabilizzato", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var dateCell = row.Cell(columns["Data_Valuta"]);
                        var date = dateCell.TryGetValue<DateTime>(out var d) ? DateOnly.FromDateTime(d) : Formats.Date(dateCell.GetString().Trim());
                        var income = Amount(row.Cell(columns["Entrate"]));
                        var outgoing = Amount(row.Cell(columns["Uscite"]));
                        var description = row.Cell(columns["Descrizione_Completa"]).GetString();
                        if ((income is null) == (outgoing is null) || income < 0 || outgoing > 0 || string.IsNullOrWhiteSpace(description))
                            throw new FinanceException("EXCEL_FORMAT", "Movimento privo di descrizione o con importi incoerenti.");
                        result.Add(new(row.RowNumber(), date, income, outgoing is null ? null : checked(-outgoing.Value), description));
                    }
                    catch (Exception e) when (e is not IOException) { throw new FinanceException("EXCEL_FORMAT", $"Riga {row.RowNumber()}: {e.Message}"); }
                }
                // The bank export is newest first in booking order, NOT value-date order.
                result.Reverse();
                return result;
            }
            throw new FinanceException("EXCEL_FORMAT", "Tabella movimenti non trovata.");
        }
        catch (FinanceException) { throw; }
        catch (Exception e) { throw new FinanceException("EXCEL_READ", $"Impossibile leggere l'Excel: {e.Message}"); }
    }

    private static long? Amount(IXLCell cell)
    {
        if (cell.IsEmpty() || string.IsNullOrWhiteSpace(cell.GetString())) return null;
        decimal value;
        if (cell.DataType == XLDataType.Number) value = Convert.ToDecimal(cell.GetDouble(), CultureInfo.InvariantCulture);
        else if (!decimal.TryParse(cell.GetString(), NumberStyles.Number, Formats.Italian, out value))
            throw new FinanceException("EXCEL_FORMAT", "Importo non riconosciuto.");
        if (value * 100 != decimal.Truncate(value * 100)) throw new FinanceException("EXCEL_FORMAT", "Importo con più di due decimali.");
        return checked((long)(value * 100));
    }
}
