using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class LoadingProgressWindow : Window
    {
        public LoadingProgressWindow()
        {
            InitializeComponent();
        }

        internal void UpdateProgress(string fileName, FileLoadProgress progress)
        {
            FileNameTextBlock.Text = fileName;
            MessageTextBlock.Text = progress.Message;

            if (progress.Percent.HasValue)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = progress.Percent.Value;
                PercentTextBlock.Text = $"{progress.Percent.Value:0}%";
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
                PercentTextBlock.Text = string.Empty;
            }
        }
    }
}
