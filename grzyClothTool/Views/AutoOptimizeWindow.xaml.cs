using grzyClothTool.Controls;
using grzyClothTool.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace grzyClothTool.Views
{
    /// <summary>
    /// Lists every texture performance warning the optimizer can fix and marks the selected ones
    /// to be optimized during build. Warnings that need manual work are listed separately.
    /// </summary>
    public partial class AutoOptimizeWindow : Window
    {
        private List<TextureOptimizationCandidate> _candidates = [];

        public AutoOptimizeWindow()
        {
            InitializeComponent();
            Owner = MainWindow.Instance;
            Scan();
        }

        private void Scan()
        {
            var result = AutoOptimizer.Scan(MainWindow.AddonManager.Addons);

            _candidates = result.Candidates;
            CandidatesControl.ItemsSource = _candidates;
            ManualIssuesControl.ItemsSource = result.ManualIssues;

            ManualIssuesTitle.Text = result.ManualIssues.Count == 0
                ? "No warnings that require manual action"
                : $"Requires manual action ({result.ManualIssues.Count}) - missing LODs can be generated with \"Generate missing LODs\"";

            if (result.PendingTextures > 0)
            {
                LogHelper.Log($"Auto optimizer: {result.PendingTextures} texture(s) were still loading and were skipped. Rescan once loading finishes.", LogType.Warning);
            }

            UpdateSummary(result.PendingTextures);
        }

        private void UpdateSummary(int pendingTextures = 0)
        {
            var selected = _candidates.Where(c => c.IsSelected).ToList();

            if (_candidates.Count == 0)
            {
                SubtitleText.Text = "No texture performance warnings found.";
            }
            else
            {
                long before = selected.Sum(c => c.CurrentSizeBytes);
                long after = selected.Sum(c => c.TargetSizeBytes);

                SubtitleText.Text = $"{_candidates.Count} texture(s) can be optimized, {selected.Count} selected. " +
                                    $"Estimated memory: {FormatBytes(before)} → {FormatBytes(after)} (saves {FormatBytes(before - after)}).";
            }

            if (pendingTextures > 0)
            {
                SubtitleText.Text += $" {pendingTextures} texture(s) still loading - rescan later.";
            }

            ApplyButton.IsEnabled = selected.Count > 0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:0.#} KB";
            }

            return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        }

        private void SetAllSelected(bool isSelected)
        {
            foreach (var candidate in _candidates)
            {
                candidate.IsSelected = isSelected;
            }

            UpdateSummary();
        }

        private void CandidateCheckBox_Click(object sender, RoutedEventArgs e) => UpdateSummary();

        private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);

        private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

        private void Rescan_Click(object sender, RoutedEventArgs e) => Scan();

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var applied = AutoOptimizer.Apply(_candidates.Where(c => c.IsSelected));
            if (applied == 0)
            {
                return;
            }

            SaveHelper.SetUnsavedChanges(true);
            CustomMessageBox.Show($"{applied} texture(s) will be optimized during resource build.", "Performance Optimizer");
            Scan();
        }

        private void GenerateLods_Click(object sender, RoutedEventArgs e)
        {
            var generator = new LodGeneratorWindow { Owner = this };
            generator.ShowDialog();
            Scan();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
