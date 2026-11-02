using System.Globalization;
using System.Windows;

namespace NMEA2000Analyzer.Tools.Raymarine
{
    public partial class RaymarineI50CogSogSourceWindow : Window
    {
        private bool _isBusy;

        public RaymarineI50CogSogSourceWindow()
        {
            InitializeComponent();
            RefreshPayloadPreview();
        }

        private async void ProbeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            if (!TryBuildCommand(out var command, out var validationMessage))
            {
                MessageBox.Show(validationMessage, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isBusy = true;
            StatusTextBlock.Text = "Probing i50 source response...";
            try
            {
                var result = await command.ProbeAsync();
                if (result.Success)
                {
                    StatusTextBlock.Text = result.Message;
                    PayloadPreviewTextBox.Text = string.Join(
                        " ",
                        result.Payload.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                    return;
                }

                if (PCAN.IsDeviceUnavailableError(result.Message))
                {
                    MessageBox.Show(
                        "Attach the PCAN device, then try again.",
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(result.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                }

                StatusTextBlock.Text = "Probe failed.";
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
            {
                return;
            }

            if (!TryBuildCommand(out var command, out var validationMessage))
            {
                MessageBox.Show(validationMessage, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isBusy = true;
            var statusMessage = string.Empty;
            try
            {
                while (!command.TrySend(out statusMessage))
                {
                    if (PCAN.IsDeviceUnavailableError(statusMessage))
                    {
                        var result = MessageBox.Show(
                            "Attach the PCAN device, then click OK to try again.",
                            Title,
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Information);

                        if (result == MessageBoxResult.OK)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        MessageBox.Show(statusMessage, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    StatusTextBlock.Text = "Send failed.";
                    return;
                }

                StatusTextBlock.Text = statusMessage;
                RefreshPayloadPreview();
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SourceMode_Changed(object sender, RoutedEventArgs e)
        {
            if (GpsSourceAddressTextBox == null)
            {
                return;
            }

            GpsSourceAddressTextBox.IsEnabled = ManualSourceRadioButton.IsChecked == true;
            RefreshPayloadPreview();
        }

        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            RefreshPayloadPreview();
        }

        private bool TryBuildCommand(out RaymarineI50CogSogSourceCommand command, out string validationMessage)
        {
            command = new RaymarineI50CogSogSourceCommand();

            if (!TryParseAddress(DestinationAddressTextBox.Text, out var destinationAddress))
            {
                validationMessage = "Enter a valid i50 destination address from 0 to 255.";
                return false;
            }

            if (!TryParseAddress(ToolSourceAddressTextBox.Text, out var toolSourceAddress))
            {
                validationMessage = "Enter a valid tool source address from 0 to 255.";
                return false;
            }

            var mode = ManualSourceRadioButton.IsChecked == true
                ? RaymarineI50CogSogSourceMode.Manual
                : RaymarineI50CogSogSourceMode.Auto;

            byte gpsSourceAddress = 0;
            if (mode == RaymarineI50CogSogSourceMode.Manual &&
                !TryParseAddress(GpsSourceAddressTextBox.Text, out gpsSourceAddress))
            {
                validationMessage = "Enter a valid GPS source address from 0 to 255.";
                return false;
            }

            command = new RaymarineI50CogSogSourceCommand
            {
                I50DestinationAddress = destinationAddress,
                ToolSourceAddress = toolSourceAddress,
                Mode = mode,
                SelectedGpsSourceAddress = gpsSourceAddress
            };
            validationMessage = string.Empty;
            return true;
        }

        private void RefreshPayloadPreview()
        {
            if (PayloadPreviewTextBox == null)
            {
                return;
            }

            if (TryBuildCommand(out var command, out _))
            {
                PayloadPreviewTextBox.Text = RaymarineI50CogSogSourceCommand.FormatPayloads(command.BuildPayloads());
            }
            else
            {
                PayloadPreviewTextBox.Text = string.Empty;
            }
        }

        private static bool TryParseAddress(string? value, out byte address)
        {
            return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
        }
    }
}
