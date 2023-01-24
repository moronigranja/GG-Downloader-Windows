Added threaded downloading option (default = max = 4), saving to same file.
Mimimum size for download chunk if 100MiB, so smaller files will use less threads, regardless of thread setting.
Connection restarts from last saved byte on idle timeout.
Checks if file exists before download, and if so, verifies checksum against sfv.
Error handling on auth, and sfv download.
Added speed on download progress, and size in human readable format.
When downloaded is threaded, it was necessary to checksum the file after the complete download. However, it should be possible to calculate the CRC32 for the whole file from the checksums of the parts.
Tried to make error messages during download not fill the screen, but still be usable as a log.
