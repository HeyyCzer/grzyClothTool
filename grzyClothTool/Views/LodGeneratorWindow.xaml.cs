using grzyClothTool.Controls;
using grzyClothTool.Helpers;
using grzyClothTool.Optimization.Lods;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static grzyClothTool.Controls.CustomMessageBox;

namespace grzyClothTool.Views
{
    /// <summary>
    /// Lists the drawables missing Medium/Low LODs and generates them with Blender + Sollumz
    /// (same pipeline as the grzyOptimizer CLI, see <see cref="LodGenerationHelper"/>).
    /// </summary>
    public partial class LodGeneratorWindow : Window
    {
        private List<LodCandidate> _candidates = [];
        private CancellationTokenSource _cancellation;
        private bool _closeWhenDone;

        public LodGeneratorWindow()
        {
            InitializeComponent();
            Owner = MainWindow.Instance;
            LoadSettings();
            Scan();
        }

        private bool IsRunning => _cancellation != null;

        private void LoadSettings()
        {
            var settings = PersistentSettingsHelper.Instance.LodGenerator;

            BlenderPathBox.Text = settings.BlenderPath;
            SollumzFolderBox.Text = settings.SollumzFolder;
            MediumPercentBox.Text = FormatPercent(settings.MediumRatio);
            LowPercentBox.Text = FormatPercent(settings.LowRatio);
            WorkersBox.Text = settings.Workers.ToString(CultureInfo.InvariantCulture);

            switch (settings.SollumzMode)
            {
                case LodGenerationHelper.SollumzBundled:
                    SollumzBundledRadio.IsChecked = true;
                    break;
                case LodGenerationHelper.SollumzFolder:
                    SollumzFolderRadio.IsChecked = true;
                    break;
                default:
                    SollumzInstalledRadio.IsChecked = true;
                    break;
            }
            UpdateSollumzFolderVisibility();

            var detected = BlenderLocator.Find();
            BlenderDetectedText.Text = detected == null
                ? "No Blender found automatically. Install Blender 4.2+ or select blender.exe."
                : $"Auto-detected: {detected}";
        }

        /// <summary>Reads the controls into settings; null (after telling the user) when a value is invalid.</summary>
        private LodGeneratorSettings ReadSettings()
        {
            if (!TryParsePercent(MediumPercentBox.Text, out var medium) || !TryParsePercent(LowPercentBox.Text, out var low))
            {
                CustomMessageBox.Show("LOD sizes must be percentages between 1 and 99.", "LOD Generator", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
                return null;
            }

            if (!int.TryParse(WorkersBox.Text?.Trim(), out var workers) || workers < 1 || workers > 8)
            {
                CustomMessageBox.Show("Blender processes must be a number between 1 and 8.", "LOD Generator", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
                return null;
            }

            return new LodGeneratorSettings
            {
                BlenderPath = BlenderPathBox.Text?.Trim().Trim('"') ?? string.Empty,
                SollumzMode = SollumzBundledRadio.IsChecked == true ? LodGenerationHelper.SollumzBundled
                    : SollumzFolderRadio.IsChecked == true ? LodGenerationHelper.SollumzFolder
                    : LodGenerationHelper.SollumzInstalled,
                SollumzFolder = SollumzFolderBox.Text?.Trim().Trim('"') ?? string.Empty,
                MediumRatio = medium,
                LowRatio = low,
                Workers = workers
            };
        }

        private static bool TryParsePercent(string text, out double ratio)
        {
            ratio = 0;
            var value = text?.Trim().TrimEnd('%').Trim();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) &&
                !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
            {
                return false;
            }
            if (percent <= 0 || percent >= 100)
            {
                return false;
            }
            ratio = percent / 100.0;
            return true;
        }

        private static string FormatPercent(double ratio) => (ratio * 100).ToString("0.##", CultureInfo.CurrentCulture);

        private void Scan()
        {
            var result = LodGenerationHelper.Scan(MainWindow.AddonManager.Addons);

            _candidates = result.Candidates;
            CandidatesControl.ItemsSource = _candidates;

            var text = _candidates.Count == 0
                ? "No drawables with missing LODs found."
                : $"{_candidates.Count} drawable(s) are missing Medium and/or Low LODs.";
            if (result.SkippedClothPhysics > 0)
            {
                text += $" {result.SkippedClothPhysics} with cloth physics skipped.";
            }
            if (result.Pending > 0)
            {
                text += $" {result.Pending} drawable(s) still loading - rescan later.";
            }
            SubtitleText.Text = text;
            ProgressText.Text = string.Empty;

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            GenerateButton.IsEnabled = !IsRunning && _candidates.Any(c => c.IsSelected);
        }

        private void SetRunning(bool running)
        {
            SettingsPanel.IsEnabled = !running;
            SelectionButtons.IsEnabled = !running;
            CandidatesControl.IsEnabled = !running;
            CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = running;
            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var settings = ReadSettings();
            if (settings == null)
            {
                return;
            }
            PersistentSettingsHelper.Instance.LodGenerator = settings;

            LodGenerationOptions options;
            try
            {
                options = LodGenerationHelper.CreateOptions(settings);
            }
            catch (ArgumentException ex)
            {
                CustomMessageBox.Show(ex.Message, "LOD Generator", CustomMessageBoxButtons.OKOnly, CustomMessageBoxIcon.Warning);
                return;
            }

            var selected = _candidates.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (var candidate in selected)
            {
                candidate.Status = "Waiting...";
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            SetRunning(true);
            Progress.Value = 0;
            ProgressText.Text = "Starting Blender (the first run may install Sollumz dependencies)...";

            int done = 0, generated = 0, failed = 0;
            bool cancelled = false;
            bool wasPaused = SaveHelper.SavingPaused;
            SaveHelper.SavingPaused = true;
            LogHelper.Log($"LOD generator: generating LODs for {selected.Count} drawable(s)");

            try
            {
                await using var generator = new LodGenerator(options);

                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.MaxWorkers, CancellationToken = token };
                await Parallel.ForEachAsync(selected, parallelOptions, async (candidate, ct) =>
                {
                    candidate.Status = "Generating...";
                    try
                    {
                        var (relativePath, changes, notes) = await LodGenerationHelper.GenerateAsync(generator, candidate.Drawable, ct);

                        if (relativePath != null)
                        {
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                candidate.Drawable.FilePath = relativePath;
                                await candidate.Drawable.LoadDetails();
                            }).Task.Unwrap();

                            Interlocked.Increment(ref generated);
                            candidate.IsSelected = false;
                            candidate.Status = "Added " + string.Join(", ", changes.Select(c => $"{c.Level} ({c.Triangles} polys)"))
                                + (notes.Count > 0 ? $" - {string.Join("; ", notes)}" : "");
                            LogHelper.Log($"LOD generator: {candidate.DrawableName} - {candidate.Status}");
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            candidate.Status = notes.Count > 0 ? string.Join("; ", notes) : "Nothing generated";
                            LogHelper.Log($"LOD generator: {candidate.DrawableName} - {candidate.Status}", LogType.Warning);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        candidate.Status = $"Failed: {ex.Message}";
                        ErrorLogHelper.LogError($"LOD generation failed for {candidate.DrawableName}: {ex.Message}", ex);
                    }

                    int current = Interlocked.Increment(ref done);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Progress.Value = current / (double)selected.Count;
                        var versions = generator.Versions != null ? $" - {generator.Versions}" : "";
                        ProgressText.Text = $"{current}/{selected.Count} drawable(s){versions}";
                    });
                });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                SaveHelper.SavingPaused = wasPaused;
                _cancellation.Dispose();
                _cancellation = null;
            }

            foreach (var candidate in selected.Where(c => c.Status is "Waiting..." or "Generating..."))
            {
                candidate.Status = "Cancelled";
            }

            if (generated > 0)
            {
                SaveHelper.SetUnsavedChanges(true);
                TryRefreshPreview();
            }

            SetRunning(false);
            var summary = $"LODs generated for {generated} drawable(s)" +
                          (failed > 0 ? $", {failed} failed or skipped" : "") +
                          (cancelled ? " (cancelled)" : "") + ".";
            ProgressText.Text = summary;
            LogHelper.Log($"LOD generator: {summary}");

            if (_closeWhenDone)
            {
                Close();
            }
        }

        private static void TryRefreshPreview()
        {
            try
            {
                CWHelper.SendDrawableUpdateToPreview(EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogHelper.Log($"Could not refresh the preview: {ex.Message}", LogType.Warning);
            }
        }

        private void SollumzMode_Changed(object sender, RoutedEventArgs e) => UpdateSollumzFolderVisibility();

        private void UpdateSollumzFolderVisibility()
        {
            SollumzFolderBox.Visibility = SollumzFolderRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetAllSelected(bool isSelected)
        {
            foreach (var candidate in _candidates)
            {
                candidate.IsSelected = isSelected;
            }

            UpdateButtons();
        }

        private void CandidateCheckBox_Click(object sender, RoutedEventArgs e) => UpdateButtons();

        private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);

        private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

        private void Rescan_Click(object sender, RoutedEventArgs e) => Scan();

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CancelButton.IsEnabled = false;
            ProgressText.Text = "Cancelling after the drawables in progress...";
            _cancellation?.Cancel();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!IsRunning)
            {
                return;
            }

            // Blender processes must be shut down and the finished drawables updated before the window goes away.
            e.Cancel = true;
            _closeWhenDone = true;
            Cancel_Click(sender, null);
        }
    }
}
