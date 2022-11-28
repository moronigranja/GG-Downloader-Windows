# TODO

## Planned Features

- When downloading, verify any already existing files via SFV and skip them if they match
- A better download progress bar
- Implement a centralized SFV database
- Add a SYNC command to scan executing folder for games and ensure they're up-to-date
- If the folder of the executable is non-writable, use LocalAppData instead to store settings
- Improve CDN override
- Allow specifying of a directory to download games into
- Break out key functionality into a library to be shared with the GUI.

## Possible Features
- Store settings in hidden directory off user profile - _do we want to persist data in "random" directories?_
- Localization?
- Specify a configuration file to use from command line.