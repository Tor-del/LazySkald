using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ScreenReader.App;

public partial class MainWindow
{
    private readonly ObservableCollection<TranslationCorrectionEntry> translationCorrectionEntries=new();
    private TranslationCorrectionEntry? editingTranslationCorrection;

    private void InitializeTranslationCorrections(TranslationSettings settings)
    {
        foreach (var entry in TranslationCorrections.EntriesFor(settings)) translationCorrectionEntries.Add(entry);
        TranslationCorrectionsList.ItemsSource=translationCorrectionEntries;
    }

    private void TranslationPreserveChanged(object sender,RoutedEventArgs e)
    {
        if (TranslationReplacementBox is not null)
            TranslationReplacementBox.IsEnabled=TranslationPreserveBox.IsChecked!=true;
    }

    private void AddTranslationCorrection(object sender,RoutedEventArgs e)
    {
        var entry=new TranslationCorrectionEntry { Source=TranslationSourceBox.Text.Trim(),
            Replacement=TranslationPreserveBox.IsChecked==true ? "" : TranslationReplacementBox.Text.Trim(),
            Preserve=TranslationPreserveBox.IsChecked==true,IgnoreCase=TranslationIgnoreCaseBox.IsChecked==true };
        int index=editingTranslationCorrection is null ? -1 : translationCorrectionEntries.IndexOf(editingTranslationCorrection);
        var proposed=translationCorrectionEntries.ToList();
        if (index<0) proposed.Add(entry); else proposed[index]=entry;
        try { TranslationCorrections.ValidateEntries(proposed); }
        catch (ArgumentException ex) { TranslationCorrectionError.Text=ex.Message; return; }
        if (index<0) translationCorrectionEntries.Add(entry); else translationCorrectionEntries[index]=entry;
        ResetTranslationCorrectionEditor();
        TranslationCorrectionsList.SelectedItem=entry;
        TranslationCorrectionsList.ScrollIntoView(entry);
    }

    private void EditTranslationCorrection(object sender,RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TranslationCorrectionEntry entry) return;
        editingTranslationCorrection=entry;
        TranslationSourceBox.Text=entry.Source;
        TranslationReplacementBox.Text=entry.Replacement;
        TranslationPreserveBox.IsChecked=entry.Preserve;
        TranslationIgnoreCaseBox.IsChecked=entry.IgnoreCase;
        TranslationCorrectionError.Text="";
        TranslationCorrectionAddButton.SetResourceReference(ContentControl.ContentProperty,"translation.entryApply");
        TranslationCorrectionCancelButton.Visibility=Visibility.Visible;
        TranslationSourceBox.Focus();
    }

    private void DeleteTranslationCorrection(object sender,RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TranslationCorrectionEntry entry) return;
        translationCorrectionEntries.Remove(entry);
        if (ReferenceEquals(editingTranslationCorrection,entry)) ResetTranslationCorrectionEditor();
    }

    private void CancelTranslationCorrection(object sender,RoutedEventArgs e)=>ResetTranslationCorrectionEditor();

    private void ResetTranslationCorrectionEditor()
    {
        editingTranslationCorrection=null;
        TranslationSourceBox.Clear(); TranslationReplacementBox.Clear();
        TranslationPreserveBox.IsChecked=false; TranslationIgnoreCaseBox.IsChecked=true;
        TranslationCorrectionError.Text="";
        TranslationCorrectionAddButton.SetResourceReference(ContentControl.ContentProperty,"translation.entryAdd");
        TranslationCorrectionCancelButton.Visibility=Visibility.Collapsed;
    }
}
