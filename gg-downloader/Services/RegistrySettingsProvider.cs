using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using gg_downloader.Interfaces;
using Microsoft.Win32;

namespace gg_downloader.Services
{
    internal class RegistrySettingsProvider : ISettingsProvider
    {
        private readonly RegistryKey _key;
        public string UserName
        {
            get => _key.GetValue("Username").ToString();
            set => _key.SetValue("Username", value);
        }

        public string Password
        {
            get => _key.GetValue("Password").ToString();
            set => _key.SetValue("Password", value);
        }

        public string CDNRoot
        {
            get => _key.GetValue("CDNRoot").ToString();
            set => _key.SetValue("CDNRoot", value);
        }

        public RegistrySettingsProvider(string defaultCDNRoot)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new NotSupportedException("Registry handling only available on Windows platforms");

            _key = Registry.CurrentUser.OpenSubKey("Software\\GG-Downloader", true);
            if (_key != null) return;
            _key = Registry.CurrentUser.CreateSubKey("Software\\GG-Downloader");
            Debug.Assert(_key != null, nameof(_key) + " != null");
            _key.SetValue("Username", string.Empty);
            _key.SetValue("Password", string.Empty);
            _key.SetValue("CDNRoot", defaultCDNRoot);
        }

        ~RegistrySettingsProvider()
        {
            _key.Close();
        }

    }
}