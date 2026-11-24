using System.Windows;

namespace CryptoFixer;

public partial class SavePresetDialog : Window
{
    private readonly Func<string, string?> _validateName;

    public SavePresetDialog(string suggestedName, string filePath, Func<string, string?> validateName)
    {
        InitializeComponent();
        _validateName = validateName;
        NameBox.Text = suggestedName;
        FileText.Text = $"Stored in {filePath}";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string PresetName => NameBox.Text.Trim();

    public string? PresetDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = _validateName(NameBox.Text);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
