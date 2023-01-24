using System;
using Timer = System.Timers.Timer;
using System.Collections.Generic;


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

            _totalBytesRead.Add(CurrentBytesRead);
            if (_totalBytesRead.Count > _maxDataPoints) _totalBytesRead.RemoveAt(0);

            double? dblDownloadSize = TotalDownloadSize;
            double dblBytesRead = CurrentBytesRead;
            double? progressPercentage = null;
            double currentSpeed = 0;

            //Calculate percentage
            if (dblDownloadSize.HasValue)
                progressPercentage = (double)dblBytesRead / dblDownloadSize.Value;

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
            var text = $"\r{(progressPercentage.Value * 100).ToString("F2")}% ({totalBytesDownloaded}/{totalFileSize} {unit}) {speed}      ";
            Console.Write(text);
        }

        public void Dispose()
        {
            _downloadTimer.Dispose();
        }
    }
}
