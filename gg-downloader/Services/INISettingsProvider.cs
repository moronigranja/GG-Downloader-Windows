using gg_downloader.Interfaces;
using IniParser;
using IniParser.Exceptions;
using IniParser.Model;
using System;

namespace gg_downloader.Services
{
    internal class INISettingsProvider : ISettingsProvider
    {
        private readonly FileIniDataParser _parser;
        private readonly IniData _data;
        private readonly string _fileName = AppDomain.CurrentDomain.BaseDirectory + "\\config.ini";

        public INISettingsProvider(string defaultCDNRoot)
        {
            _parser = new FileIniDataParser();
            try
            {
                _data = _parser.ReadFile(_fileName);
            }
            catch (ParsingException)
            {
                _data = new IniData
                {
                    ["Auth"] =
                    {
                        ["Username"] = string.Empty,
                        ["Password"] = string.Empty
                    },
                    ["CDN"] =
                    {
                        ["Root"] = defaultCDNRoot
                    }
                };
                _parser.WriteFile(_fileName, _data);
                _data = _parser.ReadFile(_fileName);
            }
        }

        public string UserName { 
            get => _data["Auth"]["Username"];
            set
            {
                _data["Auth"]["Username"] = value;
                _parser.WriteFile(_fileName, _data);
            }
        }
        public string Password { 
            get => _data["Auth"]["Password"];
            set
            {
                _data["Auth"]["Password"] = value;
                _parser.WriteFile(_fileName, _data);
            }
        }

        public string CDNRoot
        {
            get => _data["CDN"]["Root"];
            set
            {
                _data["CDN"]["Root"] = value;
                _parser.WriteFile(_fileName, _data);
            }
        }

        ~INISettingsProvider()
        {
            // shouldn't be necessary, but flush the settings to disk just in case
            _parser.WriteFile(_fileName,_data);
        }
    }
}
