using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private readonly ObservableCollection<OcrCorrectionEntry> ocrCorrectionEntries=new();
    private OcrCorrectionEntry? editingOcrCorrection;

    private void InitializeOcrCorrections(AppSettings settings)
    {
        foreach (var entry in OcrTextCorrections.EntriesFor(settings)) ocrCorrectionEntries.Add(entry);
        OcrCorrectionsList.ItemsSource=ocrCorrectionEntries;
    }

    private void AddOcrCorrection(object sender,RoutedEventArgs e)
    {
        var entry=new OcrCorrectionEntry { Source=OcrSourceBox.Text.Trim(),
            Replacement=OcrReplacementBox.Text.Trim(),
            IgnoreCase=OcrIgnoreCaseBox.IsChecked==true };
        int index=editingOcrCorrection is null ? -1 : ocrCorrectionEntries.IndexOf(editingOcrCorrection);
        var proposed=ocrCorrectionEntries.ToList();
        if (index<0) proposed.Add(entry); else proposed[index]=entry;
        try { OcrTextCorrections.ValidateEntries(proposed); }
        catch (ArgumentException ex) { OcrCorrectionError.Text=ex.Message; return; }
        if (index<0) ocrCorrectionEntries.Add(entry); else ocrCorrectionEntries[index]=entry;
        ResetOcrCorrectionEditor();
        OcrCorrectionsList.SelectedItem=entry;
        OcrCorrectionsList.ScrollIntoView(entry);
    }

    private void EditOcrCorrection(object sender,RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OcrCorrectionEntry entry) return;
        editingOcrCorrection=entry;
        OcrSourceBox.Text=entry.Source;
        OcrReplacementBox.Text=entry.Replacement;
        OcrIgnoreCaseBox.IsChecked=entry.IgnoreCase;
        OcrCorrectionError.Text="";
        OcrCorrectionAddButton.SetResourceReference(ContentControl.ContentProperty,"translation.entryApply");
        OcrCorrectionCancelButton.Visibility=Visibility.Visible;
        OcrSourceBox.Focus();
    }

    private void DeleteOcrCorrection(object sender,RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OcrCorrectionEntry entry) return;
        ocrCorrectionEntries.Remove(entry);
        if (ReferenceEquals(editingOcrCorrection,entry)) ResetOcrCorrectionEditor();
    }

    private void CancelOcrCorrection(object sender,RoutedEventArgs e)=>ResetOcrCorrectionEditor();

    private void ResetOcrCorrectionEditor()
    {
        editingOcrCorrection=null;
        OcrSourceBox.Clear(); OcrReplacementBox.Clear();
        OcrIgnoreCaseBox.IsChecked=false;
        OcrCorrectionError.Text="";
        OcrCorrectionAddButton.SetResourceReference(ContentControl.ContentProperty,"translation.entryAdd");
        OcrCorrectionCancelButton.Visibility=Visibility.Collapsed;
    }
}
