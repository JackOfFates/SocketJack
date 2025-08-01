using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.Logger;
using static System.Net.Mime.MediaTypeNames;

namespace HamTranscriber {

    public class Transcriber {

        static void Main(string[] args) {

        }

        public Transcriber() {
            //var whisperLogger = LogProvider.AddConsoleLogging(WhisperLogLevel.Debug);
            //waveProvider = new BufferedWaveProvider(format);
            //waveProvider.BufferLength = 44100 * 2 * 45; // 45 seconds of audio at 44100Hz, 16-bit, stereo
            processor = whisperFactory.CreateBuilder().WithLanguage("auto").SplitOnWord().Build();
            if (!File.Exists(modelFileName)) {
                DownloadModel(modelFileName, ggmlType);
            }

            //var ofd = new OpenFileDialog();
            //ofd.ShowDialog();ofd.FileName)
            FromFile("C:\\Users\\Vin\\Desktop\\untitled.wav");
            //FromMicrophoneInput();
        }

        //public UltraWideBandSpeexCodec Codec = new UltraWideBandSpeexCodec();

        private GgmlType ggmlType = GgmlType.Base;
        private string modelFileName = "C:\\inetpub\\wwwroot\\Transcriber\\ggml-Base.bin";
        private WhisperFactory whisperFactory = WhisperFactory.FromPath("C:\\inetpub\\wwwroot\\Transcriber\\ggml-Base.bin");
        private WhisperProcessor processor;


        public TimeSpan DeadSpaceInterval = TimeSpan.FromSeconds(2);
        private DateTime LastDeadSpace = DateTime.UtcNow;

        public bool IsTranscribing { get; internal set; }

        //private WaveFormat format = new WaveFormat(rate: 44100, bits: 16, channels: 2);
        //private BufferedWaveProvider waveProvider;
        public async Task<Callsign> FromFile(string filename) {
            IsTranscribing = true;
            //for (int i = -1, loopTo = WaveIn.DeviceCount - 1; i <= loopTo; i++) {
            //    WaveInCapabilities caps = WaveIn.GetCapabilities(i);
            //    string name = caps.ProductName;

            //    Console.WriteLine(i + ": " + name);
            //}
            //goBack:
            //var selectedInput = Console.ReadLine();
            // int selectedIndex = 0;
            //if (int.TryParse(selectedInput, out int deviceNumber) && deviceNumber >= -1 && deviceNumber < WaveIn.DeviceCount) {
            //    selectedIndex = deviceNumber;
            //} else {
            //    Console.WriteLine("Please type a numeric value less than or greater to the wave-in device length.");
            //    goto goBack;
            //}

            //var MicInput = new WaveInEvent() { WaveFormat = format, BufferMilliseconds = 50, DeviceNumber = selectedIndex };

            //MicInput.DataAvailable += async (sender, e) => {
            var cs = new Callsign();
            // var vol = GetVolumeLevel(e.Buffer);
            //if (vol > 10) {
            //    Console.WriteLine(vol);
            //    LastDeadSpace = DateTime.UtcNow;
            //    waveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            //} else if (DateTime.UtcNow - LastDeadSpace > DeadSpaceInterval) {
            var text = string.Empty;

            //var buff = new byte[waveProvider.BufferLength];
            //var read = waveProvider.Read(buff, 0, waveProvider.BufferLength);
            var memorystream = new MemoryStream(File.ReadAllBytes(filename));
            //var samples = ToFloat(buff, read);
            //try {
            await foreach (var result in processor.ProcessAsync(memorystream)) {
                text = text + " " + result.Text.Replace("[", string.Empty).Replace("BLANK_AUDIO", string.Empty).Replace("SILENCE", string.Empty).Replace("  ", " ").Replace("]", string.Empty).Trim();
            }

            cs.message = text;
            cs.ParseCallsign();
            IsTranscribing = false;
            return cs;
            //LastDeadSpace = DateTime.UtcNow;



            //} catch (Exception) {
            //}
        }
        //x += 1;

        //};
        //MicInput.StartRecording();
        //using var fileStream = File.OpenRead(wavFileName);

        //// This section processes the audio file and prints the results (start time, end time and text) to the console.
        //OnNewText?.Invoke(result.Text);
        //processor.Process();
        //await foreach (var result in processor.ProcessAsync(fileStream)) {
        //    OnNewText?.Invoke(result.Text);
        //}
        //}
        private int GetVolumeLevel(byte[] Buffer) {
            short[] values = new short[(int)Math.Round(Buffer.Length / 2d - 1d + 1)];
            System.Buffer.BlockCopy(Buffer, 0, values, 0, Buffer.Length);
            int percent = (int)(values.Max() / 32768d * 100d);
            return percent;
        }

        private float[] ToFloat(byte[] bytesBuffer, int read) {
            var floatSamples = new float[read / 2];
            for (int sampleIndex = 0; sampleIndex < read / 2; sampleIndex++) {
                var intSampleValue = BitConverter.ToInt16(bytesBuffer, sampleIndex * 2);
                floatSamples[sampleIndex] = (float)(intSampleValue / 32768.0);
            }
            return floatSamples;
        }
        private static async Task DownloadModel(string fileName, GgmlType ggmlType) {
            Console.WriteLine($"Downloading Model {fileName}");
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType);
            using var fileWriter = File.OpenWrite(fileName);
            await modelStream.CopyToAsync(fileWriter);
        }

    }

}
