using System;
using System.Timers;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Output Device
        private IWavePlayer waveOut;
        // Capture Device (microphone)
        private IWaveIn capture;
        // A middle cache between Input and output
        private CacheAndSave cache;
        // UI update timer
        private Timer updateTimer;

        public MainWindow()
        {
            InitializeComponent();

            waveOut = new WasapiOut();
            capture = new WasapiCapture();
            cache = new CacheAndSave(capture.WaveFormat);

            // WaveFormat include information such as encoding, channels, sampling rate, byterates, etc;
            Box.Items.Add(string.Format("Encoding: {0}; Channels: {1}, Sampling rate: {2}, BytesRate: {3}",
                capture.WaveFormat.Encoding, capture.WaveFormat.Channels, capture.WaveFormat.SampleRate, capture.WaveFormat.AverageBytesPerSecond));

            // PCM => int (byte varies) with range (MIN_VAL, MAX_VAL) depends on byte used
            // IeeeFloat => float (4 bytes) with range (-1.0e0, 1.0e0)
            if (capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new FormatException("Format is not IeeeFloat!!");

            // handler that listen to incoming data and put into cache
            capture.DataAvailable += (o, args) => cache.Append(args.Buffer, args.BytesRecorded); 

            // Sync input and output WaveFormat;
            waveOut.Init(cache);

            Box.Items.Add("READY:");
        }

        protected override void OnClosed(EventArgs e)
        {
            // Inout Output device and cache must be disposed explicitly
            waveOut?.Dispose();
            capture?.Dispose();
            cache?.Dispose();

            updateTimer?.Dispose();
            base.OnClosed(e);
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            // Use system save file dialog
            var saveFileDialog = new SaveFileDialog();            
            saveFileDialog.Filter = "WAV File|*.wav"; // Name followed by filter

            if (saveFileDialog.ShowDialog() == true)
                FilenameText.Text = saveFileDialog.FileName;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;

            Box.Items.Add(string.Format("Encoding: {0}; Channels: {1}, Sampling rate: {2}, BytesRate: {3}", 
                capture.WaveFormat.Encoding, capture.WaveFormat.Channels, capture.WaveFormat.SampleRate, capture.WaveFormat.AverageBytesPerSecond));
            Box.Items.Add(string.Format("Saving to {0}", FilenameText.Text));
            cache.CreateWriter(FilenameText.Text);

            // Start both capture and output
            capture.StartRecording();
            waveOut.Play();

            // Print Cached count every second
            updateTimer = new Timer() { Interval = 1000 };
            updateTimer.Elapsed += (o, eArgs) =>
            {
                var posInBytes = ((IWavePosition)waveOut).GetPosition();
                var bytesPerSec = cache.WaveFormat.AverageBytesPerSecond;
                float posInSec = (float)posInBytes / (float)bytesPerSec;

                // Need to use dispatcher to update UI elements outside main thread.
                Box.Dispatcher.Invoke(() =>
                {
                    Box.Items.Add(string.Format("PositionInBytes: {0}; PositionInSecond: {1}; Cache Count: {2}", posInBytes, posInSec, cache.cache.Count));
                });
            };
            updateTimer.Start();

            StopButton.IsEnabled = true;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopButton.IsEnabled = false;
            waveOut?.Stop();
            capture?.StopRecording();
            cache?.CloseWriter();

            updateTimer?.Stop();
            updateTimer?.Dispose();
            updateTimer = null;

            StartButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///  Just cache the sample from input and use it as output
    /// </summary>
    public class Cache : ISampleProvider
    {
        public Queue<float[]> cache;
        public WaveFormat WaveFormat { get; set; }

        // In case not consuming whole float arr. Keep offset position
        private int firstOffset;
        private float[] lastSample;

        public Cache(WaveFormat format)
        {
            WaveFormat = format;
            cache = new Queue<float[]>();
            firstOffset = 0;
            lastSample = new float[WaveFormat.Channels];
            Array.Clear(lastSample, 0, lastSample.Length);
        }
        
        public virtual int Read(float[] arrOut, int offset, int count)
        {
            // arr is byte[] disguised as float[], so Array.Copy can only be used by byte[]
            // 4 bytes = 1 float => *4 for all copy operations
            var floatRead = 0;
            while (floatRead < count && cache.Count > 0)
            {
                var cacheFirst = cache.First();
                var floatsAvailable = cacheFirst.Length - firstOffset;
                var floatsToRead = count - floatRead;
                var arrOutCurPos = (offset + floatRead);

                if (floatsAvailable <= floatsToRead)
                { // Consume all data for cacheFirst
                    Buffer.BlockCopy(cacheFirst, firstOffset*4, arrOut, arrOutCurPos*4, floatsAvailable*4);
                    floatRead += floatsAvailable;
                    firstOffset = 0;
                    cache.Dequeue();
                }
                else
                { // Consume partial data for cacheFirst
                    Buffer.BlockCopy(cacheFirst, firstOffset*4, arrOut, arrOutCurPos*4, floatsToRead*4);
                    floatRead += floatsToRead;
                    firstOffset += floatsToRead;
                }
            }

            // Return at least 1 Sample to keep playing;
            if (floatRead > 0)
            {
                // Cache Last Sample
                Buffer.BlockCopy(arrOut, (floatRead-lastSample.Length)*4, lastSample, 0, lastSample.Length*4);
                return floatRead;
            }
            else
            {
                // Copy Last Sample
                Buffer.BlockCopy(lastSample, 0, arrOut, 0, lastSample.Length * 4);
                return lastSample.Length;
            }
        }

        public void Append(byte[] arr, int length)
        {
            // use float to store data
            var arrCache = new float[length / 4];
            Buffer.BlockCopy(arr, 0, arrCache, 0, length);
            cache.Enqueue(arrCache);
        }

    }

    /// <summary>
    /// Before Output to Read. Save the file to some stream
    /// </summary>
    public class CacheAndSave : Cache, IDisposable
    {
        public WaveFileWriter writer = null;

        public CacheAndSave(WaveFormat format, string path=null) : base(format)
        {
            if (path != null)
                CreateWriter(path);
        }

        // Init a writer
        public bool CreateWriter(string path)
        {
            writer?.Dispose();
            writer = new WaveFileWriter(path, WaveFormat);
            return true;
        }

        public override int Read(float[] arr, int offset, int count)
        {
            var read = base.Read(arr, offset, count);
            
            // Write to the writer
            if (writer != null)
            {
                writer.WriteSamples(arr, offset, read);
            }

            return read;
        }

        public void CloseWriter()
        {
            writer?.Dispose();
            writer = null;
        }

        public void Dispose()
        {
            writer?.Dispose();
        }
    }
}