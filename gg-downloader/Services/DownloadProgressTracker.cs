using System;
using Timer = System.Timers.Timer;
using System.Collections.Generic;
using System.Text;

//Method updateText used under MIT license from https://gist.github.com/DanielSWolf/0ab6a96899cc5377bf54

namespace gg_downloader.Services
{
    internal class DownloadProgressTracker : IDisposable
    {
        // Private properties & class variables
        private readonly Timer _downloadTimer;
        private List<long> _totalBytesRead;
        private int _maxDataPoints;
        private const double _timerInterval = 200;
        private const int _speedMeasurementInterval = 10000; //10 seconds
        private string currentText = string.Empty;

        //public properties and delegates
        public long? TotalDownloadSize { get; set; }
        public long CurrentBytesRead { get; set; }

        public DownloadProgressTracker()
        {
            _downloadTimer = new Timer(_timerInterval);
            _downloadTimer.Elapsed += updateDownloadStatus;
            _maxDataPoints = _speedMeasurementInterval / Convert.ToInt32(_timerInterval);
            _totalBytesRead = new List<long>();
        }

        public void Start()
        {
            _downloadTimer.Enabled = true;
        }

        public void Stop()
        {
            _downloadTimer.Stop();
            TriggerProgressChanged();
            Reset();
        }

        public void Reset()
        {
            TotalDownloadSize = 0;
            CurrentBytesRead = 0;
            _totalBytesRead = new List<long>();
        }

        private void updateDownloadStatus(object sender, System.Timers.ElapsedEventArgs e)
        {
            TriggerProgressChanged();
        }

        private double calculateCurrentSpeed()
        {
            double speedSums = 0;

            for (int i = 0; i < _totalBytesRead.Count - 1; i++)
            {
                speedSums += (_totalBytesRead[i + 1] - _totalBytesRead[i]);
            }

            var meanSpeed = speedSums / _totalBytesRead.Count;

            return meanSpeed * (1000 / _timerInterval);
        }

        private void TriggerProgressChanged()
        {
            // //Fix to avoid negative download speeds
            // if (_totalBytesRead.Count > 0 && CurrentBytesRead < _totalBytesRead[_totalBytesRead.Count - 1])
            //     _totalBytesRead.Clear();

            _totalBytesRead.Add(CurrentBytesRead);
            if (_totalBytesRead.Count > _maxDataPoints) _totalBytesRead.RemoveAt(0);

            // if (ProgressChanged == null)
            //     return;

            double? dblDownloadSize = TotalDownloadSize;
            double dblBytesRead = CurrentBytesRead;
            double? progressPercentage = null;
            double currentSpeed = 0;

            //Calculate percentage
            if (dblDownloadSize.HasValue)
                progressPercentage = Math.Round((double)dblBytesRead / dblDownloadSize.Value * 100, 2);

            //Calculate Speed
            currentSpeed = calculateCurrentSpeed();
            string speed = "0 bps";

            long gb = 1024 * 1024 * 1024;
            long mb = 1024 * 1024;
            long kb = 1024;
            var unit = "bytes";

            if (dblBytesRead > (gb * 0.85))
            {
                dblBytesRead = dblBytesRead / gb;
                dblDownloadSize = dblDownloadSize / gb;
                unit = "GiB";
            }
            else if (dblBytesRead > (mb * 0.85))
            {
                dblBytesRead = dblBytesRead / mb;
                dblDownloadSize = dblDownloadSize / mb;
                unit = "MiB";
            }
            else if (dblBytesRead > (kb * 0.85))
            {
                dblBytesRead = dblBytesRead / kb;
                dblDownloadSize = dblDownloadSize / kb;
                unit = "Kib";
            }

            if (currentSpeed > gb) speed = $"{Math.Round(currentSpeed / gb, 2)} GB/s";
            else if (currentSpeed > mb) speed = $"{Math.Round(currentSpeed / mb, 2)} MB/s";
            else if (currentSpeed > kb) speed = $"{Math.Round(currentSpeed / kb, 2)} KB/s";
            else speed = $"{currentSpeed} bps";

            ProgressChanged(Math.Round(dblDownloadSize.Value, 2), Math.Round(dblBytesRead, 2), progressPercentage, unit, speed);
        }

        private void ProgressChanged(double? totalFileSize, double totalBytesDownloaded, double? progressPercentage, string unit, string speed)
        {
            var text = $"\r{progressPercentage}% ({totalBytesDownloaded}/{totalFileSize} {unit}) {speed}";
            UpdateText(text);
        }

        private void UpdateText(string text)
        {
            // Get length of common portion
            int commonPrefixLength = 0;
            int commonLength = Math.Min(currentText.Length, text.Length);
            while (commonPrefixLength < commonLength && text[commonPrefixLength] == currentText[commonPrefixLength])
            {
                commonPrefixLength++;
            }

            // Backtrack to the first differing character
            StringBuilder outputBuilder = new StringBuilder();
            outputBuilder.Append('\b', currentText.Length - commonPrefixLength);

            // Output new suffix
            outputBuilder.Append(text.Substring(commonPrefixLength));

            // If the new text is shorter than the old one: delete overlapping characters
            int overlapCount = currentText.Length - text.Length;
            if (overlapCount > 0)
            {
                outputBuilder.Append(' ', overlapCount);
                outputBuilder.Append('\b', overlapCount);
            }

            Console.Write(outputBuilder);
            currentText = text;
        }

        public void Dispose()
        {
            _downloadTimer.Dispose();
        }
    }
}
