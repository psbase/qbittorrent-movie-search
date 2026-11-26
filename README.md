# Movie Search for qBittorrent

Movie Search opens a separate Windows search window from the qBittorrent Plugins menu. It searches with the providers you already enabled. A single click on an IMDb rating opens the matched film in your default browser. Click the column heading to sort numerically. Use **Columns** to show votes, genre, runtime or the IMDb rating. Choose **Language → Deutsch** for the German interface.

## Install

1. On 64-bit Windows 10 or 11, use the official qBittorrent 5.3.0 desktop client or a compatible version with Lua plugins. Its Search tab must already work with at least one enabled provider.
2. Extract the Windows package and run **Install.cmd**. This installs MovieSearch.exe in your user profile and opens the folder containing **movie_search.lua**.
3. In qBittorrent, open **Plugins → Manage plugins**, install the Lua file and enable **Movie Search**.
4. Under **Tools → Options → Web UI**, enable the Web User Interface and bind it to **127.0.0.1** for local access. Configure a username and password. Keep authentication enabled.
5. Open **Plugins → Movie Search**. Enter a query and choose **Search**. On the first search in each session, enter your local Web UI address and credentials. The password stays in memory.

The first search window downloads the official IMDb titles and ratings in the background. The first import can take several minutes and requires temporary disk space. You can search while it runs. Later launches check at most once per 24 hours, including after a failed attempt. Unchanged datasets are skipped when IMDb provides revision headers. The last completed index remains available during an update and offline. qBittorrent 5.3.0 exposes no general application-start event to Lua, so the check starts when Movie Search opens.

Movie Search looks for a film title and release year in each torrent name. Missing, ambiguous or unrated matches show a dash. Optional German alternate titles require the larger IMDb title.akas download. Enable them under **IMDb data**. For an offline import, choose **IMDb data → Import from folder** and select a folder containing title.basics.tsv.gz, title.ratings.tsv.gz and, optionally, title.akas.tsv.gz. Search itself requires the configured providers to be reachable.

To update the application, close Movie Search and rerun Install.cmd from the new package. To uninstall, remove the plugin through qBittorrent, close all Movie Search windows, and remove its MovieSearchForQBittorrent folder under LocalAppData.

To add a result, right-click its row and choose **Add to qBittorrent**. Magnet links go directly to the Transfers list. For a provider link, qBittorrent uses the search provider to resolve it. Movie Search checks the Web UI session before submitting and renews an expired login using the credentials from the open window. If qBittorrent still refuses the request, use **Connection** to sign in again. If a request times out, check Transfers before retrying.

## Build and test

Run the following in PowerShell from this repository on Windows with .NET Framework 4.8. The script uses the Windows framework compiler and creates out/MovieSearch-Windows.zip.

```powershell
.\build.ps1 -Test
```

## License and data

The code is distributed under the MIT license. Each installation obtains IMDb data directly for personal, non-commercial use. IMDb data is subject to its own terms. This product uses data from IMDb but is not endorsed or certified by IMDb.com.

[IMDb datasets and terms](https://developer.imdb.com/non-commercial-datasets/) · [qBittorrent Web API](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29)
