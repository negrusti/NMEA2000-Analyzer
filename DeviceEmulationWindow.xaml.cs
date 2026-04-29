using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class DeviceEmulationWindow : Window, INotifyPropertyChanged
    {
        private readonly DeviceEmulationService _service;
        private string _sourceAddressText;
        private string _statusText = "Ready to emulate over PCAN.";
        private bool _isRunning;

        public DeviceEmulationWindow(DeviceEmulationPlan plan, IReadOnlyList<DeviceBusAddressOption> availableBusDevices)
        {
            _service = new DeviceEmulationService(plan);
            _service.StatusChanged += OnServiceStatusChanged;

            _sourceAddressText = plan.OriginalSourceAddress.ToString(CultureInfo.InvariantCulture);
            DestinationMappings = new ObservableCollection<DeviceDestinationMapping>(
                plan.DestinationProfiles.Select(profile => new DeviceDestinationMapping
                {
                    OriginalAddress = profile.OriginalAddress,
                    Label = profile.Label,
                    NewAddress = profile.OriginalAddress.ToString(CultureInfo.InvariantCulture)
                }));

            AvailableBusDevices = new ObservableCollection<DeviceBusAddressOption>(
                MergeAvailableAddresses(plan.DestinationProfiles, availableBusDevices));

            InitializeComponent();
            Title = $"Device Emulation - {plan.DeviceLabel}";
            DataContext = this;
            MaxHeight = SystemParameters.WorkArea.Height * 0.8;

            OriginalSourceAddress = plan.OriginalSourceAddress;
            DeviceLabel = plan.DeviceLabel;
            IdentitySummary = $"Identity frames: {plan.IdentityFrames.Count}";
            RoutineSummary = $"Routine frames: {plan.RoutineFrames.Count}";
            CycleSummary = $"Cycle: {plan.RoutineCycleLength.TotalSeconds:0.##} s";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public byte OriginalSourceAddress { get; }
        public string DeviceLabel { get; }
        public string IdentitySummary { get; }
        public string RoutineSummary { get; }
        public string CycleSummary { get; }
        public ObservableCollection<DeviceDestinationMapping> DestinationMappings { get; }
        public ObservableCollection<DeviceBusAddressOption> AvailableBusDevices { get; }

        public string SourceAddressText
        {
            get => _sourceAddressText;
            set
            {
                if (_sourceAddressText == value)
                {
                    return;
                }

                _sourceAddressText = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged();
            }
        }

        public bool CanStart => !_isRunning;
        public bool CanStop => _isRunning;

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuildMapping(out var sourceAddress, out var destinationMap, out var validationMessage))
            {
                MessageBox.Show(validationMessage, "Device Emulation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_service.Start(sourceAddress, destinationMap, out var errorMessage))
            {
                MessageBox.Show(errorMessage, "Device Emulation", MessageBoxButton.OK, MessageBoxImage.Error);
                SetRunningState(false);
                return;
            }

            SetRunningState(true);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await _service.StopAsync();
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            await CloseAfterStoppingAsync();
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_service.IsRunning)
            {
                e.Cancel = true;
                await CloseAfterStoppingAsync();
                return;
            }

            _service.StatusChanged -= OnServiceStatusChanged;
            base.OnClosing(e);
        }

        private async Task CloseAfterStoppingAsync()
        {
            await _service.StopAsync();
            _service.StatusChanged -= OnServiceStatusChanged;
            Close();
        }

        private bool TryBuildMapping(out byte sourceAddress, out Dictionary<byte, byte> destinationMap, out string validationMessage)
        {
            destinationMap = new Dictionary<byte, byte>();

            if (!byte.TryParse(SourceAddressText, NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceAddress))
            {
                validationMessage = "Enter a valid emulated source address from 0 to 255.";
                return false;
            }

            foreach (var mapping in DestinationMappings)
            {
                if (!byte.TryParse(mapping.NewAddress, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remappedAddress))
                {
                    validationMessage = $"Enter a valid new address for original destination {mapping.OriginalAddress}.";
                    return false;
                }

                destinationMap[mapping.OriginalAddress] = remappedAddress;
            }

            validationMessage = string.Empty;
            return true;
        }

        private void OnServiceStatusChanged(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText = message;
                if (message.StartsWith("Emulation stopped", StringComparison.Ordinal) ||
                    message.StartsWith("Identity packets sent", StringComparison.Ordinal))
                {
                    SetRunningState(false);
                }
            });
        }

        private void SetRunningState(bool isRunning)
        {
            if (_isRunning == isRunning)
            {
                return;
            }

            _isRunning = isRunning;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }

        private static IReadOnlyList<DeviceBusAddressOption> MergeAvailableAddresses(
            IEnumerable<DeviceDestinationProfile> destinationProfiles,
            IReadOnlyList<DeviceBusAddressOption> discoveredAddresses)
        {
            var merged = new Dictionary<string, DeviceBusAddressOption>(StringComparer.Ordinal);

            foreach (var option in discoveredAddresses)
            {
                merged[option.AddressText] = option;
            }

            foreach (var profile in destinationProfiles)
            {
                var addressText = profile.OriginalAddress.ToString(CultureInfo.InvariantCulture);
                if (!merged.ContainsKey(addressText))
                {
                    merged[addressText] = new DeviceBusAddressOption
                    {
                        AddressText = addressText,
                        Label = $"{profile.OriginalAddress} - Addr {profile.OriginalAddress}"
                    };
                }
            }

            return merged.Values
                .OrderBy(option => int.Parse(option.AddressText, CultureInfo.InvariantCulture))
                .ToList();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
