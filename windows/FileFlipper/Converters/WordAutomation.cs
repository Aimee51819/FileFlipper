using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace FileFlipper.Converters;

/// <summary>
/// When Microsoft Word is installed, Word documents are turned into PDFs by Word itself
/// (ExportAsFixedFormat, the same as "Save as PDF"), so the result looks exactly like printing
/// from Word. Without Word, or if Word fails, the built-in layout is used instead.
/// </summary>
public static class WordAutomation
{
    public static readonly string[] Extensions = { "docx", "docm", "doc", "dotx", "rtf", "odt" };

    private static readonly Lazy<Type?> wordType = new(() =>
    {
        try { return Type.GetTypeFromProgID("Word.Application"); } catch { return null; }
    });

    public static bool IsInstalled => wordType.Value != null;

    public static bool CanExport(string path) =>
        IsInstalled && Array.IndexOf(Extensions, FileKinds.Extension(path)) >= 0;

    /// <summary>Returns false if anything goes wrong, so the caller can fall back.</summary>
    public static bool TryExportPdf(string source, string output)
    {
        if (wordType.Value is not Type type) return false;
        bool succeeded = false;
        // Office automation needs a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try { succeeded = Export(type, Path.GetFullPath(source), Path.GetFullPath(output)); }
            catch { succeeded = false; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        // A Word dialog (e.g. a damaged file) could block forever; give up and fall back.
        if (!thread.Join(TimeSpan.FromMinutes(3))) return false;
        if (!succeeded) FileSizes.TryDelete(output);
        return succeeded && File.Exists(output);
    }

    private static bool Export(Type type, string source, string output)
    {
        object? word = null, documents = null, document = null;
        try
        {
            word = Activator.CreateInstance(type);
            if (word == null) return false;
            Set(word, "Visible", false);
            Set(word, "DisplayAlerts", 0);                  // wdAlertsNone
            documents = Get(word, "Documents");
            // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
            document = Call(documents!, "Open", source, false, true, false);
            // ExportAsFixedFormat(OutputFileName, ExportFormat: wdExportFormatPDF = 17)
            Call(document!, "ExportAsFixedFormat", output, 17);
            return true;
        }
        finally
        {
            if (document != null)
            {
                try { Call(document, "Close", 0); } catch { }  // wdDoNotSaveChanges
                Marshal.FinalReleaseComObject(document);
            }
            if (word != null)
            {
                // Leave Word running if the user has other documents open in it.
                try
                {
                    var open = Get(word, "Documents");
                    int count = open == null ? 0 : Convert.ToInt32(Get(open, "Count"));
                    if (open != null) Marshal.FinalReleaseComObject(open);
                    if (count == 0) Call(word, "Quit", 0);
                }
                catch { }
            }
            if (documents != null) Marshal.FinalReleaseComObject(documents);
            if (word != null) Marshal.FinalReleaseComObject(word);
        }
    }

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);
}
