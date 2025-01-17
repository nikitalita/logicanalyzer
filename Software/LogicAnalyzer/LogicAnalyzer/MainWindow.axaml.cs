using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LogicAnalyzer.Classes;
using LogicAnalyzer.Controls;
using LogicAnalyzer.Dialogs;
using LogicAnalyzer.Extensions;
using LogicAnalyzer.Protocols;
using MessageBox.Avalonia;
using MessageBox.Avalonia.Enums;
using Newtonsoft.Json;
using SharedDriver;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace LogicAnalyzer
{
    public partial class MainWindow : Window
    {
        LogicAnalyzerDriver driver;
        CaptureSettings settings;

        ProtocolAnalyzerLoader pLoader;
        public static MainWindow? Instance { get; private set; }
        
        public MainWindow()
        {
            Instance = this;
            InitializeComponent();
            btnRefresh.Click += btnRefresh_Click;
            btnOpenClose.Click += btnOpenClose_Click;
            btnRepeat.Click += btnRepeat_Click;
            btnCapture.Click += btnCapture_Click;
            btnAbort.Click += btnAbort_Click;
            sampleMarker.RegionCreated += sampleMarker_RegionCreated;
            sampleMarker.RegionDeleted += sampleMarker_RegionDeleted;
            sampleMarker.UserMarkerSelected += sampleMarker_UserMarkerSelected;
            sampleMarker.SamplesDeleted += SampleMarker_SamplesDeleted;
            sampleMarker.MeasureSamples += SampleMarker_MeasureSamples;
            tkInScreen.PropertyChanged += tkInScreen_ValueChanged;
            scrSamplePos.Scroll += scrSamplePos_ValueChanged;
            mnuOpen.Click += mnuOpen_Click;
            mnuSave.Click += mnuSave_Click;
            mnuExit.Click += MnuExit_Click;
            mnuExport.Click += MnuExport_Click;
            mnuNetSettings.Click += MnuNetSettings_Click;
            ckMoreSamples.Checked += CkMoreSamples_Checked;
            LoadAnalyzers();
            RefreshPorts();
        }

        private async void CkMoreSamples_Checked(object? sender, RoutedEventArgs e)
        {
            if (ckMoreSamples.IsChecked ?? false)
            {
                var result = await ShowConfirm("Warning!", "Enabling this option can be very CPU intensive, are you sure you want to enable it?");

                if (!result)
                {
                    ckMoreSamples.IsChecked = false;
                    return;
                }
                tkInScreen.Maximum = 1024;
                lblMaxSamples.Text = "1024";
            }
            else
            {
                tkInScreen.Maximum = 200;
                lblMaxSamples.Text = "1024";
                if (tkInScreen.Value > 200)
                    tkInScreen.Value = 200;
            }
        }

        private async void SampleMarker_MeasureSamples(object? sender, SamplesEventArgs e)
        {
            List<byte[]> samples = new List<byte[]>();

            for (int buc = 0; buc < sampleViewer.ChannelCount; buc++)
                samples.Add(ExtractSamples(buc, sampleViewer.Samples, e.FirstSample, e.SampleCount));

            var names = channelViewer.ChannelsText.ToArray();

            for (int buc = 0; buc < names.Length; buc++)
                if (string.IsNullOrWhiteSpace(names[buc]))
                    names[buc] = (buc + 1).ToString();

            MeasureDialog dlg = new MeasureDialog();
            dlg.SetData(names, samples, settings.Frequency);
            await dlg.ShowDialog(this);

        }

        private async void MnuNetSettings_Click(object? sender, RoutedEventArgs e)
        {
            var dlg = new NetworkSettingsDialog();

            if (await dlg.ShowDialog<bool>(this))
            {
                bool res = driver.SendNetworkConfig(dlg.AccessPoint, dlg.Password, dlg.Address, dlg.Port);

                if (!res)
                    await ShowError("Error", "Error updating network settings, restart the device and try again.");
                else
                    await ShowInfo("Updated", "Network settings updated successfully.");
            }
        }

        private async void SampleMarker_SamplesDeleted(object? sender, SamplesEventArgs e)
        {
            var lastSample = e.FirstSample + e.SampleCount - 1;
            var triggerSample = sampleViewer.PreSamples - 1;

            var containsTrigger = e.FirstSample <= triggerSample && lastSample >= triggerSample;

            if(containsTrigger) 
            {
                await ShowError("Error", "Cannot delete the trigger sample.");
                return;
            }

            var preDelete = sampleViewer.Samples.Take(e.FirstSample);
            var postDelete = sampleViewer.Samples.Skip(e.FirstSample + e.SampleCount);

            var finalSamples = preDelete.Concat(postDelete).ToArray();

            var finalPreSamples = e.FirstSample > triggerSample ? sampleViewer.PreSamples : sampleViewer.PreSamples - e.SampleCount;

            var regions = sampleViewer.SelectedRegions;
            List<SelectedSampleRegion> finalRegions = new List<SelectedSampleRegion>();

            foreach(var region in regions) 
            {
                int minRegion = Math.Min(region.FirstSample, region.LastSample);
                int maxRegion = Math.Max(region.FirstSample, region.LastSample);

                if (minRegion >= e.FirstSample && maxRegion <= lastSample) //removed
                    continue;

                if (maxRegion <= e.FirstSample && maxRegion <= lastSample) //Region before delete, do not modify
                {
                    finalRegions.Add(region);
                    continue;
                }
                else if (minRegion >= e.FirstSample && minRegion >= lastSample) //Region after delete, offset n samples
                {
                    region.FirstSample -= e.SampleCount;
                    region.LastSample -= e.SampleCount;
                    finalRegions.Add(region);
                    continue;
                }
                else if (minRegion >= e.FirstSample && maxRegion > lastSample) //Begin of region cropped
                {
                    region.FirstSample = lastSample + 1;
                    region.LastSample = maxRegion;

                    if (region.LastSample - region.FirstSample < 1) //Regions smaller than 2 samples are removed
                        continue;

                    region.FirstSample -= e.SampleCount;
                    region.LastSample -= e.SampleCount;
                    finalRegions.Add(region);
                    continue;
                }
                else if (minRegion < e.FirstSample && maxRegion <= lastSample) //End of region cropped
                {
                    region.FirstSample = minRegion;
                    region.LastSample = e.FirstSample;

                    if (region.LastSample - region.FirstSample < 1) //Regions smaller than 2 samples are removed
                        continue;

                    finalRegions.Add(region);
                    continue;
                }
                else //Deleted samples are inside region (not possible, just left for sanity)
                {
                    region.LastSample -= e.SampleCount;

                    if (region.LastSample - region.FirstSample < 1) //Regions smaller than 2 samples are removed
                        continue;

                    finalRegions.Add(region);
                    continue;
                }
            }

            sampleViewer.BeginUpdate();
            sampleViewer.Samples = finalSamples;
            sampleViewer.PreSamples = finalPreSamples;
            sampleViewer.SamplesInScreen = Math.Min(100, finalSamples.Length / 10);

            if(sampleViewer.FirstSample > finalSamples.Length - 1)
                sampleViewer.FirstSample = finalSamples.Length - 1;

            sampleViewer.ClearRegions();
            sampleViewer.ClearAnalyzedChannels();

            if (finalRegions.Count > 0)
                sampleViewer.AddRegions(finalRegions);

            sampleViewer.EndUpdate();

            sampleMarker.VisibleSamples = sampleViewer.SamplesInScreen;
            sampleMarker.FirstSample = sampleViewer.FirstSample;
            sampleMarker.ClearRegions();

            if (finalRegions.Count > 0)
                sampleMarker.AddRegions(finalRegions);

            scrSamplePos.Maximum = finalSamples.Length - 1;
            scrSamplePos.Value = e.FirstSample - 1;
        }

        private void sampleMarker_UserMarkerSelected(object? sender, UserMarkerEventArgs e)
        {
            sampleViewer.BeginUpdate();

            if (sampleViewer.UserMarker != null && sampleViewer.UserMarker == e.Position)
                sampleViewer.UserMarker = null;
            else
                sampleViewer.UserMarker = e.Position;

            sampleViewer.EndUpdate();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            this.FixStartupPosition();
        }

        private async void MnuExport_Click(object? sender, RoutedEventArgs e)
        {

            var sf = new SaveFileDialog();
            {
                sf.Filters.Add(new FileDialogFilter { Name = "Comma-separated values file", Extensions = new System.Collections.Generic.List<string> { "csv" } });

                var file = await sf.ShowAsync(this);

                if (string.IsNullOrWhiteSpace(file))
                    return;

                StreamWriter sw = new StreamWriter(File.Create(file));

                StringBuilder sb = new StringBuilder();

                for (int buc = 0; buc < channelViewer.Channels.Length; buc++)
                {
                    sb.Append(string.IsNullOrWhiteSpace(channelViewer.ChannelsText[buc]) ? $"Channel {buc + 1}" : channelViewer.ChannelsText[buc]);

                    if (buc < channelViewer.Channels.Length - 1)
                        sb.Append(",");
                }

                sw.WriteLine(sb.ToString());

                for (int sample = 0; sample < sampleViewer.Samples.Length; sample++)
                {
                    sb.Clear();

                    for (int buc = 0; buc < channelViewer.Channels.Length; buc++)
                    {
                        if ((sampleViewer.Samples[sample] & (1 << buc)) == 0)
                            sb.Append("0,");
                        else
                            sb.Append("1,");
                    }

                    sb.Remove(sb.Length - 1, 1);

                    sw.WriteLine(sb.ToString());
                }

                sw.Close();
                sw.Dispose();
            }
        }

        private void MnuExit_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Driver_CaptureCompleted(object? sender, CaptureEventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                sampleViewer.BeginUpdate();
                sampleViewer.Samples = e.Samples;
                sampleViewer.PreSamples = e.PreSamples;
                sampleViewer.ChannelCount = e.ChannelCount;
                sampleViewer.SamplesInScreen = Math.Min(100, e.Samples.Length / 10);
                sampleViewer.FirstSample = Math.Max(e.PreSamples - 10, 0);
                sampleViewer.ClearRegions();
                sampleViewer.ClearAnalyzedChannels();
                sampleViewer.EndUpdate();

                scrSamplePos.Maximum = e.Samples.Length - 1;
                scrSamplePos.Value = sampleViewer.FirstSample;
                tkInScreen.Value = sampleViewer.SamplesInScreen;

                channelViewer.Channels = settings.CaptureChannels;

                sampleMarker.VisibleSamples = sampleViewer.SamplesInScreen;
                sampleMarker.FirstSample = sampleViewer.FirstSample;
                sampleMarker.ClearRegions();

                btnCapture.IsEnabled = true;
                btnRepeat.IsEnabled = true;
                btnOpenClose.IsEnabled = true;
                btnAbort.IsEnabled = false;
                mnuProtocols.IsEnabled = true;
                mnuSave.IsEnabled = true;
                mnuExport.IsEnabled = true;
                mnuSettings.IsEnabled = !driver.IsNetwork && (driver.DeviceVersion?.Contains("WIFI") ?? false);
                LoadInfo();

            });
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            LoadAnalyzers();
            RefreshPorts();
        }

        void LoadAnalyzers()
        {
            pLoader = new ProtocolAnalyzerLoader(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "analyzers"));

            var protocols = pLoader.ProtocolNames;
            mnuProtocols.Items = null;

            if (protocols.Length == 0)
                mnuProtocols.Items = new MenuItem[] { new MenuItem { Header = "<- None ->" } };
            else
            {
                List<MenuItem> finalItems = new List<MenuItem>();

                finalItems.AddRange(pLoader.ProtocolNames.Select(p =>
                {
                    var itm = new MenuItem { Header = p, Tag = p };
                    itm.Click += ProtocolAnalyzer_Click;
                    return itm;
                }).ToArray());

                var clearItem = new MenuItem { Header = "C_lear analysis data" };
                clearItem.Click += ClearItem_Click;
                finalItems.Add(clearItem);

                mnuProtocols.Items = finalItems;
            }
        }

        private void ClearItem_Click(object? sender, RoutedEventArgs e)
        {
            sampleViewer.BeginUpdate();
            sampleViewer.ClearAnalyzedChannels();
            sampleViewer.EndUpdate();
        }

        private async void ProtocolAnalyzer_Click(object? sender, RoutedEventArgs e)
        {
            var item = (sender as MenuItem)?.Tag?.ToString();

            if (item == null)
                return;

            var analyzer = pLoader.GetAnalyzer(item);

            var dlg = new ProtocolAnalyzerSettingsDialog();
            {
                dlg.Analyzer = analyzer;
                dlg.Channels = channelViewer.Channels;

                if (await dlg.ShowDialog<bool>(this) != true)
                    return;

                if (dlg.SelectedSettings == null)
                    return;

                var channels = dlg.SelectedChannels;
                var samples = sampleViewer.Samples;

                foreach (var channel in channels)
                    ExtractSamples(channel, samples);

                var analysisResult = analyzer.Analyze(settings.Frequency, settings.PreTriggerSamples - 1, dlg.SelectedSettings, channels);

                if (analysisResult != null)
                {
                    sampleViewer.BeginUpdate();
                    sampleViewer.AddAnalyzedChannels(analysisResult);
                    sampleViewer.EndUpdate();
                }
            }
        }

        private void ExtractSamples(ProtocolAnalyzerSelectedChannel channel, uint[]? samples)
        {
            if (channel == null || samples == null)
                return;

            int idx = channel.ChannelIndex;
            int mask = 1 << idx;
            channel.Samples = samples.Select(s => (s & mask) != 0 ? (byte)1 : (byte)0).ToArray();
        }

        private byte[] ExtractSamples(int channel, uint[] samples, int firstSample, int count)
        {
            int mask = 1 << channel;
            return samples.Skip(firstSample).Take(count).Select(s => (s & mask) != 0 ? (byte)1 : (byte)0).ToArray();
        }

        private async void btnOpenClose_Click(object? sender, EventArgs e)
        {
            if (driver == null)
            {
                if (ddSerialPorts.SelectedIndex == -1)
                {
                    await ShowError("Error", "Select a serial port to connect.");
                    return;
                }

                try
                {
                    if(ddSerialPorts.SelectedItem?.ToString() == "Network") 
                    {
                        NetworkDialog dlg = new NetworkDialog();
                        if (!await dlg.ShowDialog<bool>(this))
                            return;

                        driver = new LogicAnalyzerDriver(dlg.Address + ":" + dlg.Port);
                    }
                    else
                        driver = new LogicAnalyzerDriver(ddSerialPorts.SelectedItem?.ToString() ?? "", 115200);

                    driver.CaptureCompleted += Driver_CaptureCompleted;
                }
                catch(Exception ex)
                {
                    await ShowError("Error", $"Cannot connect to device ({ex.Message}).");
                    return;
                }

                lblConnectedDevice.Text = driver.DeviceVersion;
                ddSerialPorts.IsEnabled = false;
                btnRefresh.IsEnabled = false;
                btnOpenClose.Content = "Close device";
                btnCapture.IsEnabled = true;
                btnRepeat.IsEnabled = true;
                mnuSettings.IsEnabled = !driver.IsNetwork && (driver.DeviceVersion?.Contains("WIFI") ?? false);
            }
            else
            {
                driver.Dispose();
                driver = null;
                lblConnectedDevice.Text = "< None >";
                ddSerialPorts.IsEnabled = true;
                btnRefresh.IsEnabled = true;
                btnOpenClose.Content = "Open device";
                RefreshPorts();
                btnCapture.IsEnabled = false;
                btnRepeat.IsEnabled = false;
                mnuSettings.IsEnabled = false;
            }
        }

        private void btnRefresh_Click(object? sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        void RefreshPorts()
        {
            ddSerialPorts.Items = null;
            ddSerialPorts.Items = SerialPort.GetPortNames().Concat(new string[] { "Network" }).ToArray();
            
        }

        private async void btnRepeat_Click(object? sender, RoutedEventArgs e)
        {
            if (settings == null)
            {
                await ShowError("Error", "No capture to repeat");
                return;
            }

            BeginCapture();
        }

        private async void btnCapture_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new CaptureDialog();
            {
                if (!await dialog.ShowDialog<bool>(this))
                    return;

                settings = dialog.SelectedSettings;
                BeginCapture();
            }
        }

        private void btnAbort_Click(object? sender, RoutedEventArgs e)
        {
            driver.StopCapture();
            btnCapture.IsEnabled = true;
            btnRepeat.IsEnabled = true;
            btnOpenClose.IsEnabled = true;
            btnAbort.IsEnabled = false;
        }

        private async void BeginCapture()
        {

            if (settings.TriggerType != 0)
            {
                var error = driver.StartPatternCapture(settings.Frequency, settings.PreTriggerSamples, settings.PostTriggerSamples, settings.CaptureChannels, settings.TriggerChannel, settings.TriggerBitCount, settings.TriggerPattern, settings.TriggerType == 2 ? true : false, settings.CaptureMode);

                if(error != CaptureError.None)
                    await ShowError(error);
            }
            else
            {
                var error = driver.StartCapture(settings.Frequency, settings.PreTriggerSamples, settings.PostTriggerSamples, settings.CaptureChannels, settings.TriggerChannel, settings.TriggerInverted, settings.CaptureMode);

                if (error != CaptureError.None)
                {
                    await ShowError(error);
                    return;
                }
            }

            btnCapture.IsEnabled = false;
            btnRepeat.IsEnabled = false;
            btnOpenClose.IsEnabled = false;
            btnAbort.IsEnabled = true;
            mnuSettings.IsEnabled = false;
        }

        private async Task ShowError(CaptureError error)
        {
            switch (error)
            {
                case CaptureError.Busy:
                    await ShowError("Error", "Device is busy, stop the capture before starting a new one.");
                    return;
                case CaptureError.BadParams:
                    await ShowError("Error", "Specified parameters are incorrect. Check the documentation in the repository to validate them.");
                    return;
                case CaptureError.HardwareError:
                    await ShowError("Error", "Device reported error starting capture. Restart the device and try again.");
                    return;
                case CaptureError.UnexpectedError:
                    await ShowError("Error", "Unexpected error, restart the application and the device and try again.");
                    return;
            }
        }

        private void scrSamplePos_ValueChanged(object? sender, ScrollEventArgs e)
        {
            if (sampleViewer.Samples != null)
            {
                sampleViewer.BeginUpdate();
                sampleViewer.FirstSample = (int)scrSamplePos.Value;
                sampleViewer.EndUpdate();
                sampleMarker.FirstSample = sampleViewer.FirstSample;
            }
        }

        private void tkInScreen_ValueChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (sampleViewer.Samples != null)
            {
                sampleViewer.BeginUpdate();
                sampleViewer.SamplesInScreen = (int)tkInScreen.Value;
                sampleViewer.EndUpdate();
                sampleMarker.VisibleSamples = sampleViewer.SamplesInScreen;
            }
        }

        private void btnJmpTrigger_Click(object? sender, RoutedEventArgs e)
        {
            if (sampleViewer.Samples != null && settings != null)
            {
                sampleViewer.BeginUpdate();
                sampleViewer.FirstSample = (int)Math.Max(settings.PreTriggerSamples - (tkInScreen.Value / 10), 0);
                sampleViewer.EndUpdate();
                scrSamplePos.Value = sampleViewer.FirstSample;
                sampleMarker.FirstSample = sampleViewer.FirstSample;
            }
        }

        private async void mnuSave_Click(object? sender, RoutedEventArgs e)
        {
            var sf = new SaveFileDialog();
            {
                sf.Filters.Add( new FileDialogFilter { Name = "Logic analyzer captures", Extensions = new System.Collections.Generic.List<string> { "lac" } });
                var file = await sf.ShowAsync(this);

                if (string.IsNullOrWhiteSpace(file))
                    return;

                ExportedCapture ex = new ExportedCapture { Settings = settings, Samples = sampleViewer.Samples, ChannelTexts = channelViewer.ChannelsText, SelectedRegions = sampleViewer.SelectedRegions };

                File.WriteAllText(file, JsonConvert.SerializeObject(ex, new JsonConverter[] { new SelectedSampleRegion.SelectedSampleRegionConverter() }));
            }
        }

        private async void mnuOpen_Click(object? sender, RoutedEventArgs e)
        {
            var sf = new OpenFileDialog();
            {
                sf.Filters.Add(new FileDialogFilter { Name = "Logic analyzer captures", Extensions = new System.Collections.Generic.List<string> { "lac" } });

                var file = (await sf.ShowAsync(this))?.FirstOrDefault();

                if (string.IsNullOrWhiteSpace(file))
                    return;

                ExportedCapture ex = JsonConvert.DeserializeObject<ExportedCapture>(File.ReadAllText(file), new JsonConverter[] { new SelectedSampleRegion.SelectedSampleRegionConverter() });

                if (ex == null)
                    return;

                settings = ex.Settings;

                sampleViewer.BeginUpdate();
                sampleViewer.Samples = ex.Samples;
                sampleViewer.PreSamples = ex.Settings.PreTriggerSamples;
                sampleViewer.ChannelCount = ex.Settings.CaptureChannels.Length;
                sampleViewer.SamplesInScreen = Math.Min(100, ex.Samples.Length / 10);
                sampleViewer.FirstSample = Math.Max(ex.Settings.PreTriggerSamples - 10, 0);
                sampleViewer.ClearRegions();
                sampleViewer.ClearAnalyzedChannels();

                if (ex.SelectedRegions != null)
                    sampleViewer.AddRegions(ex.SelectedRegions);

                sampleViewer.EndUpdate();

                sampleMarker.VisibleSamples = sampleViewer.SamplesInScreen;
                sampleMarker.FirstSample = sampleViewer.FirstSample;
                sampleMarker.ClearRegions();

                if (ex.SelectedRegions != null)
                    sampleMarker.AddRegions(ex.SelectedRegions);

                scrSamplePos.Maximum = ex.Samples.Length - 1;
                scrSamplePos.Value = sampleViewer.FirstSample;
                tkInScreen.Value = sampleViewer.SamplesInScreen;

                channelViewer.Channels = ex.Settings.CaptureChannels;
                channelViewer.ChannelsText = ex.ChannelTexts;

                mnuSave.IsEnabled = true;
                mnuProtocols.IsEnabled = true;
                mnuExport.IsEnabled = true;
                LoadInfo();
            }
        }

        void LoadInfo()
        {

            string triggerType = settings.TriggerType == 0 ? "Edge" : (settings.TriggerType == 1 ? "Complex" : "Fast");

            lblFreq.Text = String.Format("{0:n0}", settings.Frequency) + " Hz";
            lblPreSamples.Text = String.Format("{0:n0}", settings.PreTriggerSamples);
            lblPostSamples.Text = String.Format("{0:n0}", settings.PostTriggerSamples);
            lblSamples.Text = String.Format("{0:n0}", settings.PostTriggerSamples + settings.PreTriggerSamples);
            lblChannels.Text = settings.CaptureChannels.Length.ToString();
            lblTrigger.Text = $"{triggerType}, channel {settings.TriggerChannel + 1}";
            lblValue.Text = settings.TriggerType == 0 ? (settings.TriggerInverted ? "Negative" : "Positive") : GenerateStringTrigger(settings.TriggerPattern, settings.TriggerBitCount);
        }

        private string GenerateStringTrigger(ushort triggerPattern, int bitCount)
        {
            string value = "";
            for(int buc = 0; buc < bitCount; buc++)
                value += (triggerPattern & (1 << buc)) == 0 ? "0" : "1";
            return value;
        }

        private void sampleMarker_RegionCreated(object? sender, RegionEventArgs e)
        {
            sampleViewer.BeginUpdate();
            sampleViewer.AddRegion(e.Region);
            sampleViewer.EndUpdate();
        }

        private void sampleMarker_RegionDeleted(object? sender, RegionEventArgs e)
        {
            sampleViewer.BeginUpdate();
            sampleViewer.RemoveRegion(e.Region);
            sampleViewer.EndUpdate();
        }

        private async Task ShowError(string Title, string Text)
        {
            var box = MessageBoxManager.GetMessageBoxStandardWindow(Title, Text, icon: MessageBox.Avalonia.Enums.Icon.Error);

            var prop = box.GetType().GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
            var win = prop.GetValue(box) as Window;

            win.Icon = this.Icon;
            await box.ShowDialog(this);
        }

        private async Task ShowInfo(string Title, string Text)
        {
            var box = MessageBoxManager.GetMessageBoxStandardWindow(Title, Text, icon: MessageBox.Avalonia.Enums.Icon.Info);

            var prop = box.GetType().GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
            var win = prop.GetValue(box) as Window;

            win.Icon = this.Icon;
            await box.ShowDialog(this);
        }

        private async Task<bool> ShowConfirm(string Title, string Text)
        {
            var box = MessageBoxManager.GetMessageBoxStandardWindow(Title, Text, @enum: MessageBox.Avalonia.Enums.ButtonEnum.YesNo, icon: MessageBox.Avalonia.Enums.Icon.Warning);

            var prop = box.GetType().GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic);
            var win = prop.GetValue(box) as Window;

            win.Icon = this.Icon;
            var result = await box.ShowDialog(this);

            if (result == ButtonResult.No)
                return false;

            return true;
        }
    }
}
