using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DanishTranscriber;

public static class TranscriptDocument
{
    public static string NewPath(string directory) =>
        Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.docx");

    public static void Save(string path, string text, DateTime started)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var doc = WordprocessingDocument.Create(temporary, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text($"Danish transcript — {started:yyyy-MM-dd HH:mm}"))),
                    new Paragraph(new Run(new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }))));
                main.Document.Save();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
