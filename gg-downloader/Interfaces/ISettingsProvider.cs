namespace gg_downloader.Interfaces
{
    internal interface ISettingsProvider
    {
        string UserName { get; set; }
        string Password { get; set; }
        string CDNRoot { get; set; }
    }
}
