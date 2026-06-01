namespace BidAssistantApp;

/// <summary>
/// Helper for showing file dialogs
/// </summary>
static class FileDialogHelper
{
    /// <summary>
    /// Shows a file dialog to select a Word document
    /// </summary>
    /// <returns>Selected file path, or null if cancelled</returns>
    public static string? ShowWordFileDialog()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select Word document",
                Filter = "Word macro-enabled (*.docm)|*.docm|Word documents (*.docx)|*.docx|Word 97-2003 (*.doc)|*.doc|All files (*.*)|*.*",
                FilterIndex = 1,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            // Create a temporary topmost form as dialog owner so it appears
            // in front of Chrome (otherwise it opens behind the browser)
            using var owner = new Form
            {
                TopMost = true,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                Size = Size.Empty,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000)
            };
            owner.Show();

            if (dialog.ShowDialog(owner) != DialogResult.OK)
                return null;

            // Validate selected path
            var (valid, error) = PathValidator.ValidateWordPath(dialog.FileName);
            if (!valid)
            {
                Logger.Warning($"Invalid file selected: {error}");
                MessageBox.Show(error, "Invalid File", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            Logger.Info($"Word document selected: {dialog.FileName}");
            return dialog.FileName;
        }
        catch (Exception ex)
        {
            Logger.Error("File dialog failed", ex);
            MessageBox.Show($"Failed to open file dialog: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }
}
